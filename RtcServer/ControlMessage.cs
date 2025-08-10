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
internal record InvalidMessage(byte Type) : IControlMessage;

/// <summary>An <see cref="IControlMessage"/> used for <see cref="RtcClient"/> authentication.</summary>
/// <param name="Username">The username to use for authentication.</param>
/// <param name="Password">The password to use for authentication.</param>
/// <param name="Echo">Echo the data received back to the client or broadcast it to other clients.</param>
// TODO: Use JWT authentication instead.
public record AuthenticationMessage(string Username, string Password, bool Echo) : IControlMessage {
	public byte Type => ControlMessage.Auth;
}

/// <summary>An <see cref="IControlMessage"/> used for joining an <see cref="RtcClient"/> to a channel.</summary>
/// <param name="ChannelId">The ID of the channel to join to.</param>
public record JoinChannelMessage(uint ChannelId) : IControlMessage {
	public byte Type => ControlMessage.Chan;
}
