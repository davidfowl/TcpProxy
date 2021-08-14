using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Options;

// This is listens on a socket and spins up various backends to forward those sockets to
internal class DelegatingConnectionListenerFactory : IConnectionListenerFactory
{
    private readonly ILogger _logger;
    private readonly BackendOptions _options;

    public DelegatingConnectionListenerFactory(ILogger<DelegatingConnectionListenerFactory> logger, IOptions<BackendOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        // Startup the destination backends that we're going to forward connections to
        var destinations = new Destination[2];
        for (int i = 0; i < destinations.Length; i++)
        {
            destinations[i] = new(_options);
            await destinations[i].StartAsync(cancellationToken);
        }

        // Setup the listen socket
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(endpoint);
        socket.Listen();

        return new SocketListener(destinations, socket, _logger);
    }

    class Destination : IAsyncDisposable
    {
        private readonly Socket _socket = new(SocketType.Stream, ProtocolType.Tcp);
        private Process _process = default!;
        private readonly BackendOptions _options;

        public Destination(BackendOptions options)
        {
            _options = options;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_socket.Connected)
            {
                return;
            }

            // Pick a random port
            var port = Random.Shared.Next(8000, 9000);

            var processStartInfo = new ProcessStartInfo
            {
                FileName = _options.ProcessPath,
            };

            processStartInfo.Environment["PORT"] = port.ToString();

            _process = Process.Start(processStartInfo)!;

            while (true)
            {
                try
                {
                    await _socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken);
                    break;
                }
                catch
                {
                    await Task.Delay(1000, cancellationToken);
                }
            }

            // We're connected to the backend
        }

        public async Task DelegateConnectionAsync(Socket client)
        {
            var socketInformation = client.DuplicateAndClose(_process.Id);

            var protocolInfo = socketInformation.ProtocolInformation;
            var length = protocolInfo.Length;
            var payload = new byte[2 + length];
            BinaryPrimitives.WriteInt16LittleEndian(payload, (short)length);
            protocolInfo.AsSpan().CopyTo(payload.AsSpan(2));

            await _socket.SendAsync(payload, SocketFlags.None);
        }

        public async ValueTask DisposeAsync()
        {
            _process.Kill();

            await _process.WaitForExitAsync();
        }
    }

    class SocketListener : IConnectionListener
    {
        private readonly Socket _socket;
        private readonly SocketConnectionContextFactory _contextFactory;
        private long _sockets;
        private readonly Destination[] _destinations;

        public SocketListener(Destination[] destinations, Socket socket, ILogger logger)
        {
            _destinations = destinations;
            _socket = socket;
            _contextFactory = new(new(), logger);
        }

        public EndPoint EndPoint => _socket.LocalEndPoint!;

        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                while (true)
                {
                    var client = await _socket.AcceptAsync();

                    _sockets++;

                    // Randomly forward a connection to a backend
                    if (OperatingSystem.IsWindows() && _sockets % 2 == 0)
                    {
                        var which = Random.Shared.Next(_destinations.Length);

                        await _destinations[which].DelegateConnectionAsync(client);

                        continue;
                    }

                    // Handle some connections locally
                    return _contextFactory.Create(client);
                }
            }
            catch (ObjectDisposedException)
            {

            }
            catch (SocketException)
            {

            }

            return null;
        }

        public async ValueTask DisposeAsync()
        {
            _socket.Dispose();

            _contextFactory.Dispose();

            foreach (var dest in _destinations)
            {
                await dest.DisposeAsync();
            }
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            _socket.Dispose();
            return default;
        }
    }
}
