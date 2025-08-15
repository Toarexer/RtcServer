using System.Buffers.Binary;
using System.Text;

namespace RtcServer;

/// <summary>Valid control message types.</summary>
public static class ControlMessage {
	/// <summary>The control message type used for authentication.</summary>
	public const byte Auth = 1;

	/// <summary>The control message type used for joining a channel.</summary>
	public const byte Chan = 2;
}

/// <summary>The interface that has to be implemented by for control messages used by the <see cref="RtcClient"/>.</summary>
public interface IControlMessage {
	/// <summary>The type of the control message.</summary>
	public byte Type { get; }
}

/// <summary>An invalid control message.</summary>
/// <param name="Type">The type of the control message.</param>
public sealed record InvalidMessage(byte Type) : IControlMessage;

/// <summary>An <see cref="IControlMessage"/> used for <see cref="RtcClient"/> authentication.</summary>
/// <param name="Echo">Echo the data received back to the client or broadcast it to other clients.</param>
/// <param name="Username">The username to use for authentication.</param>
/// <param name="Password">The password to use for authentication.</param>
public sealed record AuthenticationMessage(bool Echo, string Username, string Password) : IControlMessage {
	public byte Type => ControlMessage.Auth;

	/// <summary>The maximum size of the encoded strings not counting the size prefix itself, accepted by the server.</summary>
	public const byte MaxEncodedStringLength = byte.MaxValue;
}

/// <summary>An <see cref="IControlMessage"/> used for joining an <see cref="RtcClient"/> to a channel.</summary>
/// <param name="ChannelId">The ID of the channel to join to.</param>
public sealed record JoinChannelMessage(uint ChannelId) : IControlMessage {
	public byte Type => ControlMessage.Chan;
}

public static class ControlMessageQuicStreamExtensions {
	/// <summary>Reads a single byte from the stream.</summary>
	/// <returns>The byte read from the stream.</returns>
	public static async Task<byte> ReadByteAsync(this Stream stream, CancellationToken token) {
		byte[] buffer = new byte[sizeof(byte)];
		await stream.ReadExactlyAsync(buffer, token);
		return buffer[0];
	}

	/// <summary>Reads a single byte from the stream.</summary>
	/// <returns><c>false</c> if the read byte is 0, otherwise <c>true</c>.</returns>
	private static async Task<bool> ReadBoolAsync(this Stream stream, CancellationToken token) {
		byte value = await stream.ReadByteAsync(token);
		return value != 0;
	}

	/// <summary>Reads 4 bytes from the stream.</summary>
	/// <returns>The bytes converted to a little endian <see cref="uint"/>.</returns>
	private static async Task<uint> ReadUInt32LeAsync(this Stream stream, CancellationToken token) {
		byte[] buffer = new byte[sizeof(uint)];
		await stream.ReadExactlyAsync(buffer, token);
		return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
	}

	/// <summary>Reads a single byte from the stream, and then an additional number of bytes equal to the value of the first byte.</summary>
	/// <returns>An UTF8 string that has the maximum length of 255 when represented as a byte array.</returns>
	private static async Task<string> ReadUtf8String255Async(this Stream stream, CancellationToken token) {
		byte length = await stream.ReadByteAsync(token);
		if (length == 0)
			return "";

		byte[] buffer = new byte[length];
		await stream.ReadExactlyAsync(buffer, token);
		return Encoding.UTF8.GetString(buffer);
	}

	/// <summary>Reads an <see cref="AuthenticationMessage"/> from the stream.</summary>
	public static async Task<AuthenticationMessage> ReadAuthMessageAsync(this Stream stream, CancellationToken token) {
		bool echo = await stream.ReadBoolAsync(token);
		string username = await stream.ReadUtf8String255Async(token);
		string password = await stream.ReadUtf8String255Async(token);
		return new AuthenticationMessage(echo, username, password);
	}

	/// <summary>Reads a <see cref="JoinChannelMessage"/> from the stream.</summary>
	public static async Task<JoinChannelMessage> ReadChanMessageAsync(this Stream stream, CancellationToken token) {
		uint id = await stream.ReadUInt32LeAsync(token);
		return new JoinChannelMessage(id);
	}
}
