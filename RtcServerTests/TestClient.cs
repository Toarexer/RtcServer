using System.Net.Quic;
using System.Runtime.Versioning;

namespace RtcServerTests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
internal sealed class TestClient(uint number, CancellationToken token) : IAsyncDisposable {
	private sealed class NotConnectedException : Exception {
		public override string Message => "Client is not connected";
	}

	private QuicConnection? _connection;

	private QuicStream? _controlStream;

	private QuicStream? _dataStream;

	public async Task ConnectAsync(int destinationPort, bool echo) {
		_connection = await Helpers.CreateConnectionAsync(destinationPort);

		_controlStream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, token);
		await Helpers.WriteAuthMessageAsync(_controlStream, echo, $"{nameof(TestClient)}{number}", "no_pass", token);

		_dataStream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
		await Helpers.WriteZeroDataAsync(_dataStream, token);
	}

	public async Task JoinChannelAsync(uint id) {
		if (_controlStream is null || _dataStream is null)
			throw new NotConnectedException();
		await Helpers.WriteChanMessageAsync(_controlStream, id, token);
		await Helpers.WriteZeroDataAsync(_dataStream, token);
	}

	public ValueTask WriteDataAsync(byte[] data) {
		if (_dataStream is null)
			throw new NotConnectedException();
		return data.Length == 0
			? Helpers.WriteZeroDataAsync(_dataStream, token)
			: Helpers.WriteDataAsync(_dataStream, data, token);
	}

	public Task<Helpers.ReadResult> ReadDataAsync() {
		if (_dataStream is null)
			throw new NotConnectedException();
		return Helpers.ReadDataAsync(_dataStream, token);
	}

	public async ValueTask DisposeAsync() {
		if (_connection is not null)
			await _connection.DisposeAsync();

		if (_controlStream is not null)
			await _controlStream.DisposeAsync();

		if (_dataStream is not null)
			await _dataStream.DisposeAsync();
	}
}
