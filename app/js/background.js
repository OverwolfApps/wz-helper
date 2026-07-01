// Warzone Helper — background controller (WebSocket client).
//
// The app no longer loads the native DLL. Instead the elevated background agent
// (WarzoneHelper.Console.exe, run as an admin scheduled task) hosts a WebSocket server and does
// all the privileged work — including the ETW UDP game-server detection that Overwolf itself can't
// do unelevated. This window just connects, fans events out to the UI windows, and relays GEP
// hints back to the agent.

const WS_URL = 'ws://127.0.0.1:17999/';
const EVENT_BUFFER_MAX = 500;
const RECONNECT_MS = 3000;

const GEP_QUEUE_MAX = 100;

const state = {
  ws: null,
  connected: false,
  events: [],
  reconnectTimer: null,
  gepQueue: [],   // GEP hints captured while the socket is down, flushed on connect
};
window.wzh = state; // UI windows read recent events via overwolf.windows.getMainWindow()

function connect() {
  clearTimeout(state.reconnectTimer);
  try {
    const ws = new WebSocket(WS_URL);
    state.ws = ws;

    ws.onopen = () => {
      state.connected = true;
      console.log('[wzh] connected to agent', WS_URL);
      ws.send(JSON.stringify({ type: 'hello' })); // request backlog
      flushGepQueue();
      broadcast('agent-status', { connected: true });
    };

    ws.onmessage = (msg) => {
      let evt;
      try { evt = JSON.parse(msg.data); } catch { return; }
      onHelperEvent(evt.name, evt);
    };

    ws.onclose = () => {
      state.connected = false;
      broadcast('agent-status', { connected: false });
      scheduleReconnect();
    };
    ws.onerror = () => { try { ws.close(); } catch {} };
  } catch (e) {
    scheduleReconnect();
  }
}

function scheduleReconnect() {
  clearTimeout(state.reconnectTimer);
  state.reconnectTimer = setTimeout(connect, RECONNECT_MS);
}

function onHelperEvent(name, data) {
  const entry = { name, data: data.data || data, at: Date.now() };
  state.events.push(entry);
  if (state.events.length > EVENT_BUFFER_MAX) state.events.shift();
  broadcast('helper-event', entry);
}

function broadcast(id, content) {
  for (const win of ['desktop', 'in_game']) {
    overwolf.windows.sendMessage(win, id, content, () => {});
  }
}

// --- GEP hints relayed to the agent over the same socket -----------------------------------
// The Overwolf app is the only side that can observe GEP, so it ingests those events and forwards
// them into the agent's stream (which then logs and re-broadcasts them) — giving both sides access.
// Hints captured while the socket is down are queued and flushed on reconnect.
function sendGep(name, data) {
  const frame = JSON.stringify({ type: 'gep', name, data });
  if (state.connected && state.ws && state.ws.readyState === WebSocket.OPEN) {
    try { state.ws.send(frame); return; } catch (e) { /* fall through to queue */ }
  }
  state.gepQueue.push(frame);
  if (state.gepQueue.length > GEP_QUEUE_MAX) state.gepQueue.shift();
}

function flushGepQueue() {
  while (state.gepQueue.length && state.ws && state.ws.readyState === WebSocket.OPEN) {
    try { state.ws.send(state.gepQueue.shift()); }
    catch (e) { break; }
  }
}

function wireGep() {
  try {
    registerCodGep((gepName, gepData) => sendGep(gepName, gepData));
  } catch (e) { console.warn('[wzh] gep register failed', e); }
}

function main() {
  connect();
  wireGep();
  overwolf.windows.obtainDeclaredWindow('desktop', (r) => {
    if (r.status === 'success') overwolf.windows.restore(r.window.id, () => {});
  });
}

main();
