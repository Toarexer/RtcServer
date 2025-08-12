using JetBrains.Annotations;
using RtcServer;

namespace RtcServerTests;

internal sealed class DummyClient : IRtcClient {
	public uint Id => 0;

	public string? Alias { get; set; }

	public string? Remote => null;

	public static uint NextId => 0;
}

public sealed class RtcClientStoreTests {
	private readonly RtcClientStore<DummyClient> _store = new();

	private readonly DummyClient _client1 = new();
	private readonly DummyClient _client2 = new();
	private readonly DummyClient _client3 = new();

	[AssertionMethod]
	private void AssertStoreCounts(int channelCount, int clientCount) {
		RtcClientStoreInfo info = _store.GetStoreInfo();
		Assert.Equal(channelCount, info.ChannelCount);
		Assert.Equal(clientCount, info.ClientCount);
	}

	[Fact]
	public void TestAdd() {
		AssertStoreCounts(0, 0);

		_store.Add(_client1, 0);
		_store.Add(_client2, 0);
		_store.Add(_client3, 1);

		AssertStoreCounts(2, 3);
	}

	[Fact]
	public void TestAddExising() {
		AssertStoreCounts(0, 0);

		_store.Add(_client1, 0);
		_store.Add(_client2, 0);
		_store.Add(_client3, 0);

		_store.Add(_client1, 1);
		_store.Add(_client2, 0);
		_store.Add(_client3, 1);

		AssertStoreCounts(2, 3);
	}

	[Fact]
	public void TestRemove() {
		_store.Add(_client1, 0);
		_store.Add(_client2, 0);
		_store.Add(_client3, 1);

		AssertStoreCounts(2, 3);

		_store.Remove(_client1);
		_store.Remove(_client2);

		AssertStoreCounts(1, 1);

		_store.Remove(_client3);

		AssertStoreCounts(0, 0);
	}

	[Fact]
	public void TestSwitch() {
		_store.Add(_client1, 0);
		_store.Add(_client2, 0);
		_store.Add(_client3, 0);

		AssertStoreCounts(1, 3);

		_store.Add(_client1, 1);
		_store.Add(_client2, 2);
		_store.Add(_client3, 3);

		AssertStoreCounts(3, 3);

		_store.Add(_client1, 4);
		_store.Add(_client2, 4);
		_store.Add(_client3, 4);

		AssertStoreCounts(1, 3);
	}

	[Fact]
	public void TestClear() {
		_store.Add(_client1, 0);
		_store.Add(_client2, 0);
		_store.Add(_client3, 0);

		AssertStoreCounts(1, 3);

		_store.Clear();

		AssertStoreCounts(0, 0);
	}
}
