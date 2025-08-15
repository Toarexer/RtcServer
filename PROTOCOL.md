# QUIC RTC Server Protocol

Custom ALPN string: `qrtc/1`

## Overview

The communication between a client and the server happen through two QUIC streams.

- A unidirectional stream for sending control messages referred to as the **control** stream.
- A bidirectional stream for sending and receiving data messages referred to as the **data** stream.

### Connecting to the server

1. The client establishes a QUIC connection with the server using the custom ALPN and without certificate validation.
2. The client creates a unidirectional stream.
3. The client sends an [Authentication message](#authentication-control-message).
4. The client creates a bidirectional stream.

If the server encounters any errors during this process the connection is aborted.

> [!IMPORTANT]
> Currently the server generates a new certificate on each startup.
> The certificate is only meant for enabling TLS encryption and not for authenticating the server!

### Joining a channel

By default, the client is added to channel 0 which does not allow broadcasting.
To join another channel, the client has to send a [JoinChannel message](#joinchannel-control-message)
through the **control** stream which contains the ID of the channel it wishes to connect to.

### Sending data

To send data, the client has to send a [C2S (client-to-server) message](#c2s-data-message) through the **data** stream.
If the message is malformed the connection will likely be aborted.

> [!IMPORTANT]
> Since data sent is simply broadcasted to other clients on the same channel
> all clients are required to use the same Opus encoder and decoder settings.

### Receiving data

To receive data, the client has to read from the **data** stream
and parse the incoming [S2C (server-to-client) message](#s2c-data-message).

## Message formats

### Authentication control message

| Segment         | Length      | Description                                                                    |
|:----------------|:------------|:-------------------------------------------------------------------------------|
| Type            | 1 byte      | The type of the message. Always 1.                                             |
| Echo            | 1 byte      | Determines whether the server should echo back or broadcast the data received. |
| Username Length | 1 byte      | The length of the UTF8 encoded username string.                                |
| Username        | 0-255 bytes | The UTF8 encoded username string.                                              |
| Password Length | 1 byte      | The length of the UTF8 encoded password string.                                |
| Password        | 0-255 bytes | The UTF8 encoded password string.                                              |

### JoinChannel control message

| Segment    | Length  | Description                                                   |
|:-----------|:--------|:--------------------------------------------------------------|
| Type       | 1 byte  | The type of the message. Always 2.                            |
| Channel ID | 4 bytes | The little endian unsigned integer containing the channel ID. |

### C2S data message

| Segment | Length       | Description                     |
|:--------|:-------------|:--------------------------------|
| Length  | 2 byte       | The length of the next segment. |
| Data    | 0-1275 bytes | The Opus encoded PCM data.      |

### S2C data message

| Segment   | Length       | Description                                                                                                 |
|:----------|:-------------|:------------------------------------------------------------------------------------------------------------|
| Sender ID | 4 byte       | The little endian 4 byte unsigned int containing the server assigned ID of the client who sent the message. |
| Length    | 2 byte       | The little endian 2 byte int length of the next segment.                                                    |
| Data      | 0-1275 bytes | The Opus encoded PCM data.                                                                                  |

> [!NOTE]
> The message handling done in [RtcServerTests/Helpers.cs](./RtcServerTests/Helpers.cs) and
> [RtcServerTests/TestClient.cs](./RtcServerTests/TestClient.cs) can be used as reference for implementing a client.
