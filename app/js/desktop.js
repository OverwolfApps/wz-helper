// Warzone Helper — desktop dashboard. Live event stream, summary chips, saved event filters,
// saved transparency, and drag-resize for the Overwolf (non-native) window.

let selfWindowId = null;
overwolf.windows.getCurrentWindow((r) => { if (r.status === 'success') selfWindowId = r.window.id; });

const ALL_EVENTS = [
  'HELPER_STARTED','HELPER_STOPPED','COD_PROCESS_STARTED','COD_PROCESS_STOPPED',
  'GAME_SERVER_CONNECTED','GAME_SERVER_DISCONNECTED','SERVICE_CONNECTED','SERVICE_DISCONNECTED',
  'COD_STATUS_CHANGED','MATCH_STARTED','MATCH_ENDED','SCENE_CHANGED','MODE_CHANGED',
  'DEPLOYED','HEALTH_CHANGED','PLAYER_DEAD','LOBBY_ID_CHANGED','CHAT_MESSAGE',
  'PARTY_LIST_CHANGED','MATCH_LIST_CHANGED','SPECTATING_PLAYER','PERF_STATS','LOG_FILE_CHANGED','CACHE_CHANGED',
];
const NAME_CLASS = {
  GAME_SERVER_CONNECTED:'game', GAME_SERVER_DISCONNECTED:'game', SERVICE_CONNECTED:'svc',
  SERVICE_DISCONNECTED:'svc', PLAYER_DEAD:'dead', HEALTH_CHANGED:'warn', COD_STATUS_CHANGED:'warn',
  CHAT_MESSAGE:'svc', PARTY_LIST_CHANGED:'game', MATCH_LIST_CHANGED:'warn', SPECTATING_PLAYER:'warn',
};

// --- persisted settings -------------------------------------------------------------------
const LS_FILTERS = 'wzh_filters', LS_OPACITY = 'wzh_opacity';
let enabled = loadFilters();          // Set of enabled event names
function loadFilters() {
  try { const j = JSON.parse(localStorage.getItem(LS_FILTERS)); if (Array.isArray(j)) return new Set(j); } catch {}
  return new Set(ALL_EVENTS); // default: everything on
}
function saveFilters() { localStorage.setItem(LS_FILTERS, JSON.stringify([...enabled])); }

const els = {
  log: document.getElementById('log'),
  game: document.getElementById('s-game'), server: document.getElementById('s-server'),
  ping: document.getElementById('s-ping'), health: document.getElementById('s-health'),
  lobby: document.getElementById('s-lobby'), status: document.getElementById('s-status'),
};

// --- transparency slider ------------------------------------------------------------------
const opacity = document.getElementById('opacity');
function applyOpacity(v) { document.documentElement.style.setProperty('--bg-alpha', (v/100).toFixed(2)); }
opacity.value = parseInt(localStorage.getItem(LS_OPACITY) || '92', 10);
applyOpacity(opacity.value);
opacity.addEventListener('input', () => { applyOpacity(opacity.value); localStorage.setItem(LS_OPACITY, opacity.value); });

// --- filter dropdown ----------------------------------------------------------------------
const filterBtn = document.getElementById('filter-btn');
const filterPanel = document.getElementById('filter-panel');
const filterList = document.getElementById('filter-list');
filterBtn.onclick = () => filterPanel.classList.toggle('open');
document.addEventListener('click', (e) => {
  if (!filterPanel.contains(e.target) && e.target !== filterBtn) filterPanel.classList.remove('open');
});
for (const name of ALL_EVENTS) {
  const lbl = document.createElement('label');
  const cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = enabled.has(name); cb.dataset.name = name;
  cb.onchange = () => { cb.checked ? enabled.add(name) : enabled.delete(name); saveFilters(); applyFilter(); };
  lbl.appendChild(cb); lbl.appendChild(document.createTextNode(name));
  filterList.appendChild(lbl);
}
document.getElementById('filter-all').onclick = () => setAll(true);
document.getElementById('filter-none').onclick = () => setAll(false);
function setAll(on) {
  enabled = on ? new Set(ALL_EVENTS) : new Set();
  filterList.querySelectorAll('input').forEach((cb) => cb.checked = on);
  saveFilters(); applyFilter();
}
function applyFilter() {
  els.log.querySelectorAll('.row').forEach((r) => { r.style.display = enabled.has(r.dataset.name) ? '' : 'none'; });
}

// --- window controls ----------------------------------------------------------------------
document.getElementById('clear').onclick = () => { els.log.innerHTML = ''; };
document.getElementById('min').onclick = () => selfWindowId && overwolf.windows.minimize(selfWindowId, () => {});
document.getElementById('close').onclick = () => selfWindowId && overwolf.windows.hide(selfWindowId, () => {});

// Drag-resize grips (Overwolf non-native windows need explicit dragResize).
const EDGE = { right:'Right', bottom:'Bottom', 'bottom-right':'BottomRight' };
document.querySelectorAll('.grip').forEach((g) => {
  g.addEventListener('mousedown', () => {
    if (selfWindowId) overwolf.windows.dragResize(selfWindowId, EDGE[g.dataset.edge]);
  });
});

// --- event stream -------------------------------------------------------------------------
overwolf.windows.onMessageReceived.addListener((msg) => {
  if (msg.id === 'helper-event') render(msg.content);
  else if (msg.id === 'agent-status') setAgentStatus(msg.content.connected);
});
function setAgentStatus(connected) {
  const h = document.querySelector('header h1');
  if (h) h.textContent = connected ? '🎯 Warzone Helper' : '🎯 Warzone Helper — agent offline';
  if (!connected) els.game.textContent = 'agent offline';
}
try { const bg = overwolf.windows.getMainWindow(); if (bg && bg.wzh && bg.wzh.events) bg.wzh.events.forEach(render); } catch {}

function render(entry) {
  const { name, data, at } = entry;
  updateSummary(name, data);

  const row = document.createElement('div');
  row.className = 'row'; row.dataset.name = name;
  if (!enabled.has(name)) row.style.display = 'none';
  const t = new Date(at).toLocaleTimeString();
  row.innerHTML =
    `<div class="time">${t}</div>` +
    `<div class="name ${NAME_CLASS[name] || ''}">${name}</div>` +
    `<div class="data">${summarize(name, data)}</div>`;
  els.log.prepend(row);
  while (els.log.children.length > 500) els.log.removeChild(els.log.lastChild);
}

// Windows Chromium (Overwolf CEF) has no color flag-emoji font, so we use flagcdn images.
function flagImg(iso) {
  if (!iso || iso.length !== 2) return '';
  return `<img class="flag" src="https://flagcdn.com/20x15/${iso.toLowerCase()}.png" alt="${iso}" onerror="this.replaceWith('${iso} ')">`;
}

function summarize(name, d) {
  if (!d) return '';
  if (name.startsWith('GAME_SERVER') || name.startsWith('SERVICE')) {
    const geo = [d.city, d.countryIso].filter(Boolean).join(', ');
    const vpn = d.isLikelyVPN ? `⚠VPN?(${d.vpnReason})` : '';
    const ping = d.pingMs >= 0 ? `${d.pingMs}ms` : 'ping n/a';
    const rate = d.bytesPerSec ? `${d.bytesPerSec}B/s` : '';
    return `${flagImg(d.countryIso)}${d.ip}:${d.port} ${d.protocol} ${geo} ${ping} ${rate} ${d.asnOrg||''} ${vpn}`;
  }
  if (name === 'CHAT_MESSAGE') return `💬 [${d.channel}] ${d.name}: ${d.text}`;
  if (name === 'PARTY_LIST_CHANGED' || name === 'MATCH_LIST_CHANGED')
    return `${d.count} players: ` + (d.members||[]).map(m => `${m.name}${m.level?'('+m.level+')':''}`).join(', ');
  if (name === 'SPECTATING_PLAYER') return `👁 ${d.name}${d.id?'#'+d.id:''}`;
  if (name === 'PERF_STATS') return [
    d.gameLatencyMs!=null?`game ${d.gameLatencyMs}ms`:'', d.latencyMs!=null?`net ${d.latencyMs}ms`:'',
    d.packetLossPct!=null?`loss ${d.packetLossPct}%`:'', d.fps!=null?`${d.fps}fps`:'',
    d.gpuTemp!=null?`gpu ${d.gpuTemp}°`:'', d.vramPct!=null?`vram ${d.vramPct}%`:'', d.clock||''
  ].filter(Boolean).join('  ');
  if (name === 'HEALTH_CHANGED') return `health ${Math.round((d.health||0)*100)}%`;
  if (name === 'LOBBY_ID_CHANGED') return `lobby ${d.lobbyId}`;
  if (name === 'COD_STATUS_CHANGED') return `${d.gameTitle} [${d.platform}] ${d.change}`;
  if (name === 'LOG_FILE_CHANGED') return d.line ? d.line.slice(0,160) : (d.path||'');
  return JSON.stringify(d).slice(0,160);
}

function updateSummary(name, d) {
  switch (name) {
    case 'COD_PROCESS_STARTED': els.game.textContent = 'running'; break;
    case 'COD_PROCESS_STOPPED': els.game.textContent = 'closed'; break;
    case 'GAME_SERVER_CONNECTED':
      els.server.innerHTML = `${flagImg(d.countryIso)}${d.city || d.countryIso || d.ip}${d.isLikelyVPN?' ⚠':''}`;
      // Game servers block ICMP, so ping is usually n/a; show throughput as a secondary signal.
      els.ping.textContent = d.pingMs >= 0 ? `${d.pingMs} ms`
        : (d.bytesPerSec ? `n/a · ${d.bytesPerSec} B/s` : 'n/a'); break;
    case 'GAME_SERVER_DISCONNECTED': els.server.textContent = '—'; els.ping.textContent = '—'; break;
    case 'HEALTH_CHANGED': els.health.textContent = `${Math.round((d.health||0)*100)}%`; break;
    case 'PLAYER_DEAD': els.health.textContent = 'DEAD'; break;
    case 'LOBBY_ID_CHANGED': els.lobby.textContent = d.lobbyId; break;
    case 'COD_STATUS_CHANGED':
      if (d.ok === true || d.change === 'all_ok') {
        els.status.textContent = 'OK'; els.status.style.color = 'var(--game)';
      } else if (d.activeIssues != null && d.change === 'summary') {
        els.status.textContent = `${d.activeIssues} issue${d.activeIssues===1?'':'s'}`; els.status.style.color = 'var(--warn)';
      } else {
        els.status.textContent = `${d.gameTitle}: ${d.change}`; els.status.style.color = 'var(--warn)';
      }
      break;
    case 'PERF_STATS': {
      // Ping = network LATENCY (truer ping); show game latency alongside when present.
      const net = d.latencyMs, game = d.gameLatencyMs;
      if (net != null || game != null)
        els.ping.textContent = net != null
          ? `${net} ms${game != null ? ` (game ${game})` : ''}`
          : `game ${game} ms`;
      break;
    }
  }
}
