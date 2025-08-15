using System.Globalization;
using System.Text.Json;

namespace RtcServer;

/// <summary>Stores the configuration for the server.</summary>
/// <param name="QuicPort">The UDP port to listen on for QUIC connections.</param>
/// <param name="HttpPort">The TCP port to listen or for REST API requests.</param>
/// <param name="AuthorizationUri">The URI to call for client authorization.</param>
/// <param name="LogLevel">The minimum log level.</param>
public record Config(int QuicPort, int HttpPort, string AuthorizationUri, LogLevel LogLevel) {
	private const string ConfigFile = "config.json";

	private const string QuicPortEnv         = "RTC_SERVER_QUIC_PORT";
	private const string HttpPortEnv         = "RTC_SERVER_HTTP_PORT";
	private const string AuthorizationUriEnv = "RTC_SERVER_AUTH_URI";
	private const string LogLevelEnv         = "RTC_SERVER_LOG_LEVEL";

	public static readonly JsonSerializerOptions SerializerOptions = new() {
		IndentCharacter = '\t',
		IndentSize = 1,
		NewLine = "\n",
		WriteIndented = true
	};

	/// <summary>Writes this configuration into a json file.</summary>
	/// <param name="configFile">The json file to write the server configuration to.</param>
	public void WriteToFile(string configFile = ConfigFile) {
		File.WriteAllText(configFile, JsonSerializer.Serialize(this, SerializerOptions) + '\n');
	}

	/// <summary>Loads the server configuration either from the specified file or environment variables if it not found. Exits using <see cref="Environment.FailFast(string?)"/> on failure.</summary>
	/// <param name="configFile">The json file to load the server configuration from.</param>
	/// <returns>A newly created <see cref="Config"/>.</returns>
	/// <remarks>If the file is not found the config is loaded from <c>RTC_SERVER_QUIC_PORT</c>, <c>RTC_SERVER_HTTP_PORT</c>, <c>RTC_SERVER_AUTH_URI</c> and <c>RTC_SERVER_LOG_LEVEL</c> environment variables.</remarks>
	public static Config Load(string configFile = ConfigFile) {
		if (!File.Exists(configFile))
			return new Config(
				GetEnv<int>(QuicPortEnv),
				GetEnv<int>(HttpPortEnv),
				GetEnv(AuthorizationUriEnv),
				GetEnum<LogLevel>(LogLevelEnv)
			);

		byte[] content = File.ReadAllBytes(configFile);
		Config? config = JsonSerializer.Deserialize<Config>(content);

		if (config is null)
			Environment.FailFast($"Failed to parse '{configFile}'");
		return config;
	}

	/// <summary>Gets the value of an environment variable or exits using <see cref="Environment.FailFast(string?)"/> on failure.</summary>
	/// <param name="key">The name of the environment variable.</param>
	/// <returns>The value of the environment variable.</returns>
	private static string GetEnv(string key) {
		string? value = Environment.GetEnvironmentVariable(key);
		if (string.IsNullOrWhiteSpace(value))
			Environment.FailFast($"The environment variable '{key}' is not set");
		return value;
	}

	/// <summary>Gets and parses the value of an environment variable or exits using <see cref="Environment.FailFast(string?)"/> on failure.</summary>
	/// <param name="key">The name of the environment variable.</param>
	/// <typeparam name="T">The type to parse the value into.</typeparam>
	/// <returns>The parsed value of the environment variable.</returns>
	private static T GetEnv<T>(string key) where T : IParsable<T> {
		if (!T.TryParse(GetEnv(key), CultureInfo.InvariantCulture, out T? value))
			Environment.FailFast($"Failed to parse the value of the environment variable '{key}' into a {typeof(T)}");
		return value;
	}

	/// <summary>Gets and parses the value of an environment variable or exits using <see cref="Environment.FailFast(string?)"/> on failure.</summary>
	/// <param name="key">The name of the environment variable.</param>
	/// <typeparam name="TEnum">The enum type to parse the value into.</typeparam>
	/// <returns>The parsed value of the environment variable.</returns>
	private static TEnum GetEnum<TEnum>(string key) where TEnum : struct, Enum {
		if (!Enum.TryParse(GetEnv(key), true, out TEnum value))
			Environment.FailFast($"Failed to parse the value of the environment variable '{key}' into a {typeof(TEnum)}");
		return value;
	}
}
