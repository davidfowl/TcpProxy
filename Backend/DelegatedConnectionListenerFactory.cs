
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;

// Custom transport that accepts client sockets over the listen socket as data
internal class DelegatedConnectionListenerFactory : IConnectionListenerFactory
{
    private ILogger<DelegatedConnectionListenerFactory> _logger;

    public DelegatedConnectionListenerFactory(ILogger<DelegatedConnectionListenerFactory> logger)
    {
        _logger = logger;
    }

    public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        // Front end connections connect to this socket
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(endpoint);
        socket.Listen();

        return new SocketListener(socket, _logger);
    }

    class SocketListener : IConnectionListener
    {
        private Socket _socket;
        private readonly Task _acceptTask;
        // This is equivalent to our connection backlog
        private readonly Channel<ConnectionContext> _channel = Channel.CreateBounded<ConnectionContext>(20);
        private readonly SocketConnectionContextFactory _contextFactory;

        public SocketListener(Socket socket, ILogger logger)
        {
            _socket = socket;
            _acceptTask = AcceptServerSocketConnections();
            _contextFactory = new(new(), logger);
        }

        public EndPoint EndPoint => _socket.LocalEndPoint!;

        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            var connection = await _channel.Reader.ReadAsync(cancellationToken);

            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            _socket.Dispose();

            await _acceptTask;
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            _socket.Dispose();

            return default;
        }

        private async Task AcceptServerSocketConnections()
        {
            async Task AcceptForwardedConnection(Socket frontEndSocket)
            {
                var reader = PipeReader.Create(new NetworkStream(frontEndSocket));
                while (true)
                {
                    // Make sure we have the length at least
                    var result = await reader.ReadAtLeastAsync(2);
                    var buffer = result.Buffer;

                    if (buffer.Length < 2)
                    {
                        Debug.Assert(result.IsCompleted);
                        break;
                    }

                    var lengthBytes = buffer.Slice(0, 2);
                    var payloadLength = BinaryPrimitives.ReadInt16LittleEndian(lengthBytes.IsSingleSegment ? lengthBytes.FirstSpan : lengthBytes.ToArray());
                    
                    // Look at the remaining buffer
                    buffer = buffer.Slice(2);

                    // We didn't get the payload in the same message so read it
                    if (buffer.Length < payloadLength)
                    {
                        // Advance 2 bytes
                        reader.AdvanceTo(lengthBytes.End);

                        // Try reading a new message the length of the payload
                        result = await reader.ReadAtLeastAsync(payloadLength);
                        buffer = result.Buffer;

                        // We still didn't get enough, it means we're done reading
                        if (buffer.Length < payloadLength)
                        {
                            Debug.Assert(result.IsCompleted);
                            break;
                        }
                    }

                    var message = buffer.Slice(0, payloadLength);

                    var si = new SocketInformation
                    {
                        ProtocolInformation = message.ToArray()
                    };

                    reader.AdvanceTo(message.End);

                    var socket = new Socket(si);

                    var connectionContext = _contextFactory.Create(socket);

                    await _channel.Writer.WriteAsync(connectionContext);
                }

                await reader.CompleteAsync();
            }

            try
            {
                while (true)
                {
                    // This loop waits for new front end connections to receive
                    // forwarded sockets from
                    var client = await _socket.AcceptAsync();

                    // Kick off the listen for sockets passed over this channel
                    _ = Task.Run(() => AcceptForwardedConnection(client));
                }
            }
            catch (ObjectDisposedException)
            {

            }
        }
    }
}
