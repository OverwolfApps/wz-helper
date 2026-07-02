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
const FRAME_INTERVAL_MS = 800;   // how often we push a game frame to the agent for OCR

const state = {
  ws: null,
  connected: false,
  events: [],
  reconnectTimer: null,
  gepQueue: [],   // GEP hints captured while the socket is down, flushed on connect
  frameTimer: null,
  frameBusy: false,
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
  for (const win of ['log', 'hud', 'in_game', 'players']) {
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

// --- Game-only frame capture -> agent -----------------------------------------------------
// Overwolf's screenshot API captures the GAME surface, NOT our overlay windows, so the agent
// OCRs the game only (no feedback loop from our own overlays). GDI on the agent would grab them.
function startFramePush() {
  if (state.frameTimer) return;
  state.frameTimer = setInterval(captureAndPush, FRAME_INTERVAL_MS);
}

function captureAndPush() {
  if (!state.connected || !state.ws || state.frameBusy) return;
  state.frameBusy = true;
  try {
    overwolf.media.getScreenshotUrl({ roundAwayFromZero: false }, (res) => {
      if (!res || res.status !== 'success' || !res.url) {
        state.frameFails = (state.frameFails || 0) + 1;
        if (state.frameFails === 1) console.warn('[wzh][frame] getScreenshotUrl failed: ' + JSON.stringify(res));
        // DX12/Vulkan games don't support in-memory screenshots — the agent uses GDI instead. Stop trying.
        if (state.frameFails >= 3 && state.frameTimer) {
          clearInterval(state.frameTimer); state.frameTimer = null;
          console.warn('[wzh][frame] disabling frame push (agent uses GDI capture for this game).');
        }
        state.frameBusy = false; return;
      }
      const img = new Image();
      img.onload = () => {
        try {
          const canvas = document.createElement('canvas');
          canvas.width = img.naturalWidth; canvas.height = img.naturalHeight;
          canvas.getContext('2d').drawImage(img, 0, 0);
          let dataUrl;
          try { dataUrl = canvas.toDataURL('image/jpeg', 0.7); }
          catch (e) { console.error('[wzh][frame] toDataURL failed (canvas tainted?): ' + e.message); state.frameBusy = false; return; }
          if (state.connected && state.ws && state.ws.readyState === WebSocket.OPEN) {
            state.ws.send(JSON.stringify({ type: 'frame', data: dataUrl }));
            if (!state.frameLogged) { state.frameLogged = true; console.log(`[wzh][frame] first frame sent ${img.naturalWidth}x${img.naturalHeight}, ${dataUrl.length} chars`); }
          }
        } catch (e) { console.error('[wzh][frame] draw failed: ' + e.message); }
        state.frameBusy = false;
      };
      img.onerror = () => { console.error('[wzh][frame] image load failed for ' + res.url); state.frameBusy = false; };
      img.src = res.url;
    });
  } catch (e) { console.error('[wzh][frame] getScreenshotUrl threw: ' + e.message); state.frameBusy = false; }
}

function main() {
  connect();
  wireGep();
  startFramePush();
  for (const w of ['log', 'hud', 'players']) {
    overwolf.windows.obtainDeclaredWindow(w, (r) => {
      if (r.status === 'success') overwolf.windows.restore(r.window.id, () => {});
    });
  }
}

main();
