using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace RtcServer;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
internal static class Program {
	public static readonly Config Config;

	public static readonly HttpClient HttpClient;

	public static readonly CancellationToken GlobalToken;

	public static readonly ILogger Logger;

	public static readonly RtcClientStore Store;

	private static readonly X509Certificate2 Certificate;

	private static readonly CancellationTokenSource GlobalTokenSource;

	private static readonly WebApplication WebApp;

	private static int _terminationInitiated;

	static Program() {
		CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

		try {
			Config = Config.Load();
			Store = new RtcClientStore();
			Certificate = CertGenerator.Create();
			HttpClient = new HttpClient();
			WebApp = CreateWebApplication();

			GlobalTokenSource = new CancellationTokenSource();
			GlobalToken = GlobalTokenSource.Token;

			using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole());
			Logger = loggerFactory.CreateLogger(nameof(RtcServer));
		}
		catch (Exception exception) {
			Certificate?.Dispose();
			GlobalTokenSource?.Dispose();
			HttpClient?.Dispose();

			Environment.FailFast("Initialization error", exception);
		}
	}

	private static async Task Main() {
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
			Environment.FailFast("Only Linux and Windows platforms are supported");

		PosixSignalRegistration.Create(PosixSignal.SIGINT, HandleTermination);
		PosixSignalRegistration.Create(PosixSignal.SIGTERM, HandleTermination);

		AppDomain.CurrentDomain.ProcessExit += (_, _) => HandleTermination(null);
		Console.CancelKeyPress += (_, cce) => {
			cce.Cancel = true;
			HandleTermination(null);
		};

		SslServerAuthenticationOptions authOptions = new() {
			ApplicationProtocols = [SslApplicationProtocol.Http3],
			ServerCertificate = Certificate
		};

		QuicServerConnectionOptions connOptions = new() {
			DefaultCloseErrorCode = (long)QuicError.ConnectionAborted,
			DefaultStreamErrorCode = (long)QuicError.StreamAborted,
			IdleTimeout = TimeSpan.FromMinutes(5),
			ServerAuthenticationOptions = authOptions
		};

		QuicListener listener = await QuicListener.ListenAsync(new QuicListenerOptions {
			ApplicationProtocols = [SslApplicationProtocol.Http3],
			ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(connOptions),
			ListenEndPoint = new IPEndPoint(IPAddress.Any, Config.QuicPort)
		});
		Logger.LogInformation("Started QUIC listener on port {}", listener.LocalEndPoint.Port);

		Task webAppTask = WebApp.RunAsync(GlobalToken);

		while (true)
			try {
				QuicConnection connection = await listener.AcceptConnectionAsync(GlobalToken);
				_ = Task.Run(async () => {
					await QuicHandler.HandleConnection(connection);
					await connection.DisposeAsync();
				});
			}
			catch (OperationCanceledException) {
				break;
			}
			catch (Exception exception) {
				Logger.LogError("{}: {}", exception.GetType(), exception.Message);
			}

		await webAppTask;

		await listener.DisposeAsync();
		await WebApp.DisposeAsync();

		HttpClient.Dispose();
		GlobalTokenSource.Dispose();
		Certificate.Dispose();
	}

	/// <summary>Gets the version number and the Git commit ref.</summary>
	private static string GetVersion() {
		Assembly assembly = typeof(Program).Assembly;
		AssemblyInformationalVersionAttribute? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
		return version?.InformationalVersion ?? "unknown";
	}

	/// <summary>Gets the Git commit ref without the version number.</summary>
	private static string GetCommit() => GetVersion().Split('+', 2)[^1];

	/// <summary>Creates a new <see cref="WebApplication"/> using the settings specified by <see cref="Config"/> and maps endpoints to it.</summary>
	private static WebApplication CreateWebApplication() {
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.Logging.SetMinimumLevel(Config.LogLevel);
		builder.WebHost.ConfigureKestrel(kso => kso.ListenAnyIP(Config.HttpPort));

		AppInfo appInfo = new(builder.Environment.EnvironmentName, GetCommit());

		WebApplication app = builder.Build();
		app.MapPost("/auth/allow-all", () => Results.Ok());
		app.MapGet("/info/app", () => Results.Json(appInfo, Config.SerializerOptions));
		app.MapGet("/info/config", () => Results.Json(Config, Config.SerializerOptions));
		app.MapGet("/info/store", () => Results.Json(Store.GetStoreInfo(), Config.SerializerOptions));
		app.MapGet("/info/clients", () => Results.Json(Store.GetClientInfos(), Config.SerializerOptions));
		app.MapGet("/info", () => Results.Json(new AllInfo(appInfo, Config, Store.GetStoreInfo(), Store.GetClientInfos()), Config.SerializerOptions));

		return app;
	}

	/// <summary>Cancels the <see cref="GlobalTokenSource"/> and sets <see cref="_terminationInitiated"/> to 1.</summary>
	/// <param name="context">An optional <see cref="PosixSignalContext"/> to cancel.</param>
	private static void HandleTermination(PosixSignalContext? context) {
		if (context is not null)
			context.Cancel = true;

		if (Interlocked.Exchange(ref _terminationInitiated, 1) != 0)
			return;

		GlobalTokenSource.Cancel();
		Logger.LogInformation("Exiting...");
	}
}
