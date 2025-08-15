using System.Net.Quic;
using System.Runtime.Versioning;
using System.Text;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RtcServer;

namespace RtcServerTests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public sealed class ServerTests : IAsyncLifetime {
	private bool           _allow  = true;
	private Config         _config = null!;
	private Server         _server = null!;
	private WebApplication _webApp = null!;

	private IResult Authenticate(AuthorizationRequest request) => _allow ? Results.Ok() : Results.Unauthorized();

	public Task InitializeAsync() {
		int[] ports = Helpers.GetFreePorts(3).ToArray();
		int authPort = ports[0];
		int quicPort = ports[1];
		int httpPort = ports[2];

		RtcClient.ResetNextId();

		_config = new Config(quicPort, httpPort, $"http://localhost:{authPort}/", LogLevel.Trace);

		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.Logging.SetMinimumLevel(LogLevel.Error);
		builder.WebHost.ConfigureKestrel(kso => kso.ListenAnyIP(authPort));

		_webApp = builder.Build();
		_webApp.MapPost("/", Authenticate);

		_server = new Server(_config);

		return Task.CompletedTask;
	}

	public async Task DisposeAsync() {
		_server.Dispose();
		await _webApp.DisposeAsync();
	}

	[AssertionMethod]
	private static void AssertReadResult(Helpers.ReadResult result, int id, int length, byte[] data) {
		Assert.Equal((uint)id, result.Id);
		Assert.Equal(length, result.Length);
		Assert.Equal(data, result.Data);
	}

	[AssertionMethod]
	private static void AssertReadResults(IEnumerable<Helpers.ReadResult> actual, params (int id, int length, byte[] data)[] expected) {
		HashSet<(uint id, short length, string data)> expectedValues = expected
			.Select(x => (id: (uint)x.id, length: (short)x.length, data: Encoding.UTF8.GetString(x.data)))
			.ToHashSet();

		HashSet<(uint id, short length, string data)> actualValues = actual
			.Select(x => (id: x.Id, length: x.Length, data: Encoding.UTF8.GetString(x.Data)))
			.ToHashSet();

		Assert.Equal(expectedValues, actualValues);
	}

	[Fact]
	public async Task TestServerEcho() {
		using CancellationTokenSource cts = new();

		Task webAppTask = _webApp.StartAsync(cts.Token);
		Task serverTask = _server.RunWithoutKestrelAsync(cts.Token);

		await using TestClient client = new(1, cts.Token);
		await client.ConnectAsync(_config.QuicPort, true);

		byte[] dataToSend = "Test Message"u8.ToArray();
		await client.WriteDataAsync(dataToSend);

		Helpers.ReadResult result = await client.ReadDataAsync();
		AssertReadResult(result, 0, dataToSend.Length, dataToSend);

		// Wait for all tasks to finish
		await cts.CancelAsync();
		await Task.WhenAll(serverTask, webAppTask);
	}

	[Fact]
	public async Task TestConnectionAbortionOnAuthenticationError() {
		using CancellationTokenSource cts = new();

		// Reject connections
		_allow = false;

		Task webAppTask = _webApp.StartAsync(cts.Token);
		Task serverTask = _server.RunAsync(cts.Token);

		await using TestClient client = new(1, cts.Token);
		await client.ConnectAsync(_config.QuicPort, false);

		try {
			await client.ReadDataAsync();
		}
		catch (QuicException quicException) {
			Assert.Equal(QuicError.ConnectionAborted, quicException.QuicError);
		}
		catch (Exception) {
			Assert.Fail();
		}

		// Wait for all tasks to finish
		await cts.CancelAsync();
		await Task.WhenAll(serverTask, webAppTask);
	}

	[Fact]
	public async Task TestSingleBroadcasterAndMultipleReceivers() {
		using CancellationTokenSource cts = new();

		Task webAppTask = _webApp.StartAsync(cts.Token);
		Task serverTask = _server.RunAsync(cts.Token);

		TestClient broadcaster = new(1, cts.Token);
		TestClient receiver1 = new(2, cts.Token);
		TestClient receiver2 = new(3, cts.Token);

		// Connect clients
		await broadcaster.ConnectAsync(_config.QuicPort, false);
		await receiver1.ConnectAsync(_config.QuicPort, false);
		await receiver2.ConnectAsync(_config.QuicPort, false);

		// Join clients to channel 1
		await broadcaster.JoinChannelAsync(1);
		await receiver1.JoinChannelAsync(1);
		await receiver2.JoinChannelAsync(1);

		// Wait for all clients to join channel 1
		while (true) {
			RtcClientInfo[] clientInfos = _server.GetClientInfos().Values.ToArray();
			if (clientInfos is [{ Channel: 1 }, { Channel: 1 }, { Channel: 1 }])
				break;

			await Task.Delay(10, CancellationToken.None);
		}

		byte[] dataToSend = "Test Message"u8.ToArray();

		// Make receiver 1 wait for data
		Task reader1Task = Task.Run(async () => {
			Helpers.ReadResult result = await receiver1.ReadDataAsync();
			AssertReadResult(result, 0, dataToSend.Length, dataToSend);
		}, CancellationToken.None);

		// Make receiver 2 wait for data
		Task reader2Task = Task.Run(async () => {
			Helpers.ReadResult result = await receiver2.ReadDataAsync();
			AssertReadResult(result, 0, dataToSend.Length, dataToSend);
		}, CancellationToken.None);

		// Send data from broadcaster
		await broadcaster.WriteDataAsync(dataToSend);

		// Wait for all tasks to finish
		await Task.WhenAll(reader1Task, reader2Task);
		await cts.CancelAsync();
		await Task.WhenAll(serverTask, webAppTask);

		await broadcaster.DisposeAsync();
		await receiver1.DisposeAsync();
		await receiver2.DisposeAsync();
	}

	[Fact]
	public async Task TestMultipleBroadcasters() {
		using CancellationTokenSource cts = new();

		Task webAppTask = _webApp.StartAsync(cts.Token);
		Task serverTask = _server.RunAsync(cts.Token);

		TestClient client1 = new(1, cts.Token);
		TestClient client2 = new(2, cts.Token);
		TestClient client3 = new(3, cts.Token);

		// Connect clients
		await client1.ConnectAsync(_config.QuicPort, false);
		await client2.ConnectAsync(_config.QuicPort, false);
		await client3.ConnectAsync(_config.QuicPort, false);

		// Join clients to channel 1
		await client1.JoinChannelAsync(1);
		await client2.JoinChannelAsync(1);
		await client3.JoinChannelAsync(1);

		// Wait for all clients to join channel 1
		while (true) {
			RtcClientInfo[] info = _server.GetClientInfos().Values.ToArray();
			if (info is [{ Channel: 1 }, { Channel: 1 }, { Channel: 1 }])
				break;

			await Task.Delay(10, CancellationToken.None);
		}

		byte[] dataToSend = "Test Message"u8.ToArray();

		// Make client 1 wait for data
		Task reader1Task = Task.Run(async () => {
			Helpers.ReadResult result2 = await client1.ReadDataAsync();
			Helpers.ReadResult result3 = await client1.ReadDataAsync();
			AssertReadResults([result2, result3], (1, dataToSend.Length, dataToSend), (2, dataToSend.Length, dataToSend));
		}, CancellationToken.None);

		// Make client 2 wait for data
		Task reader2Task = Task.Run(async () => {
			Helpers.ReadResult result1 = await client2.ReadDataAsync();
			Helpers.ReadResult result3 = await client2.ReadDataAsync();
			AssertReadResults([result1, result3], (0, dataToSend.Length, dataToSend), (2, dataToSend.Length, dataToSend));
		}, CancellationToken.None);

		// Make client 3 wait for data
		Task reader3Task = Task.Run(async () => {
			Helpers.ReadResult result1 = await client3.ReadDataAsync();
			Helpers.ReadResult result2 = await client3.ReadDataAsync();
			AssertReadResults([result1, result2], (0, dataToSend.Length, dataToSend), (1, dataToSend.Length, dataToSend));
		}, CancellationToken.None);

		// Send data
		await client1.WriteDataAsync(dataToSend);
		await client2.WriteDataAsync(dataToSend);
		await client3.WriteDataAsync(dataToSend);

		// Wait for all tasks to finish
		await Task.WhenAll(reader1Task, reader2Task, reader3Task);
		await cts.CancelAsync();
		await Task.WhenAll(serverTask, webAppTask);

		await client1.DisposeAsync();
		await client2.DisposeAsync();
		await client3.DisposeAsync();
	}
}
