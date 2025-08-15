# QUIC RTC Server Protocol

Custom ALPN string: `qrtc/1`

## Overview

The communication between a client and the server happen through two QUIC streams.

- A unidirectional stream for sending control messages referred to as the **control** stream.
- A bidirectional stream for sending and receiving data messages referred to as the **data** stream.

Closing either stream, or sending a malformed message, is a protocol error.
The server will close the connection, although in cases where the payload parses but is semantically invalid,
closure may be deferred.

## Connecting to the server

1. The client establishes a QUIC connection with the server with the custom ALPN and certificate validation disabled.
2. The client opens a unidirectional stream.
3. The client sends an [Authentication message](#authentication-control-message).
4. The client opens a bidirectional stream.
5. After this the client can start sending further messages and
   the server will not accept new streams from this connection.

If the server encounters any errors during this process it closes the connection.

> [!IMPORTANT]
> Currently the server generates a new certificate on each startup.
> The certificate is only meant for enabling TLS encryption and not for authenticating the server!

## Joining a channel

By default, the client is assigned to channel 0, which does not allow broadcasting.
To join another channel, the client has to send a [JoinChannel message](#joinchannel-control-message)
through the **control** stream which contains the ID of the channel it wishes to connect to.
The client **will not** receive no acknowledgement and cannot confirm the success.

## Sending data

To transmit data, the client has to send [C2S (client-to-server) messages](#c2s-data-message)
through the **data** stream.

> [!IMPORTANT]
> Since the server relays data without transcoding,
> all clients are required to use the same Opus encoder and decoder settings.

Recommended settings for good audio quality and small packet sizes:

- 48 kHz
- 16 bit mono
- 20 ms (960 samples / packet)

## Receiving data

To receive data, the client has to read from the **data** stream
and parse the incoming [S2C (server-to-client) messages](#s2c-data-message).

## Message formats

### Authentication control message

| Segment         | Length      | Description                                                                                             |
|:----------------|:------------|:--------------------------------------------------------------------------------------------------------|
| Type            | 1 byte      | The type of the message. Always `1`.                                                                    |
| Echo            | 1 byte      | If `1` the server will echo back the data received. If `0` the server will broadcast the data received. |
| Username Length | 1 byte      | The length of the UTF8 encoded username string.                                                         |
| Username        | 0-255 bytes | The UTF8 encoded username string.                                                                       |
| Password Length | 1 byte      | The length of the UTF8 encoded password string.                                                         |
| Password        | 0-255 bytes | The UTF8 encoded password string.                                                                       |

### JoinChannel control message

| Segment    | Length  | Description                                                   |
|:-----------|:--------|:--------------------------------------------------------------|
| Type       | 1 byte  | The type of the message. Always `2`.                          |
| Channel ID | 4 bytes | The little endian unsigned integer containing the channel ID. |

### C2S data message

| Segment | Length       | Description                                                  |
|:--------|:-------------|:-------------------------------------------------------------|
| Length  | 2 byte       | The little endian 2 byte integer length of the next segment. |
| Data    | 0-1275 bytes | The Opus encoded data.                                       |

### S2C data message

| Segment   | Length       | Description                                                                                                     |
|:----------|:-------------|:----------------------------------------------------------------------------------------------------------------|
| Sender ID | 4 byte       | The little endian 4 byte unsigned integer containing the server assigned ID of the client who sent the message. |
| Length    | 2 byte       | The little endian 2 byte integer length of the next segment.                                                    |
| Data      | 0-1275 bytes | The Opus encoded data.                                                                                          |

> [!TIP]
> The message handling done in [RtcServerTests/Helpers.cs](./RtcServerTests/Helpers.cs) and
> [RtcServerTests/TestClient.cs](./RtcServerTests/TestClient.cs) can be used as reference for implementing a client.
