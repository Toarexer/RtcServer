# QUIC RTC Server

[![Tests](https://github.com/Toarexer/RtcServer/actions/workflows/tests.yml/badge.svg)](https://github.com/Toarexer/RtcServer/actions/workflows/tests.yml)

## Prerequisites

- [**.NET 9**](https://dotnet.microsoft.com/)
- [**MsQuic**](https://github.com/microsoft/msquic)

## Projects

| Name                                   | Description                                                                             |
|:---------------------------------------|:----------------------------------------------------------------------------------------|
| [**RtcServer**](./RtcServer)           | Contains the server implementation.                                                     |
| [**RtcServerTests**](./RtcServerTests) | Contains unit and basic integration tests for checking the functionality of the server. |

## Getting started

**1.** Create a `config.json` file in the [RtcServer](./RtcServer) directory
or specify the required environment variables.

Config file example:

```json
{
  "QuicPort": 7172,
  "HttpPort": 8080,
  "AuthorizationUri": "http://localhost:8080/auth/allow-all",
  "LogLevel": 0
}
```

Environment variables:

- RTC_SERVER_QUIC_PORT
- RTC_SERVER_HTTP_PORT
- RTC_SERVER_AUTH_URI
- RTC_SERVER_LOG_LEVEL

> [!NOTE]
> `/auth/allow-all` is a builtin endpoint for testing, that accepts all clients.
>
> The log level is specified using a
> [LogLevel](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging.loglevel?view=net-9.0-pp) value.

<br>

**2.** Run the [RtcServer](./RtcServer) project using the dotnet CLI tool.

```
dotnet run --project RtcServer
```

## User authentication

User authentication can be achieved by creating a web server
and an endpoint which accepts POST requests and specify it for the server using `AuthorizationUri`.

When a client connects to the server it sends a POST request to the URI specified by `AuthorizationUri`,
that contains an [AuthorizationRequest](./RtcServer/Models.cs#L11) object as JSON.

The server accepts the client if the specified web server responds with a successful status code within 5 seconds.
Otherwise, it aborts the connection.

## Endpoints

| Method | Path              | Description                                                                |
|:-------|:------------------|:---------------------------------------------------------------------------|
| POST   | `/auth/allow-all` | For testing purposes only, can be specified for the `AuthorizationUri`.    |
| GET    | `/info/app`       | Returns an [AppInfo](./RtcServer/Models.cs#L17) object as JSON.            |
| GET    | `/info/config`    | Returns a [Config](./RtcServer/Config.cs) object as JSON.                  |
| GET    | `/info/store`     | Returns an [RtcClientInfos](./RtcServer/Models.cs#L27) object as JSON.     |
| GET    | `/info/clients`   | Returns an [RtcClientStoreInfo](./RtcServer/Models.cs#L40) object as JSON. |
| GET    | `/info`           | Returns an [AppInfo](./RtcServer/Models.cs#L48) object as JSON.            |

> [!WARNING]
> These endpoints are not meant to be publicly exposed!

## Running the tests

Run the [RtcServerTests](./RtcServerTests) project using the dotnet CLI tool.

```
dotnet test
```

## QUIC RTC Server Protocol

The specification of the protocol used by the server can be found [**here**](./PROTOCOL.md).
