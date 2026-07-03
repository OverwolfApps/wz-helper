using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GameHelper.Core.Net
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
    ///                      {"type":"hello"}  -> current-state snapshot: the last occurrence of each
    ///                          event whose latest scrolled out of the backlog, then the recent
    ///                          backlog (so single-valued state survives AND per-entity/history does)
    ///                      {"type":"list_events","max":N|"all"}  -> replays the last N buffered
    ///                          raw events (or all of them) so a client can recover ones it missed
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

        // Recent-event backlog (raw, capped) for list_events, plus the last occurrence of EACH event
        // name (unbounded but tiny) for the hello current-state snapshot.
        private readonly Queue<string> _backlog = new Queue<string>();
        private readonly Dictionary<string, (long seq, string json)> _lastByName =
            new Dictionary<string, (long, string)>();
        private long _seq;
        private readonly object _backlogLock = new object();
        private const int BacklogMax = 500;

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
                        SendHelloSnapshot(client); // last-of-each state (even if scrolled out) + recent backlog
                        break;
                    case "list_events":
                        // {"max": N} -> last N buffered events; "all" or missing -> everything.
                        var maxTok = o["max"];
                        int take = -1;
                        if (maxTok != null && maxTok.Type != JTokenType.Null &&
                            !(maxTok.Type == JTokenType.String &&
                              string.Equals(maxTok.Value<string>(), "all", StringComparison.OrdinalIgnoreCase)))
                            try { take = maxTok.Value<int>(); } catch { take = -1; }
                        SendBacklog(client, take);
                        break;
                }
            }
            catch { /* ignore malformed control frames */ }
        }

        /// <summary>Replay buffered raw events to one client: the last <paramref name="take"/> of
        /// them, or all when take &lt; 0.</summary>
        private void SendBacklog(Client client, int take)
        {
            string[] snapshot;
            lock (_backlogLock) snapshot = _backlog.ToArray();
            int start = (take >= 0 && take < snapshot.Length) ? snapshot.Length - take : 0;
            for (int i = start; i < snapshot.Length; i++) _ = SendTo(client, snapshot[i]);
        }

        /// <summary>Replay on connect: first the last occurrence of each event whose latest has
        /// scrolled OUT of the raw backlog (so single-valued state like MATCH_STATE_CHANGED /
        /// PARTY_CODE_CHANGED is never lost), then the recent raw backlog (which preserves per-entity
        /// events like every PLAYER_JOINED and the chat/killfeed history).</summary>
        private void SendHelloSnapshot(Client client)
        {
            List<string> supplement, backlog;
            lock (_backlogLock)
            {
                var inBacklog = new HashSet<string>(_backlog);
                supplement = _lastByName.Values.OrderBy(v => v.seq)
                    .Select(v => v.json).Where(j => !inBacklog.Contains(j)).ToList();
                backlog = _backlog.ToList();
            }
            foreach (var line in supplement) _ = SendTo(client, line);
            foreach (var line in backlog) _ = SendTo(client, line);
        }

        /// <summary>Broadcast an event JSON line to all connected clients. <paramref name="name"/> is
        /// the event name (for the per-event snapshot); parsed from the JSON if not supplied.</summary>
        public void Broadcast(string json, string name = null)
        {
            lock (_backlogLock)
            {
                _backlog.Enqueue(json);
                while (_backlog.Count > BacklogMax) _backlog.Dequeue();
                var key = name ?? TryGetName(json);
                if (key != null) _lastByName[key] = (++_seq, json);
            }
            foreach (var kv in _clients)
                _ = SendTo(kv.Value, json);
        }

        private static string TryGetName(string json)
        {
            try { return JObject.Parse(json).Value<string>("name"); } catch { return null; }
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
