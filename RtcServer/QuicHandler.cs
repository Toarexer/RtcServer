using System.Collections.Immutable;
using System.Net.Quic;
using System.Runtime.Versioning;

namespace RtcServer;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
internal static class QuicHandler {
	public static async Task HandleConnection(QuicConnection connection) {
		Program.Logger.LogInformation("Accepted connection from {}", connection.RemoteEndPoint);

		await using RtcClient client = new(connection, Program.GlobalToken, Program.Config.LogLevel);

		try {
			await client.WaitForControlStream();
			Program.Logger.LogInformation("Accepted control stream from {}", connection.RemoteEndPoint);

			if (client.WaitForAuthMessage() is not { } auth) {
				Program.Logger.LogWarning("Failed to authenticate connection from {}", connection.RemoteEndPoint);
				return;
			}

			AuthorizationRequest request = new(auth.Username, auth.Password, connection.RemoteEndPoint.ToString());
			HttpResponseMessage response = await Program.HttpClient.PostAsJsonAsync(Program.Config.AuthorizationUri, request, Program.GlobalToken);
			if (!response.IsSuccessStatusCode) {
				Program.Logger.LogWarning("{} failed to authorize", connection.RemoteEndPoint);
				return;
			}

			client.Alias = auth.Username;
			await client.WaitForDataStream();
			Program.Logger.LogInformation("Accepted data stream from {}", connection.RemoteEndPoint);

			Task controlTask = client.HandleControlStream(client.ChangeChannel);
			Task dataTask = client.HandleDataStream(auth.Echo ? null : client.GetOtherClients);

			await controlTask;
			await dataTask;
		}
		catch (Exception exception) {
			Program.Logger.LogError("When handling {}: ({}) {}", connection.RemoteEndPoint, exception.GetType(), exception.Message);
		}

		client.RemoveFromStore();
		Program.Logger.LogInformation("Closed connection from {}", connection.RemoteEndPoint);
	}

	private static void RemoveFromStore(this RtcClient client) {
		if (Program.Store.Remove(client))
			Program.Logger.LogDebug("Removed client of {} from the store", client.Connection.RemoteEndPoint);
	}

	private static void ChangeChannel(this RtcClient client, uint channelId) {
		if (Program.Store.Add(client, channelId))
			Program.Logger.LogDebug("Added client of {} to the store with frequency {}", client.Connection.RemoteEndPoint, channelId);
	}

	private static ImmutableHashSet<RtcClient> GetOtherClients(this RtcClient client) => Program.Store.GetClientsOnSameChannel(client);
}
