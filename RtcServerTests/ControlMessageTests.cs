using System.Runtime.Versioning;
using RtcServer;

namespace RtcServerTests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public class ControlMessageTests {
	[Fact]
	public async Task TestAuthMessageReader() {
		AuthenticationMessage message = new(true, "ABC", "ABC");
		using MemoryStream stream = new();

		await Helpers.WriteAuthMessageAsync(stream, message.Echo, message.Username, message.Password, CancellationToken.None);

		Assert.Equal(10, stream.Length);

		stream.Seek(0, SeekOrigin.Begin);
		byte[] buffer = new byte[stream.Length];
		await stream.ReadExactlyAsync(buffer, CancellationToken.None);

		Assert.Equal(ControlMessage.Auth, buffer[0]);
		Assert.Equal(1, buffer[1]);
		Assert.Equal(3, buffer[2]);
		Assert.Equal((byte)message.Username[0], buffer[3]);
		Assert.Equal((byte)message.Username[1], buffer[4]);
		Assert.Equal((byte)message.Username[2], buffer[5]);
		Assert.Equal(3, buffer[6]);
		Assert.Equal((byte)message.Username[0], buffer[7]);
		Assert.Equal((byte)message.Username[1], buffer[8]);
		Assert.Equal((byte)message.Username[2], buffer[9]);

		stream.Seek(0, SeekOrigin.Begin);
		stream.ReadByte();
		AuthenticationMessage result = await stream.ReadAuthMessageAsync(CancellationToken.None);

		Assert.Equal(message, result);
	}

	[Fact]
	public async Task TestChanMessageReader() {
		JoinChannelMessage message = new(123456789);
		using MemoryStream stream = new();

		await Helpers.WriteChanMessageAsync(stream, message.ChannelId, CancellationToken.None);

		Assert.Equal(5, stream.Length);

		stream.Seek(0, SeekOrigin.Begin);
		byte[] buffer = new byte[stream.Length];
		await stream.ReadExactlyAsync(buffer, CancellationToken.None);

		Assert.Equal(ControlMessage.Chan, buffer[0]);
		Assert.Equal(message.ChannelId & 0xff, buffer[1]);
		Assert.Equal((message.ChannelId >> 8) & 0xff, buffer[2]);
		Assert.Equal((message.ChannelId >> 16) & 0xff, buffer[3]);
		Assert.Equal((message.ChannelId >> 24) & 0xff, buffer[4]);

		stream.Seek(0, SeekOrigin.Begin);
		stream.ReadByte();
		JoinChannelMessage result = await stream.ReadChanMessageAsync(CancellationToken.None);

		Assert.Equal(message, result);
	}
}
