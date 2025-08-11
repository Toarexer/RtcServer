using System.Net.Quic;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;

namespace RtcServer;

/// <summary>The base interface for RTC clients.</summary>
public interface IRtcClient {
	/// <summary>The ID of the client</summary>
	uint Id { get; }

	/// <summary>An optional alias for the client.</summary>
	string? Alias { get; set; }

	/// <summary>The remote address of the client.</summary>
	string? Remote { get; }

	/// <summary>The ID that will be assigned to the next client.</summary>
	static abstract uint NextId { get; }
}

/// <summary>Handles a unidirectional control and a bidirectional data <see cref="QuicStream"/> for a <see cref="QuicConnection"/>.</summary>
/// <remarks>All clients create a <see cref="CancellationTokenSource"/> that is linked to the provided <see cref="CancellationToken"/>, and a simple console logger.</remarks>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public sealed class RtcClient : IAsyncDisposable, IRtcClient {
	private record ControlMessageResult(IControlMessage? Message, bool Aborted);

	private const int DataHeaderIdSize     = 4;
	private const int DataHeaderLengthSize = 2;
	private const int DataHeaderSize       = DataHeaderIdSize + DataHeaderLengthSize;

	private const int DataChannelCapacity = 128;

	/// <summary>https://www.rfc-editor.org/rfc/rfc6716</summary>
	private const int MaxOpusPacketSize = 1275;

	/// <summary>The <see cref="QuicConnection"/> the client was created with.</summary>
	public readonly QuicConnection Connection;

	public uint Id { get; }

	public string? Alias { get; set; }

	public string Remote => Connection.RemoteEndPoint.ToString();

	private readonly Channel<byte[]> _dataChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(DataChannelCapacity) { SingleReader = true });

	private readonly byte[] _idBytes;

	private readonly CancellationTokenSource _linkedCts;

	private readonly CancellationToken _token;

	private readonly ILogger _logger;

	/// <summary>The unidirectional <see cref="QuicStream"/> used for control messages.</summary>
	private QuicStream? _control;

	/// <summary>The bidirectional <see cref="QuicStream"/> used for data transfer.</summary>
	private QuicStream? _data;

	/// <summary>The last ID that was assigned to a <see cref="RtcClient"/>.</summary>
	/// <remarks>Probably same to let if overflow since it will take a lot of time.</remarks>
	private static uint _lastId = uint.MaxValue;

	public static uint NextId => _lastId + 1;

	/// <summary>Instantiates a new <see cref="RtcClient"/>.</summary>
	/// <param name="connection">The <see cref="QuicConnection"/> to use.</param>
	/// <param name="token">The <see cref="CancellationToken"/> to link to the internally created <see cref="CancellationTokenSource"/>.</param>
	/// <param name="minLogLevel">The minimum <see cref="LogLevel"/> to set for the internal <see cref="ILogger"/>.</param>
	public RtcClient(QuicConnection connection, CancellationToken token, LogLevel minLogLevel = LogLevel.Information) {
		Id = Interlocked.Increment(ref _lastId);
		_idBytes = BitConverter.GetBytes(Id);

		_linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
		_token = _linkedCts.Token;

		using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddSimpleConsole(scf => scf.TimestampFormat = "HH:mm:ss ").SetMinimumLevel(minLogLevel));
		_logger = factory.CreateLogger(nameof(RtcClient));

		Connection = connection;
	}

	/// <summary>Tries to read a control message from the control stream.</summary>
	/// <returns>An <see cref="IControlMessage"/> or <c>null</c> if it cannot be parsed.</returns>
	private async Task<ControlMessageResult> TryReadControlMessage() {
		if (_control is null)
			return new ControlMessageResult(null, false);

		try {
			byte[] buffer = new byte[1];
			await _control.ReadExactlyAsync(buffer, 0, 1, _token);
			byte type = buffer[0];

			// TODO: Replace BinaryReader with an async solution
			using BinaryReader reader = new(_control, Encoding.UTF8, true);

			IControlMessage message = type switch {
				ControlMessage.Auth => new AuthenticationMessage(reader.ReadString(), reader.ReadString(), reader.ReadBoolean()),
				ControlMessage.Chan => new JoinChannelMessage(reader.ReadUInt32()),
				_                   => new InvalidMessage(type)
			};
			return new ControlMessageResult(message, false);
		}
		catch (OperationCanceledException) {
			return new ControlMessageResult(null, true);
		}
		catch (QuicException quicException) when (quicException.QuicError is QuicError.ConnectionAborted or QuicError.StreamAborted or QuicError.ConnectionIdle) {
			return new ControlMessageResult(null, true);
		}
		catch (Exception exception) {
			_logger.LogWarning("{Source}: {ExceptionType} - {ExceptionMessage}", nameof(TryReadControlMessage), exception.GetType(), exception.Message);
			return new ControlMessageResult(null, false);
		}
	}

	/// <summary>Waits for an inbound stream and sets is as the control stream, if it is readable, but not writable.</summary>
	/// <returns><c>true</c> on success and <c>false</c> on failure.</returns>
	public async Task<bool> WaitForControlStream() {
		try {
			if (await Connection.AcceptInboundStreamAsync(_token) is not { CanRead: true, CanWrite: false } stream)
				return false;

			_control = stream;
			return true;
		}
		catch (OperationCanceledException) {
			return false;
		}
	}

	/// <summary>Reads the control stream until it receives an <see cref="AuthenticationMessage"/>.</summary>
	/// <returns>The <see cref="AuthenticationMessage"/> or <c>null</c>, if cancellation is requested.</returns>
	public async Task<AuthenticationMessage?> WaitForAuthMessage() {
		while (!_linkedCts.IsCancellationRequested)
			if (await TryReadControlMessage() is { Message: AuthenticationMessage auth })
				return auth;
		return null;
	}

	/// <summary>Waits for an inbound stream and sets it as the data stream, if it is both readable and writable.</summary>
	/// <returns><c>true</c> on success and <c>false</c> on failure.</returns>
	public async Task<bool> WaitForDataStream() {
		try {
			if (await Connection.AcceptInboundStreamAsync(_token) is not { CanRead: true, CanWrite: true } stream)
				return false;

			_data = stream;
			_ = Task.Run(async () => {
				await foreach (byte[] message in _dataChannel.Reader.ReadAllAsync(_token))
					await _data.WriteAsync(message, _token);
			}, CancellationToken.None);

			return true;
		}
		catch (OperationCanceledException) {
			return false;
		}
	}

	/// <summary>Returns a task that starts handling the control stream in the background.</summary>
	/// <param name="joinChannelMessageCallback">A delegate that will be called when a <see cref="JoinChannelMessage"/> is received. The requested channel id is passed to the delegate.</param>
	public Task HandleControlStream(Action<RtcClient, uint>? joinChannelMessageCallback) {
		joinChannelMessageCallback?.Invoke(this, 0);

		return Task.Run(async () => {
			while (!_linkedCts.IsCancellationRequested && _control is { CanRead: true }) {
				ControlMessageResult result = await TryReadControlMessage();
				switch (result.Message) {
					case null:
						if (!result.Aborted)
							_logger.LogCritical("{Source}: Received malformed control message from {Remote}", nameof(HandleControlStream), Connection.RemoteEndPoint);
						if (!_linkedCts.IsCancellationRequested)
							await _linkedCts.CancelAsync();
						return;
					case AuthenticationMessage:
						_logger.LogDebug("{Source}: Received {Message} from {Remote}", nameof(HandleControlStream), result.Message, Connection.RemoteEndPoint);
						break;
					case JoinChannelMessage chanMessage:
						_logger.LogDebug("{Source}: Received {Message} from {Remote}", nameof(HandleControlStream), chanMessage, Connection.RemoteEndPoint);
						joinChannelMessageCallback?.Invoke(this, chanMessage.ChannelId);
						break;
					default:
						_logger.LogWarning("{Source}: Received invalid ({MessageType}) control message from {Remote}", nameof(HandleControlStream), result.Message.Type,
							Connection.RemoteEndPoint);
						break;
				}
			}
		}, CancellationToken.None);
	}

	/// <summary>Returns a task that starts handling the data stream in the background.</summary>
	/// <param name="getDestinationClients">A delegate to retrieve the streams the data stream should be written to. If <c>null</c> the received data is echoed back to the data stream.</param>
	public Task HandleDataStream(Func<RtcClient, IEnumerable<RtcClient>>? getDestinationClients) {
		byte[] buffer = new byte[DataHeaderSize + MaxOpusPacketSize];

		return Task.Run(async () => {
			try {
				if (_data is not { CanRead: true, CanWrite: true }) {
					AbortAndDispose();
					return;
				}

				while (_data is { CanRead: true, CanWrite: true }) {
					await _data.ReadExactlyAsync(buffer, DataHeaderIdSize, DataHeaderLengthSize, _token);
					int length = buffer[DataHeaderIdSize] | (buffer[DataHeaderIdSize + 1] << 8);

					switch (length) {
						case 0:
							continue;
						case < 0 or > MaxOpusPacketSize:
							AbortAndDispose();
							return;
					}

					await _data.ReadExactlyAsync(buffer, DataHeaderSize, length, _token);
					ReadOnlyMemory<byte> memory = new(buffer, 0, DataHeaderSize + length);

					if (getDestinationClients is null) {
						_idBytes.CopyTo(buffer, 0);
						await _data.WriteAsync(memory, _token);
					}
					else {
						foreach (RtcClient destination in getDestinationClients(this)) {
							byte[] message = memory.ToArray();
							_idBytes.CopyTo(message, 0);

							if (destination._data?.CanWrite is true && await TryWriteDataMessageAsync(destination, message) is { } exception)
								_logger.LogError(exception, "{Source} / {InnerSource} error", nameof(HandleDataStream), nameof(TryWriteDataMessageAsync));
						}
					}
				}
			}
			catch (QuicException quicException) when (quicException.QuicError is QuicError.ConnectionAborted or QuicError.StreamAborted or QuicError.OperationAborted) {
				AbortAndDispose();
			}
			catch (QuicException quicException) when (quicException.QuicError is QuicError.ConnectionIdle) {
				_logger.LogWarning("{Source}: Connection from {Remote} timed out due to inactivity", nameof(HandleDataStream), Connection.RemoteEndPoint);
				AbortAndDispose();
			}
			catch (OperationCanceledException) {
				_logger.LogDebug("{Source}: Operation cancelled", nameof(HandleDataStream));
				AbortAndDispose();
			}
			catch (Exception exception) {
				_logger.LogError(exception, "{Source} error", nameof(HandleDataStream));
				AbortAndDispose();
			}
		}, CancellationToken.None);
	}

	/// <summary>Aborts and disposes <see cref="_control"/> and <see cref="_data"/> streams, then disposes <see cref="_linkedCts"/>.</summary>
	private void AbortAndDispose() {
		if (!_linkedCts.IsCancellationRequested)
			_linkedCts.Cancel();

		_dataChannel.Writer.TryComplete();

		if (_control is not null) {
			_control.Abort(QuicAbortDirection.Read, (long)QuicError.StreamAborted);
			_control.Dispose();
		}

		if (_data is not null) {
			_data.Abort(QuicAbortDirection.Both, (long)QuicError.StreamAborted);
			_data.Dispose();
		}

		_linkedCts.Dispose();
	}

	public ValueTask DisposeAsync() {
		AbortAndDispose();
		return ValueTask.CompletedTask;
	}

	/// <summary>Tries to write a data message to the data channel of a <see cref="RtcClient"/>.</summary>
	/// <param name="client">The <see cref="RtcClient"/> to use the data <see cref="Channel{T}"/> and <see cref="CancellationToken"/> of for the operation.</param>
	/// <param name="message">The <see cref="ReadOnlyMemory{T}"/> of <see cref="byte"/> containing the message to write.</param>
	/// <returns>The unhandled <see cref="Exception"/> or <c>null</c> if no errors occured.</returns>
	private static async Task<Exception?> TryWriteDataMessageAsync(RtcClient client, byte[] message) {
		try {
			await client._dataChannel.Writer.WriteAsync(message, client._token);
			return null;
		}
		catch (OperationCanceledException) {
			return null;
		}
		catch (Exception exception) {
			return exception;
		}
	}

#if DEBUG
	public static void ResetNextId() => _lastId = uint.MaxValue;
#endif
}
