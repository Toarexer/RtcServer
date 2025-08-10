using System.Collections.Immutable;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace RtcServer;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public sealed class Server : IAsyncDisposable {
	private readonly X509Certificate2 _certificate;

	private readonly HttpClient _httpClient;

	private readonly ILogger _logger;

	private readonly RtcClientStore<RtcClient> _store;

	private int _disposed;

	private QuicListener? _listener;

	private WebApplication? _webApp;

	/// <summary>Creates a new instance of a <see cref="Server"/>.</summary>
	/// <exception cref="Exception">The server failed to initialize.</exception>
	public Server() {
		try {
			_store = new RtcClientStore<RtcClient>();
			_certificate = CertGenerator.Create();
			_httpClient = new HttpClient();

			using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole());
			_logger = loggerFactory.CreateLogger(nameof(Server));
		}
		catch (Exception) {
			_certificate?.Dispose();
			_httpClient?.Dispose();

			throw;
		}
	}

	/// <summary>Runs the server and returns a Task that only completes when the token is triggered.</summary>
	/// <param name="config">The <see cref="Config"/> to use.</param>
	/// <param name="token">The token to trigger shutdown.</param>
	/// <exception cref="PlatformNotSupportedException">The current platform is not Linux or Windows.</exception>
	public async Task RunAsync(Config config, CancellationToken token) {
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
			throw new PlatformNotSupportedException("Only Linux and Windows platforms are supported");

		SslServerAuthenticationOptions authOptions = new() {
			ApplicationProtocols = [SslApplicationProtocol.Http3],
			ServerCertificate = _certificate
		};

		QuicServerConnectionOptions connOptions = new() {
			DefaultCloseErrorCode = (long)QuicError.ConnectionAborted,
			DefaultStreamErrorCode = (long)QuicError.StreamAborted,
			IdleTimeout = TimeSpan.FromMinutes(5),
			ServerAuthenticationOptions = authOptions
		};

		_listener = await QuicListener.ListenAsync(new QuicListenerOptions {
			ApplicationProtocols = [SslApplicationProtocol.Http3],
			ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(connOptions),
			ListenEndPoint = new IPEndPoint(IPAddress.Any, config.QuicPort)
		}, token);
		_logger.LogInformation("Started QUIC listener on port {}", _listener.LocalEndPoint.Port);

		_webApp = CreateWebApplication(config);
		Task webAppTask = _webApp.RunAsync(token);

		while (true)
			try {
				QuicConnection connection = await _listener.AcceptConnectionAsync(token);
				_ = Task.Run(async () => {
					await HandleConnection(connection, config, token);
					await connection.DisposeAsync();
				}, token);
			}
			catch (OperationCanceledException) {
				break;
			}
			catch (Exception exception) {
				_logger.LogError("{}: {}", exception.GetType(), exception.Message);
			}

		await webAppTask;
	}

	/// <summary>Creates a new instance of a <see cref="WebApplication"/> and maps endpoints to it.</summary>
	/// <param name="config">The <see cref="Config"/> to use for the <see cref="WebApplicationBuilder"/>.</param>
	/// <returns>The newly created <see cref="WebApplication"/>.</returns>
	private WebApplication CreateWebApplication(Config config) {
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.Logging.SetMinimumLevel(config.LogLevel);
		builder.WebHost.ConfigureKestrel(kso => kso.ListenAnyIP(config.HttpPort));

		AppInfo appInfo = new(builder.Environment.EnvironmentName, GetCommit());

		WebApplication app = builder.Build();
		app.MapPost("/auth/allow-all", () => Results.Ok());
		app.MapGet("/info/app", () => Results.Json(appInfo, config.GlobalSerializerOptions));
		app.MapGet("/info/config", () => Results.Json(config, config.GlobalSerializerOptions));
		app.MapGet("/info/store", () => Results.Json(_store.GetStoreInfo(), config.GlobalSerializerOptions));
		app.MapGet("/info/clients", () => Results.Json(_store.GetClientInfos(), config.GlobalSerializerOptions));
		app.MapGet("/info", () => Results.Json(new AllInfo<RtcClient>(appInfo, config, _store.GetStoreInfo(), _store.GetClientInfos()), config.GlobalSerializerOptions));

		return app;
	}

	private async Task HandleConnection(QuicConnection connection, Config config, CancellationToken token) {
		_logger.LogInformation("Accepted connection from {}", connection.RemoteEndPoint);

		await using RtcClient client = new(connection, token, config.LogLevel);

		try {
			await client.WaitForControlStream();
			_logger.LogInformation("Accepted control stream from {}", connection.RemoteEndPoint);

			if (client.WaitForAuthMessage() is not { } auth) {
				_logger.LogWarning("Failed to authenticate connection from {}", connection.RemoteEndPoint);
				return;
			}

			AuthorizationRequest request = new(auth.Username, auth.Password, connection.RemoteEndPoint.ToString());
			HttpResponseMessage response = await _httpClient.PostAsJsonAsync(config.AuthorizationUri, request, token);
			if (!response.IsSuccessStatusCode) {
				_logger.LogWarning("{} failed to authorize", connection.RemoteEndPoint);
				return;
			}

			client.Alias = auth.Username;
			await client.WaitForDataStream();
			_logger.LogInformation("Accepted data stream from {}", connection.RemoteEndPoint);

			Task controlTask = client.HandleControlStream(ChangeChannel);
			Task dataTask = client.HandleDataStream(auth.Echo ? null : GetOtherClients);

			await controlTask;
			await dataTask;
		}
		catch (Exception exception) {
			_logger.LogError("When handling {}: ({}) {}", connection.RemoteEndPoint, exception.GetType(), exception.Message);
		}

		RemoveFromStore(client);
		_logger.LogInformation("Closed connection from {}", connection.RemoteEndPoint);
	}

	private void RemoveFromStore(RtcClient client) {
		if (_store.Remove(client))
			_logger.LogDebug("Removed client of {} from the store", client.Connection.RemoteEndPoint);
	}

	private void ChangeChannel(RtcClient client, uint channelId) {
		if (_store.Add(client, channelId))
			_logger.LogDebug("Added client of {} to the store with frequency {}", client.Connection.RemoteEndPoint, channelId);
	}

	private ImmutableHashSet<RtcClient> GetOtherClients(RtcClient client) => _store.GetClientsOnSameChannel(client);

	public async ValueTask DisposeAsync() {
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		if (_listener is not null)
			await _listener.DisposeAsync();
		if (_webApp is not null)
			await _webApp.DisposeAsync();

		_httpClient.Dispose();
		_certificate.Dispose();

		_store.Clear();
	}

	/// <summary>Gets the version number and the Git commit ref.</summary>
	private static string GetVersion() {
		Assembly assembly = typeof(Server).Assembly;
		AssemblyInformationalVersionAttribute? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
		return version?.InformationalVersion ?? "unknown";
	}

	/// <summary>Gets the Git commit ref without the version number.</summary>
	private static string GetCommit() => GetVersion().Split('+', 2)[^1];
}
