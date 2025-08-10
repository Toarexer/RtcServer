using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace RtcServer;

/// <summary>Stores RTC clients and assigns them to channels.</summary>
public class RtcClientStore<TRtcClient> where TRtcClient : IRtcClient {
	private readonly ConcurrentDictionary<TRtcClient, uint> _clientToChannelId = [];

	private readonly ConcurrentDictionary<uint, ImmutableHashSet<TRtcClient>> _channelIdToClients = [];

	private readonly DateTime _creationTime = DateTime.UtcNow;

	/// <summary>Adds a <see cref="RtcClient"/> to the store. Reassigns the client if it is already part of it.</summary>
	/// <param name="client">The <see cref="RtcClient"/> to add.</param>
	/// <param name="channelId">The id of the channel to assign the <see cref="RtcClient"/> to.</param>
	/// <returns><c>false</c> if the client is already assigned the provided channel, otherwise <c>true</c>.</returns>
	public bool Add(TRtcClient client, uint channelId) {
		if (_clientToChannelId.TryGetValue(client, out uint currentId)) {
			if (currentId == channelId)
				return false;
			Remove(client);
		}

		if (_channelIdToClients.TryGetValue(channelId, out ImmutableHashSet<TRtcClient>? clients))
			_channelIdToClients[channelId] = clients.Add(client);
		else
			_channelIdToClients[channelId] = [client];

		_clientToChannelId[client] = channelId;

		return true;
	}

	/// <summary>Removes a <see cref="RtcClient"/> from the store.</summary>
	/// <param name="client">The <see cref="RtcClient"/> to remove from the store.</param>
	/// <returns><c>true</c> if the client was removed, otherwise <c>false</c>.</returns>
	public bool Remove(TRtcClient client) {
		if (!_clientToChannelId.TryRemove(client, out uint channelId) || !_channelIdToClients.TryGetValue(channelId, out ImmutableHashSet<TRtcClient>? clients))
			return false;

		_channelIdToClients[channelId] = clients.Remove(client);
		if (_channelIdToClients[channelId].IsEmpty)
			_channelIdToClients.TryRemove(channelId, out _);

		return true;
	}

	/// <summary>Removes all entries.</summary>
	public void Clear() {
		_clientToChannelId.Clear();
		_channelIdToClients.Clear();
	}

	/// <summary>Gets the clients that share a channel with the provided <see cref="RtcClient"/>.</summary>
	/// <param name="client">The <see cref="RtcClient"/> to use the channel of.</param>
	/// <param name="ignoreChannelZero">Return no clients if the channel ID is 0.</param>
	/// <returns>The clients on the same channel excluding the provided <see cref="RtcClient"/>.</returns>
	public ImmutableHashSet<TRtcClient> GetClientsOnSameChannel(TRtcClient client, bool ignoreChannelZero = true) {
		bool found = _clientToChannelId.TryGetValue(client, out uint channelId);
		bool ignore = ignoreChannelZero && channelId == 0;

		return found && !ignore && _channelIdToClients.TryGetValue(channelId, out ImmutableHashSet<TRtcClient>? clients)
			? clients.Remove(client)
			: ImmutableHashSet<TRtcClient>.Empty;
	}

	/// <summary>Returns information about stored clients.</summary>
	/// <returns>A dictionary of <see cref="RtcClientInfo"/> items where the clients' IDs are the keys.</returns>
	public RtcClientInfos<TRtcClient> GetClientInfos() => new(_clientToChannelId.AsEnumerable());

	/// <summary>Returns information about the store.</summary>
	/// <returns>An <see cref="RtcClientStoreInfo"/>.</returns>
	public RtcClientStoreInfo GetStoreInfo() => new(_channelIdToClients.Count, _clientToChannelId.Count, TRtcClient.NextId, DateTime.UtcNow - _creationTime);
}
