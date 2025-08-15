using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Text;
using RtcServer;

namespace RtcServerTests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
internal static class Helpers {
	public sealed record ReadResult(uint Id, short Length, byte[] Data);

	private sealed class NoAvailablePortException : Exception {
		public override string Message => "Failed to find a free port";
	}

	private sealed class EncodedStringTooLargeException(string name, byte maxSize = AuthenticationMessage.MaxEncodedStringLength) : Exception {
		public override string Message => $"The encoded string of '{name}' cannot be larger than {maxSize} bytes.";
	}

	private sealed class NoDataException : Exception {
		public override string Message => "The data array is 0 bytes long.";
	}

	private sealed class DataTooLargeException : Exception {
		public override string Message => $"The data array cannot be larger than {RtcClient.MaxDataSize} bytes.";
	}

	public static IEnumerable<int> GetFreePorts(int count) {
		IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
		IEnumerable<int> tcpPorts = properties.GetActiveTcpListeners().Select(x => x.Port);
		IEnumerable<int> udpPorts = properties.GetActiveUdpListeners().Select(x => x.Port);
		HashSet<int> ports = tcpPorts.Concat(udpPorts).ToHashSet();

		int added = 0;
		for (int port = 49152; port <= ushort.MaxValue; port++)
			if (ports.Add(port)) {
				yield return port;

				if (++added == count)
					yield break;
			}

		throw new NoAvailablePortException();
	}

	public static ValueTask<QuicConnection> CreateConnectionAsync(int port) => QuicConnection.ConnectAsync(new QuicClientConnectionOptions {
		ClientAuthenticationOptions = new SslClientAuthenticationOptions {
			ApplicationProtocols = [Server.Alpn],
			RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null
		},
		DefaultCloseErrorCode = (long)QuicError.ConnectionAborted,
		DefaultStreamErrorCode = (long)QuicError.StreamAborted,
		IdleTimeout = TimeSpan.FromSeconds(10),
		RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port)
	});

	public static ValueTask WriteZeroDataAsync(Stream stream, CancellationToken token) {
		byte[] buffer = new byte[sizeof(short)];
		return stream.WriteAsync(buffer, token);
	}

	public static ValueTask WriteAuthMessageAsync(Stream stream, bool echo, string username, string password, CancellationToken token) {
		int usernameSize = Encoding.UTF8.GetByteCount(username);
		if (usernameSize > AuthenticationMessage.MaxEncodedStringLength)
			throw new EncodedStringTooLargeException(nameof(username));

		int passwordSize = Encoding.UTF8.GetByteCount(password);
		if (passwordSize > AuthenticationMessage.MaxEncodedStringLength)
			throw new EncodedStringTooLargeException(nameof(password));

		byte[] buffer = new byte[sizeof(byte) + sizeof(byte) + sizeof(byte) + usernameSize + sizeof(byte) + passwordSize];
		buffer[0] = ControlMessage.Auth;
		buffer[1] = (byte)(echo ? 1 : 0);
		buffer[2] = (byte)usernameSize;
		Encoding.UTF8.GetBytes(username, 0, username.Length, buffer, 3);
		buffer[3 + usernameSize] = (byte)passwordSize;
		Encoding.UTF8.GetBytes(password, 0, password.Length, buffer, 4 + usernameSize);

		return stream.WriteAsync(buffer, token);
	}

	public static ValueTask WriteChanMessageAsync(Stream stream, uint channelId, CancellationToken token) {
		byte[] buffer = new byte[sizeof(byte) + sizeof(uint)];
		buffer[0] = ControlMessage.Chan;
		BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(sizeof(byte)), channelId);

		return stream.WriteAsync(buffer, token);
	}

	public static ValueTask WriteDataAsync(Stream stream, byte[] data, CancellationToken token) {
		switch (data.Length) {
			case 0:
				throw new NoDataException();
			case > RtcClient.MaxDataSize:
				throw new DataTooLargeException();
		}

		byte[] buffer = new byte[RtcClient.DataHeaderLengthSize + data.Length];
		BinaryPrimitives.WriteInt16LittleEndian(buffer, (short)data.Length);
		Buffer.BlockCopy(data, 0, buffer, RtcClient.DataHeaderLengthSize, data.Length);

		return stream.WriteAsync(buffer, token);
	}

	public static async Task<ReadResult> ReadDataAsync(Stream stream, CancellationToken token) {
		byte[] header = new byte[RtcClient.DataHeaderSize];
		await stream.ReadExactlyAsync(header, token);

		uint id = BinaryPrimitives.ReadUInt32LittleEndian(header);
		short length = BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(RtcClient.DataHeaderIdSize));

		byte[] data = new byte[length];
		await stream.ReadExactlyAsync(data, token);

		return new ReadResult(id, length, data);
	}
}
