using System.Collections.Immutable;

namespace RtcServer;

/// <summary>Stores RTC clients and assigns them to channels.</summary>
public class RtcClientStore<TRtcClient> where TRtcClient : IRtcClient {
	private readonly Dictionary<TRtcClient, uint> _clientToChannelId = [];

	private readonly Dictionary<uint, ImmutableHashSet<TRtcClient>> _channelIdToClients = [];

	private readonly DateTime _creationTime = DateTime.UtcNow;

	private readonly Lock _lock = new();

	/// <summary>Adds a <see cref="RtcClient"/> to the store. Reassigns the client if it is already part of it.</summary>
	/// <param name="client">The <see cref="RtcClient"/> to add.</param>
	/// <param name="channelId">The id of the channel to assign the <see cref="RtcClient"/> to.</param>
	/// <returns><c>false</c> if the client is already assigned the provided channel, otherwise <c>true</c>.</returns>
	public bool Add(TRtcClient client, uint channelId) {
		lock (_lock) {
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
		}

		return true;
	}

	/// <summary>Removes a <see cref="RtcClient"/> from the store.</summary>
	/// <param name="client">The <see cref="RtcClient"/> to remove from the store.</param>
	/// <returns><c>true</c> if the client was removed, otherwise <c>false</c>.</returns>
	public bool Remove(TRtcClient client) {
		lock (_lock) {
			if (!_clientToChannelId.Remove(client, out uint channelId) || !_channelIdToClients.TryGetValue(channelId, out ImmutableHashSet<TRtcClient>? clients))
				return false;

			ImmutableHashSet<TRtcClient> updated = clients.Remove(client);

			if (updated.IsEmpty)
				_channelIdToClients.Remove(channelId, out _);
			else
				_channelIdToClients[channelId] = updated;
		}

		return true;
	}

	/// <summary>Removes all entries.</summary>
	public void Clear() {
		lock (_lock) {
			_clientToChannelId.Clear();
			_channelIdToClients.Clear();
		}
	}

	/// <summary>Gets the clients that share a channel with the provided <see cref="RtcClient"/>.</summary>
	/// <param name="client">The <see cref="RtcClient"/> to use the channel of.</param>
	/// <param name="ignoreChannelZero">Return no clients if the channel ID is 0.</param>
	/// <returns>The clients on the same channel excluding the provided <see cref="RtcClient"/>.</returns>
	public ImmutableHashSet<TRtcClient> GetClientsOnSameChannel(TRtcClient client, bool ignoreChannelZero = true) {
		lock (_lock) {
			bool found = _clientToChannelId.TryGetValue(client, out uint channelId);
			bool ignore = ignoreChannelZero && channelId == 0;

			return found && !ignore && _channelIdToClients.TryGetValue(channelId, out ImmutableHashSet<TRtcClient>? clients)
				? clients
				: ImmutableHashSet<TRtcClient>.Empty;
		}
	}

	/// <summary>Returns information about stored clients.</summary>
	/// <returns>A dictionary of <see cref="RtcClientInfo"/> items where the clients' IDs are the keys.</returns>
	public RtcClientInfos<TRtcClient> GetClientInfos() {
		lock (_lock)
			return new RtcClientInfos<TRtcClient>(_clientToChannelId);
	}

	/// <summary>Returns information about the store.</summary>
	/// <returns>An <see cref="RtcClientStoreInfo"/>.</returns>
	public RtcClientStoreInfo GetStoreInfo() {
		lock (_lock)
			return new RtcClientStoreInfo(_channelIdToClients.Count, _clientToChannelId.Count, TRtcClient.NextId, DateTime.UtcNow - _creationTime);
	}
}
