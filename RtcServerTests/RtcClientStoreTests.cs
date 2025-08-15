using JetBrains.Annotations;
using RtcServer;

namespace RtcServerTests;

internal sealed class DummyClient : IRtcClient {
	public uint Id => 0;

	public string? Alias => null;

	public string? Remote => null;

	public static uint NextId => 0;
}

public sealed class RtcClientStoreTests {
	private readonly RtcClientStore<DummyClient> _store = new();

	private readonly DummyClient _client1 = new();
	private readonly DummyClient _client2 = new();
	private readonly DummyClient _client3 = new();

	[AssertionMethod]
	private void AssertStoreInfo(int channelCount, int clientCount) {
		RtcClientStoreInfo info = _store.GetStoreInfo();
		Assert.Equal(channelCount, info.ChannelCount);
		Assert.Equal(clientCount, info.ClientCount);
	}

	[Fact]
	public void TestAddItemsToStore() {
		AssertStoreInfo(0, 0);

		_store.Add(_client1, 0);
		_store.Add(_client2, 0);
		_store.Add(_client3, 1);

		AssertStoreInfo(2, 3);
	}

	[Fact]
	public void TestAddExisingItemsToStore() {
		AssertStoreInfo(0, 0);

		Assert.True(_store.Add(_client1, 0));
		Assert.True(_store.Add(_client2, 0));
		Assert.True(_store.Add(_client3, 0));

		Assert.True(_store.Add(_client1, 1));
		Assert.True(_store.Add(_client2, 1));
		Assert.False(_store.Add(_client3, 0));

		AssertStoreInfo(2, 3);
	}

	[Fact]
	public void TestRemoveItemsFromStore() {
		Assert.True(_store.Add(_client1, 0));
		Assert.True(_store.Add(_client2, 0));
		Assert.True(_store.Add(_client3, 1));

		AssertStoreInfo(2, 3);

		Assert.True(_store.Remove(_client1));
		Assert.True(_store.Remove(_client2));

		AssertStoreInfo(1, 1);

		Assert.True(_store.Remove(_client3));

		AssertStoreInfo(0, 0);
	}

	[Fact]
	public void TestRemoveMissingItemsFromStore() {
		AssertStoreInfo(0, 0);

		Assert.False(_store.Remove(_client1));
		Assert.False(_store.Remove(_client2));
		Assert.False(_store.Remove(_client3));

		AssertStoreInfo(0, 0);
	}

	[Fact]
	public void TestSwitch() {
		Assert.True(_store.Add(_client1, 0));
		Assert.True(_store.Add(_client2, 0));
		Assert.True(_store.Add(_client3, 0));

		AssertStoreInfo(1, 3);

		Assert.True(_store.Add(_client1, 1));
		Assert.True(_store.Add(_client2, 2));
		Assert.True(_store.Add(_client3, 3));

		AssertStoreInfo(3, 3);

		Assert.True(_store.Add(_client1, 4));
		Assert.True(_store.Add(_client2, 4));
		Assert.True(_store.Add(_client3, 4));

		AssertStoreInfo(1, 3);
	}

	[Fact]
	public void TestClear() {
		Assert.True(_store.Add(_client1, 0));
		Assert.True(_store.Add(_client2, 0));
		Assert.True(_store.Add(_client3, 0));

		AssertStoreInfo(1, 3);

		_store.Clear();

		AssertStoreInfo(0, 0);
	}
}
