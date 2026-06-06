using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace RatcliffDefense.RadarDataExport
{
    /// <summary>
    /// Streams radar snapshots to connected clients over TCP. Entirely off the
    /// Unity main thread: the game only ever calls <see cref="Broadcast"/>, which
    /// hands off a byte buffer and returns immediately, so a slow or stalled
    /// client can never block the game loop.
    ///
    /// One acceptor thread takes new connections; one sender thread writes the
    /// most recent snapshot to every client. Snapshots coalesce — if the game
    /// produces faster than a client drains, only the latest is sent. Each
    /// snapshot is a self-contained full picture, so dropping intermediates is
    /// harmless.
    /// </summary>
    internal sealed class TcpRadarServer
    {
        private readonly IPAddress _address;
        private readonly int _port;
        private readonly ManualLogSource _log;

        private TcpListener _listener;
        private Thread _acceptThread;
        private Thread _sendThread;
        private volatile bool _running;

        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private readonly object _clientsLock = new object();
        private volatile int _clientCount;

        private volatile byte[] _pending;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);

        private const int SendTimeoutMs = 1000;

        public TcpRadarServer(string address, int port, ManualLogSource log)
        {
            _address = IPAddress.TryParse(address, out IPAddress ip) ? ip : IPAddress.Loopback;
            _port = port;
            _log = log;
        }

        public bool HasClients => _clientCount > 0;

        public bool Start()
        {
            if (_running)
                return true;

            try
            {
                _listener = new TcpListener(_address, _port);
                _listener.Start();
            }
            catch (Exception e)
            {
                _log.LogError($"Failed to bind {_address}:{_port} - {e.Message}. Export server not started.");
                return false;
            }

            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "NORDM-Accept" };
            _acceptThread.Start();
            _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "NORDM-Send" };
            _sendThread.Start();
            return true;
        }

        public void Broadcast(string snapshot)
        {
            _pending = Encoding.UTF8.GetBytes(snapshot);
            _signal.Set();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception e)
                {
                    _log.LogWarning($"Accept error: {e.Message}");
                    continue;
                }

                client.NoDelay = true;
                client.SendTimeout = SendTimeoutMs;
                lock (_clientsLock)
                {
                    _clients.Add(client);
                    _clientCount = _clients.Count;
                }
                _log.LogInfo($"Client connected: {SafeEndpoint(client)} ({_clientCount} total).");

                // Send the latest snapshot immediately so a new client paints at once.
                byte[] latest = _pending;
                if (latest != null)
                    TryWrite(client, latest);
            }
        }

        private void SendLoop()
        {
            while (_running)
            {
                _signal.WaitOne();
                if (!_running)
                    break;

                byte[] data = _pending;
                if (data == null || _clientCount == 0)
                    continue;

                List<TcpClient> dead = null;
                lock (_clientsLock)
                {
                    for (int i = 0; i < _clients.Count; i++)
                    {
                        if (!TryWrite(_clients[i], data))
                            (dead ?? (dead = new List<TcpClient>())).Add(_clients[i]);
                    }
                    if (dead != null)
                    {
                        foreach (TcpClient c in dead)
                        {
                            _clients.Remove(c);
                            Close(c);
                        }
                        _clientCount = _clients.Count;
                    }
                }

                if (dead != null)
                    _log.LogInfo($"Dropped {dead.Count} disconnected client(s) ({_clientCount} remain).");
            }
        }

        private static bool TryWrite(TcpClient client, byte[] data)
        {
            try
            {
                if (!client.Connected)
                    return false;
                NetworkStream stream = client.GetStream();
                stream.Write(data, 0, data.Length);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Stop()
        {
            if (!_running)
                return;
            _running = false;

            try { _listener?.Stop(); } catch { }
            _signal.Set();

            lock (_clientsLock)
            {
                foreach (TcpClient c in _clients)
                    Close(c);
                _clients.Clear();
                _clientCount = 0;
            }
            _log.LogInfo("Export server stopped.");
        }

        private static void Close(TcpClient c)
        {
            try { c.Close(); } catch { }
        }

        private static string SafeEndpoint(TcpClient c)
        {
            try { return c.Client.RemoteEndPoint?.ToString() ?? "?"; }
            catch { return "?"; }
        }
    }
}
