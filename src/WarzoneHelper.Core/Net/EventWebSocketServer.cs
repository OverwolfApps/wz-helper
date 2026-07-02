using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WarzoneHelper.Core.Net
{
    /// <summary>
    /// Broadcasts helper events to WebSocket clients (the Overwolf app UI) over loopback, and
    /// accepts inbound control messages from them (e.g. GEP hints). Built on HttpListener +
    /// System.Net.WebSockets, so no external dependency. Binding a fixed loopback port is why the
    /// agent runs elevated anyway (same process that owns the ETW trace).
    ///
    /// Wire protocol (text frames, one JSON object each):
    ///   server -> client : the HelperEvent JSON, verbatim
    ///   client -> server : {"type":"gep","name":"match_start","data":"..."}   (relayed to core)
    ///                      {"type":"hello"}  -> server replies with a backlog of recent events
    /// </summary>
    public sealed class EventWebSocketServer : IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly Action<string> _log;
        private readonly Action<string, string> _onGep;   // (gepName, data)
        private readonly Action<byte[]> _onFrame;         // pushed game frame (decoded png/jpg bytes)

        private HttpListener _listener;
        private CancellationTokenSource _cts;

        private sealed class Client
        {
            public WebSocket Socket;
            public SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);
        }
        private readonly ConcurrentDictionary<Guid, Client> _clients = new ConcurrentDictionary<Guid, Client>();

        // Small backlog so a freshly-opened UI window isn't blank.
        private readonly Queue<string> _backlog = new Queue<string>();
        private readonly object _backlogLock = new object();
        private const int BacklogMax = 200;

        public EventWebSocketServer(string host, int port, Action<string> log,
            Action<string, string> onGep, Action<byte[]> onFrame = null)
        {
            _host = string.IsNullOrEmpty(host) ? "127.0.0.1" : host;
            _port = port;
            _log = log;
            _onGep = onGep;
            _onFrame = onFrame;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://{_host}:{_port}/");
            try
            {
                _listener.Start();
                _log?.Invoke($"[ws] listening on ws://{_host}:{_port}/");
                Task.Run(() => AcceptLoop(_cts.Token));
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[ws] failed to start on {_host}:{_port}: {ex.Message} " +
                             "(needs elevation or a netsh http urlacl reservation).");
            }
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { break; }

                if (!ctx.Request.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 426; // Upgrade Required
                    ctx.Response.Close();
                    continue;
                }

                _ = HandleClient(ctx, ct);
            }
        }

        private async Task HandleClient(HttpListenerContext ctx, CancellationToken ct)
        {
            HttpListenerWebSocketContext wsCtx;
            try { wsCtx = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false); }
            catch { ctx.Response.StatusCode = 500; ctx.Response.Close(); return; }

            var id = Guid.NewGuid();
            var client = new Client { Socket = wsCtx.WebSocket };
            _clients[id] = client;
            _log?.Invoke($"[ws] client connected ({_clients.Count} total)");

            try
            {
                var buffer = new byte[64 * 1024];
                while (client.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    // Frames can exceed one buffer, so accumulate until EndOfMessage.
                    using (var ms = new System.IO.MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await client.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                            if (result.MessageType == WebSocketMessageType.Close) return;
                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Text)
                            HandleInbound(client, Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                    }
                }
            }
            catch { /* client dropped */ }
            finally
            {
                _clients.TryRemove(id, out _);
                try { client.Socket.Dispose(); } catch { }
                _log?.Invoke($"[ws] client disconnected ({_clients.Count} total)");
            }
        }

        private void HandleInbound(Client client, string text)
        {
            try
            {
                var o = JObject.Parse(text);
                switch (o.Value<string>("type"))
                {
                    case "gep":
                        _onGep?.Invoke(o.Value<string>("name"), o.Value<string>("data"));
                        break;
                    case "frame":
                        if (_onFrame != null)
                        {
                            var b64 = o.Value<string>("data") ?? "";
                            var comma = b64.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
                            if (comma >= 0) b64 = b64.Substring(comma + 7);
                            try { _onFrame(Convert.FromBase64String(b64)); } catch { }
                        }
                        break;
                    case "hello":
                        string[] snapshot;
                        lock (_backlogLock) snapshot = _backlog.ToArray();
                        foreach (var line in snapshot) _ = SendTo(client, line);
                        break;
                }
            }
            catch { /* ignore malformed control frames */ }
        }

        /// <summary>Broadcast an event JSON line to all connected clients.</summary>
        public void Broadcast(string json)
        {
            lock (_backlogLock)
            {
                _backlog.Enqueue(json);
                while (_backlog.Count > BacklogMax) _backlog.Dequeue();
            }
            foreach (var kv in _clients)
                _ = SendTo(kv.Value, json);
        }

        private async Task SendTo(Client client, string json)
        {
            if (client.Socket.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(json);
            await client.SendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await client.Socket.SendAsync(new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* will be cleaned up by the receive loop */ }
            finally { client.SendLock.Release(); }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            foreach (var kv in _clients)
            {
                try { kv.Value.Socket.Abort(); kv.Value.Socket.Dispose(); } catch { }
            }
            _clients.Clear();
            try { _listener?.Stop(); _listener?.Close(); } catch { }
        }
    }
}
