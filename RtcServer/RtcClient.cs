using System.Net.Quic;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;

namespace RtcServer;

/// <summary>Handles a unidirectional control and a bidirectional data <see cref="QuicStream"/> for a <see cref="QuicConnection"/>.</summary>
/// <remarks>All clients create a <see cref="CancellationTokenSource"/> that is linked to the provided <see cref="CancellationToken"/>, and a simple console logger.</remarks>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
internal sealed class RtcClient : IAsyncDisposable {
	private record ControlMessageResult(IControlMessage? Message, bool Aborted);

	private const int DataHeaderIdSize     = 4;
	private const int DataHeaderLengthSize = 2;
	private const int DataHeaderSize       = DataHeaderIdSize + DataHeaderLengthSize;

	private const int DataChannelCapacity = 128;

	/// <summary>https://www.rfc-editor.org/rfc/rfc6716</summary>
	private const int MaxOpusPacketSize = 1275;

	/// <summary>The ID that will be assigned to the next <see cref="RtcClient"/>.</summary>
	public static uint NextId => _lastId + 1;

	/// <summary>The <see cref="QuicConnection"/> the client was created with.</summary>
	public readonly QuicConnection Connection;

	/// <summary>The ID of the client</summary>
	public readonly uint Id;

	/// <summary>An optional alias for the client.</summary>
	public string? Alias { get; set; }

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
	private ControlMessageResult TryReadControlMessage() {
		if (_control is null)
			return new ControlMessageResult(null, false);

		try {
			using BinaryReader reader = new(_control, Encoding.UTF8, true);
			byte type = reader.ReadByte();

			IControlMessage message = type switch {
				ControlMessage.Auth => new AuthenticationMessage(reader.ReadString(), reader.ReadString(), reader.ReadBoolean()),
				ControlMessage.Chan => new JoinChannelMessage(reader.ReadUInt32()),
				_                   => new InvalidMessage(type)
			};
			return new ControlMessageResult(message, false);
		}
		catch (QuicException quicException) when (quicException.QuicError is QuicError.ConnectionAborted or QuicError.StreamAborted or QuicError.ConnectionIdle) {
			return new ControlMessageResult(null, true);
		}
		catch (Exception exception) {
			_logger.LogWarning("{}: {} - {}", nameof(TryReadControlMessage), exception.GetType(), exception.Message);
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
	public AuthenticationMessage? WaitForAuthMessage() {
		while (!_linkedCts.IsCancellationRequested)
			if (TryReadControlMessage().Message is AuthenticationMessage auth)
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
			}, _token);

			return true;
		}
		catch (OperationCanceledException) {
			return false;
		}
	}

	/// <summary>Returns a task that starts handling the control stream in the background.</summary>
	/// <param name="joinChannelMessageCallback">A delegate that will be called when a <see cref="JoinChannelMessage"/> is received. The requested channel id is passed to the delegate.</param>
	public Task HandleControlStream(Action<uint>? joinChannelMessageCallback) {
		joinChannelMessageCallback?.Invoke(0);

		return Task.Run(() => {
			while (!_linkedCts.IsCancellationRequested && _control is { CanRead: true }) {
				ControlMessageResult result = TryReadControlMessage();
				switch (result.Message) {
					case null:
						if (!result.Aborted)
							_logger.LogCritical("{}: Received malformed control message from {}", nameof(HandleControlStream), Connection.RemoteEndPoint);
						if (!_linkedCts.IsCancellationRequested)
							_linkedCts.Cancel();
						return;
					case AuthenticationMessage:
						_logger.LogDebug("{}: Received {} from {}", nameof(HandleControlStream), result.Message, Connection.RemoteEndPoint);
						break;
					case JoinChannelMessage freqMessage:
						_logger.LogDebug("{}: Received {} from {}", nameof(HandleControlStream), freqMessage, Connection.RemoteEndPoint);
						joinChannelMessageCallback?.Invoke(freqMessage.ChannelId);
						break;
					default:
						_logger.LogWarning("{}: Received invalid ({}) control message from {}", nameof(HandleControlStream), result.Message.Type, Connection.RemoteEndPoint);
						break;
				}
			}
		}, _token);
	}

	/// <summary>Returns a task that starts handling the data stream in the background.</summary>
	/// <param name="getDestinationClients">A delegate to retrieve the streams the data stream should be written to. If <c>null</c> the received data is echoed back to the data stream.</param>
	public Task HandleDataStream(Func<IEnumerable<RtcClient>>? getDestinationClients) {
		byte[] buffer = new byte[DataHeaderSize + MaxOpusPacketSize];

		return Task.Run(async () => {
			try {
				if (_data is not { CanRead: true, CanWrite: true }) {
					await AbortAndClose(QuicError.StreamAborted);
					return;
				}

				while (_data is { CanRead: true, CanWrite: true }) {
					await _data.ReadExactlyAsync(buffer, DataHeaderIdSize, DataHeaderLengthSize, _token);
					int length = buffer[DataHeaderIdSize] | (buffer[DataHeaderIdSize + 1] << 8);

					switch (length) {
						case 0:
							continue;
						case < 0 or > MaxOpusPacketSize:
							await AbortAndClose(QuicError.TransportError);
							return;
					}

					await _data.ReadExactlyAsync(buffer, DataHeaderSize, length, _token);
					ReadOnlyMemory<byte> memory = new(buffer, 0, DataHeaderSize + length);

					if (getDestinationClients is null) {
						_idBytes.CopyTo(buffer, 0);
						await _data.WriteAsync(memory, _token);
					}
					else {
						foreach (RtcClient destination in getDestinationClients()) {
							byte[] message = memory.ToArray();
							_idBytes.CopyTo(message, 0);

							if (destination._data?.CanWrite is true && await TryWriteDataMessageAsync(destination, message) is { } exception)
								_logger.LogError("{} / {}: {} - {}", nameof(HandleDataStream), nameof(TryWriteDataMessageAsync), exception.GetType(), exception.Message);
						}
					}
				}
			}
			catch (QuicException quicException) when (quicException.QuicError is QuicError.ConnectionAborted or QuicError.StreamAborted or QuicError.OperationAborted) {
				await AbortAndClose(QuicError.ConnectionAborted);
			}
			catch (QuicException quicException) when (quicException.QuicError is QuicError.ConnectionIdle) {
				_logger.LogWarning("{}: Connection from {} timed out due to inactivity", nameof(HandleDataStream), Connection.RemoteEndPoint);
				await AbortAndClose(QuicError.ConnectionAborted);
			}
			catch (OperationCanceledException) {
				_logger.LogDebug("{}: Operation cancelled", nameof(HandleDataStream));
				await AbortAndClose(QuicError.ConnectionAborted);
			}
			catch (Exception exception) {
				_logger.LogError("{}: {} - {}", nameof(HandleDataStream), exception.GetType(), exception.Message);
				await AbortAndClose(QuicError.InternalError);
			}
		}, _token);
	}

	/// <summary>Aborts and disposes the <see cref="_control"/> and <see cref="_data"/> streams, then closes and disposes the <see cref="Connection"/> alongside the <see cref="_linkedCts"/>.</summary>
	/// <param name="error">The error code to close the connection with.</param>
	private async ValueTask AbortAndClose(QuicError error) {
		if (!_linkedCts.IsCancellationRequested)
			await _linkedCts.CancelAsync();

		_dataChannel.Writer.TryComplete();

		if (_control is not null) {
			_control.Abort(QuicAbortDirection.Read, (long)QuicError.StreamAborted);
			await _control.DisposeAsync();
		}

		if (_data is not null) {
			_data.Abort(QuicAbortDirection.Both, (long)QuicError.StreamAborted);
			await _data.DisposeAsync();
		}

		await Connection.CloseAsync((long)error, CancellationToken.None);
		await Connection.DisposeAsync();

		_linkedCts.Dispose();
	}

	public ValueTask DisposeAsync() => AbortAndClose(QuicError.ConnectionAborted);

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
}
