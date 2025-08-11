using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RtcServer;

internal static class Program {
	private static readonly CancellationTokenSource GlobalTokenSource;

	private static readonly CancellationToken GlobalToken;

	private static int _terminationInitiated;

	static Program() {
		GlobalTokenSource = new CancellationTokenSource();
		GlobalToken = GlobalTokenSource.Token;
	}

	[SupportedOSPlatform("linux")]
	[SupportedOSPlatform("windows")]
	private static async Task Main() {
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
			Environment.FailFast("Only Linux and Windows platforms are supported");

		PosixSignalRegistration.Create(PosixSignal.SIGINT, HandleTermination);
		PosixSignalRegistration.Create(PosixSignal.SIGTERM, HandleTermination);

		AppDomain.CurrentDomain.ProcessExit += HandleTermination;
		Console.CancelKeyPress += HandleTermination;

		Config config = Config.Load();

		using Server server = new(config);
		await server.RunAsync(GlobalToken);

		GlobalTokenSource.Dispose();
	}

	/// <summary>Cancels the <see cref="GlobalTokenSource"/> and sets <see cref="_terminationInitiated"/> to 1.</summary>
	/// <param name="context">An optional <see cref="PosixSignalContext"/> to cancel.</param>
	private static void HandleTermination(PosixSignalContext? context) {
		if (context is not null)
			context.Cancel = true;

		if (Interlocked.Exchange(ref _terminationInitiated, 1) == 0)
			GlobalTokenSource.Cancel();
	}

	/// <summary>Cancels the <see cref="GlobalTokenSource"/> and sets <see cref="_terminationInitiated"/> to 1.</summary>
	private static void HandleTermination(object? sender, EventArgs? eventArgs) {
		if (eventArgs is ConsoleCancelEventArgs cce)
			cce.Cancel = true;

		if (Interlocked.Exchange(ref _terminationInitiated, 1) == 0)
			GlobalTokenSource.Cancel();
	}
}
