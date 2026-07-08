// Game Helper — background controller (WebSocket client).
//
// The app no longer loads the native DLL. Instead the elevated background agent
// (GameHelper.Console.exe, run as an admin scheduled task) hosts a WebSocket server and does
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
  latest: new Map(),   // name -> most-recent entry; never evicted, so rare state (party code,
                       // server, match state) survives the ring's FIFO churn for late-opening windows
  reconnectTimer: null,
  gepQueue: [],   // GEP hints captured while the socket is down, flushed on connect
  frameTimer: null,
  frameBusy: false,
  centralSettings: null,
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
  state.latest.set(name, entry);   // keep the last of every type regardless of ring churn
  broadcast('helper-event', entry);
  maybeForwardNotification(name, entry.data);
}

// --- Optional: forward curated events to the shared "Notifications" Overwolf app -----------------
// When enabled (log window toggle → localStorage), a small curated set of events is POSTed to the
// Notifications app's local HTTP endpoint as on-screen toasts. Everything still flows to our own
// windows regardless; this is purely an extra sink. Off by default.
const NOTIFY_LS_ENABLED = 'wzh_notify_enabled', NOTIFY_LS_PORT = 'wzh_notify_port';

function maybeForwardNotification(name, d) {
  try {
    const s = state.centralSettings || {
      notifyEnabled: localStorage.getItem(NOTIFY_LS_ENABLED) === 'true',
      notifyPort: parseInt(localStorage.getItem(NOTIFY_LS_PORT) || '61234', 10) || 61234
    };
    if (!s.notifyEnabled) return;
    const n = notificationFor(name, d);
    if (!n) return;
    fetch(`http://localhost:${s.notifyPort}/notify`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ app: 'Game Helper', ...n }),
    }).catch(() => {});   // Notifications app not running → silently skip
  } catch {}
}

// Map a Game Helper event to a notification, or null to skip. Curated to the genuinely
// notification-worthy, low-frequency events (no per-frame CV / perf spam).
function notificationFor(name, d) {
  if (!d) return null;
  switch (name) {
    case 'GAME_SERVER_CONNECTED':
      if (!d.isLikelyVPN) return null;   // only warn on suspicious (VPN/proxied) game servers
      return { type: 'warning', title: 'VPN/proxied game server',
        message: [d.city || d.ip, d.pingMs >= 0 ? `${d.pingMs}ms` : '', d.asnOrg].filter(Boolean).join(' · ') };
    case 'MATCH_STATE_CHANGED':
      if (d.phase === 'started') return { type: 'success', title: 'Match started', message: '' };
      if (d.phase === 'ended')   return { type: 'info', title: 'Match ended', message: '' };
      return null;
    case 'PARTY_CODE_CHANGED':
      return d.code ? { type: 'info', title: 'Party code', message: d.code } : null;
    case 'GAME_STATUS_CHANGED':
      if (d.change === 'summary' && d.ok === false)
        return { type: 'warning', title: 'Call of Duty status', message: `${d.activeIssues} active issue${d.activeIssues === 1 ? '' : 's'}` };
      if (d.change === 'issue_started')
        return { type: 'warning', title: d.gameTitle || 'CoD status', message: 'Outage reported' };
      return null;
    case 'GAME_VERSION_CHANGED':
      return d.previous ? { type: 'info', title: 'Game updated', message: d.version || d.raw || '' } : null;
    default:
      return null;
  }
}

// Backfill for late-opening windows: the recent ring (for history/roster deltas) plus the last of
// every event type that the ring may have already evicted, chronologically ordered. A rare event
// (e.g. PARTY_CODE_CHANGED) is thus always replayed even if thousands of chatty events buried it.
function backfill() {
  const seen = new Set(state.events);
  const extra = [...state.latest.values()].filter(e => !seen.has(e));
  return extra.concat(state.events).sort((a, b) => a.at - b.at);
}
state.backfill = backfill;

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

// Ask the agent to replay buffered events we may have missed. max = a number (last N) or 'all'
// (default). Replayed events arrive on the normal onmessage path, so they render like live ones.
// UI windows call this via overwolf.windows.getMainWindow().wzh.requestEvents(N).
function requestEvents(max) {
  if (!state.ws || state.ws.readyState !== WebSocket.OPEN) return false;
  try { state.ws.send(JSON.stringify({ type: 'list_events', max: (max == null ? 'all' : max) })); return true; }
  catch (e) { return false; }
}
state.requestEvents = requestEvents;

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
  initCentralSettings();
  connect();
  wireGep();
  startFramePush();
  for (const w of ['log', 'hud', 'players']) {
    overwolf.windows.obtainDeclaredWindow(w, (r) => {
      if (r.status === 'success') overwolf.windows.restore(r.window.id, () => {});
    });
  }
}

const WZH_SCHEMA = [
  {
    key: "wzh_opacity",
    label: "HUD Opacity",
    description: "Adjust opacity/alpha level of the Game Helper overlays.",
    type: "slider",
    category: "HUD",
    min: 20,
    max: 100,
    step: 5,
    unit: "%",
    default: 90
  },
  {
    key: "wzh_notify_enabled",
    label: "Send Alerts to Notifications App",
    description: "Forward curated game events (squad connected, match start/end) to the shared Notifications app.",
    type: "checkbox",
    category: "Alerts",
    default: true
  },
  {
    key: "wzh_notify_port",
    label: "Notifications Service Port",
    description: "Port where the shared Notifications server is listening.",
    type: "number",
    category: "Alerts",
    default: 61234
  },
  {
    key: "wzh_partycode",
    label: "Squad Party Code",
    description: "Your active Warzone lobby/party code.",
    type: "text",
    category: "Gameplay",
    default: ""
  },
  {
    key: "autoLaunch",
    label: "Start with Overwolf",
    description: "Automatically start this app when the Overwolf client starts.",
    type: "checkbox",
    category: "Lifecycle",
    default: true
  },
  {
    key: "closeOnGameExit",
    label: "Close on Game Exit",
    description: "Shut down this app automatically when Call of Duty: Warzone is closed.",
    type: "checkbox",
    category: "Lifecycle",
    default: false
  }
];

function initCentralSettings() {
  const appName = "Warzone Helper";
  const regData = {
    app: appName,
    icon: "https://cdn.simpleicons.org/callofduty",
    settings: WZH_SCHEMA
  };

  const register = () => {
    fetch('http://localhost:61235/register', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(regData)
    }).then(res => {
      if (!res.ok) throw new Error();
      console.log(`[wz-helper] Registered schema successfully.`);
    }).catch(() => {
      setTimeout(register, 3000);
    });
  };
  register();

  overwolf.extensions.getExtensions((r) => {
    if (!r || !r.extensions) return;
    const sm = r.extensions.find(e => e.meta && e.meta.name === 'Settings Manager');
    if (!sm) return;

    const applyData = (infoStr) => {
      try {
        const apps = JSON.parse(infoStr);
        if (apps && apps[appName] && apps[appName].values) {
          const vals = apps[appName].values;
          
          const oldOpacity = state.centralSettings ? state.centralSettings.wzh_opacity : 90;
          const newOpacity = parseInt(vals.wzh_opacity, 10) || 90;

          const oldCode = state.centralSettings ? state.centralSettings.wzh_partycode : "";
          const newCode = vals.wzh_partycode || "";

          state.centralSettings = {
            wzh_opacity: newOpacity,
            notifyEnabled: vals.notifyEnabled !== false,
            notifyPort: parseInt(vals.notifyPort, 10) || 61234,
            wzh_partycode: newCode,
            autoLaunch: vals.autoLaunch !== false,
            closeOnGameExit: vals.closeOnGameExit === true
          };

          // If opacity changed, tell all windows
          if (newOpacity !== oldOpacity) {
            localStorage.setItem('wzh_opacity', String(newOpacity));
            for (const w of ['log', 'hud', 'players']) {
              overwolf.windows.sendMessage(w, 'set-opacity', { v: newOpacity }, () => {});
            }
          }

          // If party code changed, tell hud
          if (newCode !== oldCode) {
            localStorage.setItem('wzh_partycode', newCode);
            overwolf.windows.sendMessage('hud', 'set-partycode', { v: newCode }, () => {});
          }
        }
      } catch (err) {
        console.error('[wzh] failed to parse settings:', err);
      }
    };

    overwolf.extensions.getInfo(sm.id, (infoRes) => {
      if (infoRes && infoRes.status === 'success' && infoRes.info) {
        applyData(infoRes.info);
      }
    });

    overwolf.extensions.registerInfo(sm.id, (infoUpdate) => {
      if (infoUpdate) applyData(infoUpdate);
    });
  });
}

overwolf.windows.onMessageReceived.addListener((msg) => {
  if (msg.id === 'shutdown-app') {
    console.log('[wz-helper] Received shutdown command from Settings Manager.');
    window.close();
  } else if (msg.id === 'set-autostart') {
    console.log('[wz-helper] Received set-autostart command from Settings Manager:', msg.content.enabled);
    overwolf.settings.setExtensionSettings({ auto_launch_with_overwolf: msg.content.enabled !== false }, () => {});
  }
});

main();
