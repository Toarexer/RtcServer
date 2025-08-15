using System.Collections.Immutable;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Console;

namespace RtcServer;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public sealed class Server : IDisposable {
	private readonly X509Certificate2 _certificate;

	private readonly Config _config;

	private readonly HttpClient _httpClient;

	private readonly ILogger _logger;

	private readonly RtcClientStore<RtcClient> _store;

	private int _disposed;

	/// <summary>The custom ALPN protocol ID used by the server: <c>qrtc/1</c></summary>
	public static readonly SslApplicationProtocol Alpn = new("qrtc/1"u8.ToArray());

	/// <summary>Creates a new instance of a <see cref="Server"/>.</summary>
	/// <param name="config">The <see cref="Config"/> to use.</param>
	/// <exception cref="Exception">The server failed to initialize.</exception>
	public Server(Config config) {
		try {
			_config = config;
			_store = new RtcClientStore<RtcClient>();
			_certificate = CertGenerator.Create();
			_httpClient = new HttpClient {
				Timeout = TimeSpan.FromSeconds(5)
			};

			using ILoggerFactory factory = LoggerFactory.Create(builder => builder
				.AddConsole(cl => cl.FormatterName = nameof(AnsiFormatter))
				.AddConsoleFormatter<AnsiFormatter, ConsoleFormatterOptions>()
				.SetMinimumLevel(config.LogLevel)
			);
			_logger = factory.CreateLogger<Server>();
		}
		catch (Exception) {
			_certificate?.Dispose();
			_httpClient?.Dispose();

			throw;
		}
	}

	/// <summary>Runs the server and returns a Task that only completes when the token is triggered.</summary>
	/// <param name="token">The token to trigger shutdown.</param>
	/// <exception cref="PlatformNotSupportedException">The current platform is not Linux or Windows.</exception>
	public async Task RunAsync(CancellationToken token) {
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
			throw new PlatformNotSupportedException("Only Linux and Windows platforms are supported");

		SslServerAuthenticationOptions authOptions = new() {
			ApplicationProtocols = [Alpn],
			ServerCertificate = _certificate
		};

		QuicServerConnectionOptions connOptions = new() {
			DefaultCloseErrorCode = (long)QuicError.ConnectionAborted,
			DefaultStreamErrorCode = (long)QuicError.StreamAborted,
			IdleTimeout = TimeSpan.FromMinutes(5),
			ServerAuthenticationOptions = authOptions
		};

		QuicListener? listener = null;
		WebApplication? webApp = null;

		try {
			listener = await QuicListener.ListenAsync(new QuicListenerOptions {
				ApplicationProtocols = authOptions.ApplicationProtocols,
				ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(connOptions),
				ListenEndPoint = new IPEndPoint(IPAddress.Any, _config.QuicPort)
			}, token);
			_logger.LogInformation("Started QUIC listener on port {Port}", listener.LocalEndPoint.Port);

			webApp = CreateWebApplication();
			Task webAppTask = webApp.RunAsync(token);
			_ = webAppTask.ContinueWith(task => _logger.LogCritical(task.Exception, "Kestrel error"), TaskContinuationOptions.OnlyOnFaulted);

			while (true)
				try {
					QuicConnection connection = await listener.AcceptConnectionAsync(token);
					_ = Task.Run(async () => {
						_logger.LogInformation("Accepted connection from {Remote}", connection.RemoteEndPoint);
						await HandleConnectionAsync(connection, token);

						await connection.CloseAsync(connOptions.DefaultCloseErrorCode, CancellationToken.None);
						await connection.DisposeAsync();
						_logger.LogInformation("Closed connection from {Remote}", connection.RemoteEndPoint);
					}, CancellationToken.None);
				}
				catch (OperationCanceledException) {
					_logger.LogInformation("Shut down QUIC listener");
					break;
				}
				catch (Exception exception) {
					_logger.LogCritical(exception, "QUIC listener error");
				}

			await webAppTask;
		}
		finally {
			if (listener is not null)
				await listener.DisposeAsync();
			if (webApp is not null)
				await webApp.DisposeAsync();
		}
	}

	/// <summary>Runs the server without the RESTful API and returns a Task that only completes when the token is triggered.</summary>
	/// <param name="token">The token to trigger shutdown.</param>
	/// <exception cref="PlatformNotSupportedException">The current platform is not Linux or Windows.</exception>
	public async Task RunWithoutKestrelAsync(CancellationToken token) {
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
			throw new PlatformNotSupportedException("Only Linux and Windows platforms are supported");

		SslServerAuthenticationOptions authOptions = new() {
			ApplicationProtocols = [Alpn],
			ServerCertificate = _certificate
		};

		QuicServerConnectionOptions connOptions = new() {
			DefaultCloseErrorCode = (long)QuicError.ConnectionAborted,
			DefaultStreamErrorCode = (long)QuicError.StreamAborted,
			IdleTimeout = TimeSpan.FromMinutes(5),
			ServerAuthenticationOptions = authOptions
		};

		QuicListener? listener = null;

		try {
			listener = await QuicListener.ListenAsync(new QuicListenerOptions {
				ApplicationProtocols = authOptions.ApplicationProtocols,
				ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(connOptions),
				ListenEndPoint = new IPEndPoint(IPAddress.Any, _config.QuicPort)
			}, token);
			_logger.LogInformation("Started QUIC listener on port {Port}", listener.LocalEndPoint.Port);

			while (true)
				try {
					QuicConnection connection = await listener.AcceptConnectionAsync(token);
					_ = Task.Run(async () => {
						_logger.LogInformation("Accepted connection from {Remote}", connection.RemoteEndPoint);
						await HandleConnectionAsync(connection, token);

						await connection.CloseAsync(connOptions.DefaultCloseErrorCode, CancellationToken.None);
						await connection.DisposeAsync();
						_logger.LogInformation("Closed connection from {Remote}", connection.RemoteEndPoint);
					}, CancellationToken.None);
				}
				catch (OperationCanceledException) {
					_logger.LogInformation("Shut down QUIC listener");
					break;
				}
				catch (Exception exception) {
					_logger.LogCritical(exception, "QUIC listener error");
				}
		}
		finally {
			if (listener is not null)
				await listener.DisposeAsync();
		}
	}

	/// <summary>Gets the <see cref="RtcClientStoreInfo"/> of the server's <see cref="RtcClientStore{TRtcClient}"/>.</summary>
	public RtcClientStoreInfo GetStoreInfo() => _store.GetStoreInfo();

	/// <summary>Gets the <see cref="RtcClientInfos{TRtcClient}"/> of the server's <see cref="RtcClientStore{TRtcClient}"/>.</summary>
	public RtcClientInfos<RtcClient> GetClientInfos() => _store.GetClientInfos();

	/// <summary>Creates a new instance of a <see cref="WebApplication"/> and maps endpoints to it.</summary>
	/// <returns>The newly created <see cref="WebApplication"/>.</returns>
	private WebApplication CreateWebApplication() {
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.Logging.AddConsole(cl => cl.FormatterName = nameof(AnsiFormatter)).AddConsoleFormatter<AnsiFormatter, ConsoleFormatterOptions>().SetMinimumLevel(_config.LogLevel);
		builder.WebHost.ConfigureKestrel(kso => kso.ListenAnyIP(_config.HttpPort));

		AppInfo appInfo = new(builder.Environment.EnvironmentName, GetCommit());

		WebApplication app = builder.Build();
		app.MapPost("/auth/allow-all", () => Results.Ok());
		app.MapGet("/info/app", () => Results.Json(appInfo, _config.GlobalSerializerOptions));
		app.MapGet("/info/config", () => Results.Json(_config, _config.GlobalSerializerOptions));
		app.MapGet("/info/store", () => Results.Json(_store.GetStoreInfo(), _config.GlobalSerializerOptions));
		app.MapGet("/info/clients", () => Results.Json(_store.GetClientInfos(), _config.GlobalSerializerOptions));
		app.MapGet("/info", () => Results.Json(new AllInfo<RtcClient>(appInfo, _config, _store.GetStoreInfo(), _store.GetClientInfos()), _config.GlobalSerializerOptions));

		return app;
	}

	private async Task HandleConnectionAsync(QuicConnection connection, CancellationToken token) {
		await using RtcClient client = new(connection, token, _config.LogLevel);

		try {
			if (!await client.WaitForControlStreamAsync()) {
				_logger.LogError("Failed to accept control stream from {Remote}", connection.RemoteEndPoint);
				return;
			}

			_logger.LogInformation("Accepted control stream from {Remote}", connection.RemoteEndPoint);

			if (await client.WaitForAuthMessageAsync() is not { } auth) {
				_logger.LogWarning("Failed to authenticate connection from {Remote}", connection.RemoteEndPoint);
				return;
			}

			AuthorizationRequest request = new(auth.Username, auth.Password, connection.RemoteEndPoint.ToString());
			using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(_config.AuthorizationUri, request, token);
			if (!response.IsSuccessStatusCode) {
				_logger.LogWarning("{Remote} failed to authorize: {StatusCode}", connection.RemoteEndPoint, response.StatusCode);
				return;
			}

			client.Alias = auth.Username;

			if (!await client.WaitForDataStreamAsync()) {
				_logger.LogError("Failed to accept data stream from {Remote}", connection.RemoteEndPoint);
				return;
			}

			_logger.LogInformation("Accepted data stream from {Remote}", connection.RemoteEndPoint);

			Task controlTask = client.HandleControlStreamAsync(ChangeChannel);
			Task dataTask = client.HandleDataStreamAsync(auth.Echo ? null : GetOtherClients);

			await Task.WhenAll(controlTask, dataTask);
		}
		catch (Exception exception) {
			_logger.LogError(exception, "Error when handling {Remote}", connection.RemoteEndPoint);
		}
		finally {
			RemoveFromStore(client);
		}
	}

	private void RemoveFromStore(RtcClient client) {
		if (_store.Remove(client))
			_logger.LogDebug("Removed client of {Remote} from the store", client.Connection.RemoteEndPoint);
	}

	private void ChangeChannel(RtcClient client, uint channelId) {
		if (_store.Add(client, channelId))
			_logger.LogDebug("Added client of {Remote} to the store with channel ID {ChannelId}", client.Connection.RemoteEndPoint, channelId);
	}

	private ImmutableHashSet<RtcClient> GetOtherClients(RtcClient client) => _store.GetClientsOnSameChannel(client);

	public void Dispose() {
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

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
