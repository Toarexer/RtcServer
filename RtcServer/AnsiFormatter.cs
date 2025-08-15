using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace RtcServer;

public class AnsiFormatter() : ConsoleFormatter(nameof(AnsiFormatter)) {
	public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter) {
		textWriter.WriteLine(logEntry.Exception is null
			? $"{GetFirstLine(logEntry)}\n                 └ {logEntry.State}"
			: $"{GetFirstLine(logEntry)}\n                 | {logEntry.State}\n                 └ {FormatException(logEntry)}"
		);
	}

	private static string GetFirstLine<TState>(in LogEntry<TState> logEntry) => $"[{DateTime.Now:HH:mm:ss}] {LevelToString(logEntry.LogLevel)} ┬ {logEntry.Category}";

	private static string LevelToString(LogLevel level) => level switch {
		LogLevel.Trace       => "\e[90mTRACE\e[0m",
		LogLevel.Debug       => "\e[32mDEBUG\e[0m",
		LogLevel.Information => "\e[34mINFO \e[0m",
		LogLevel.Warning     => "\e[93mWARN \e[0m",
		LogLevel.Error       => "\e[31mERROR\e[0m",
		LogLevel.Critical    => "\e[41mFATAL\e[0m",
		_                    => null!
	};

	private static string FormatException<TState>(in LogEntry<TState> logEntry) => logEntry.LogLevel switch {
		LogLevel.Error    => $"\e[31m{logEntry.Exception}\e[0m",
		LogLevel.Critical => $"\e[41m{logEntry.Exception}\e[0m",
		_                 => $"\e[93m{logEntry.Exception}\e[0m"
	};
}
