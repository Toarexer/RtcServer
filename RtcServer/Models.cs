using System.Net.Quic;
using System.Runtime.Versioning;
using JetBrains.Annotations;

namespace RtcServer;

/// <summary>The request sent to the URI defined by <see cref="Config.AuthorizationUri"/>.</summary>
/// <param name="Username">The username provided by the client.</param>
/// <param name="Password">The password provided by the client.</param>
/// <param name="RemoteAddress">The remote address of the <see cref="QuicConnection"/>.</param>
[UsedImplicitly]
internal record AuthorizationRequest(string Username, string Password, string RemoteAddress);

/// <summary>The response returned by the <c>/info/app</c> endpoint.</summary>
/// <param name="Environment">The name of the current environment.</param>
/// <param name="Version">The informational version.</param>
[UsedImplicitly]
internal record AppInfo(string Environment, string Version);

/// <summary>The dictionary item returned by the <c>/info/clients</c> endpoint.</summary>
/// <param name="Alias">The optional alias of the <see cref="RtcClient"/>.</param>
/// <param name="Channel">The current channel the client is assigned to.</param>
/// <param name="Remote">The remote address of the client's <see cref="QuicConnection"/>.</param>
[UsedImplicitly]
internal record RtcClientInfo(string? Alias, uint Channel, string Remote);

/// <summary>The response returned by the <c>/info/clients</c> endpoint. It maps <see cref="RtcClientInfo"/> items to the IDs of <see cref="RtcClient"/> instances.</summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
internal class RtcClientInfos : Dictionary<uint, RtcClientInfo> {
	public RtcClientInfos(IEnumerable<KeyValuePair<RtcClient, uint>> items) {
		foreach (KeyValuePair<RtcClient, uint> item in items)
			Add(item.Key.Id, new RtcClientInfo(item.Key.Alias, item.Value, item.Key.Connection.RemoteEndPoint.ToString()));
	}
}

/// <summary>The response returned by the <c>/info/store</c> endpoint.</summary>
/// <param name="ChannelCount">The number of stored channels.</param>
/// <param name="ClientCount">The number of stored clients.</param>
/// <param name="NextClientId">The ID to be assigned to the next <see cref="RtcClient"/>.</param>
/// <param name="UpTime">The time elapsed since the creation of the <see cref="RtcClientStore"/>.</param>
[UsedImplicitly]
internal record RtcClientStoreInfo(int ChannelCount, int ClientCount, uint NextClientId, TimeSpan UpTime);

/// <summary>The response returned by the <c>/info</c> endpoint.</summary>
/// <param name="App">The current <see cref="AppInfo"/>.</param>
/// <param name="Config">The current <see cref="RtcServer.Config"/>.</param>
/// <param name="Store">The current <see cref="RtcClientStoreInfo"/>.</param>
/// <param name="Clients">The current <see cref="RtcClientInfos"/>.</param>
[UsedImplicitly]
internal record AllInfo(AppInfo App, Config Config, RtcClientStoreInfo Store, RtcClientInfos Clients);
