// Warzone Helper — Log window: event rows + filter/clear/opacity/resize. (HUD chips live in hud.js.)
let selfWindowId = null;
overwolf.windows.getCurrentWindow((r) => { if (r.status === 'success') selfWindowId = r.window.id; });

const ALL_EVENTS = [
  'HELPER_STARTED','HELPER_STOPPED','COD_PROCESS_STARTED','COD_PROCESS_STOPPED',
  'GAME_SERVER_CONNECTED','GAME_SERVER_DISCONNECTED','SERVICE_CONNECTED','SERVICE_DISCONNECTED',
  'COD_STATUS_CHANGED','MATCH_STARTED','MATCH_ENDED','SCENE_CHANGED','MODE_CHANGED',
  'DEPLOYED','HEALTH_CHANGED','PLAYER_DEAD','LOBBY_ID_CHANGED','CHAT_MESSAGE',
  'PARTY_LIST_CHANGED','MATCH_LIST_CHANGED','SPECTATING_PLAYER','PERF_STATS','KILLFEED_ENTRY',
  'PARTY_CODE_CHANGED','PLAYER_INSPECTED','PLAYER_JOINED','PLAYER_LEFT','PLAYER_CHANGED','LOG_FILE_CHANGED','CACHE_CHANGED',
];
const NAME_CLASS = {
  GAME_SERVER_CONNECTED:'game', GAME_SERVER_DISCONNECTED:'game', SERVICE_CONNECTED:'svc',
  SERVICE_DISCONNECTED:'svc', PLAYER_DEAD:'dead', HEALTH_CHANGED:'warn', COD_STATUS_CHANGED:'warn',
  CHAT_MESSAGE:'svc', PARTY_LIST_CHANGED:'game', MATCH_LIST_CHANGED:'warn', SPECTATING_PLAYER:'warn',
  PLAYER_JOINED:'game', PLAYER_LEFT:'dead', KILLFEED_ENTRY:'dead',
};

const LS_FILTERS = 'wzh_filters', LS_OPACITY = 'wzh_opacity';
let enabled = loadFilters();
function loadFilters() {
  try { const j = JSON.parse(localStorage.getItem(LS_FILTERS)); if (Array.isArray(j)) return new Set(j); } catch {}
  return new Set(ALL_EVENTS);
}
function saveFilters() { localStorage.setItem(LS_FILTERS, JSON.stringify([...enabled])); }

const logEl = document.getElementById('log');

const opacity = document.getElementById('opacity');
function applyOpacity(v) { document.documentElement.style.setProperty('--bg-alpha', (v/100).toFixed(2)); }
opacity.value = parseInt(localStorage.getItem(LS_OPACITY) || '92', 10);
applyOpacity(opacity.value);
opacity.addEventListener('input', () => {
  applyOpacity(opacity.value);
  localStorage.setItem(LS_OPACITY, opacity.value);
  // Drive the other windows' opacity too.
  for (const w of ['hud', 'players']) overwolf.windows.sendMessage(w, 'set-opacity', { v: opacity.value }, () => {});
});

const filterBtn = document.getElementById('filter-btn');
const filterPanel = document.getElementById('filter-panel');
const filterList = document.getElementById('filter-list');
filterBtn.onclick = () => filterPanel.classList.toggle('open');
document.addEventListener('click', (e) => { if (!filterPanel.contains(e.target) && e.target !== filterBtn) filterPanel.classList.remove('open'); });
for (const name of ALL_EVENTS) {
  const lbl = document.createElement('label');
  const cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = enabled.has(name); cb.dataset.name = name;
  cb.onchange = () => { cb.checked ? enabled.add(name) : enabled.delete(name); saveFilters(); applyFilter(); };
  lbl.appendChild(cb); lbl.appendChild(document.createTextNode(name));
  filterList.appendChild(lbl);
}
document.getElementById('filter-all').onclick = () => setAll(true);
document.getElementById('filter-none').onclick = () => setAll(false);
function setAll(on) { enabled = on ? new Set(ALL_EVENTS) : new Set(); filterList.querySelectorAll('input').forEach((cb) => cb.checked = on); saveFilters(); applyFilter(); }
function applyFilter() { logEl.querySelectorAll('.row').forEach((r) => { r.style.display = enabled.has(r.dataset.name) ? '' : 'none'; }); }

document.getElementById('clear').onclick = () => { logEl.innerHTML = ''; };
document.getElementById('min').onclick = () => selfWindowId && overwolf.windows.minimize(selfWindowId, () => {});
document.getElementById('close').onclick = () => selfWindowId && overwolf.windows.hide(selfWindowId, () => {});
const EDGE = { right:'Right', bottom:'Bottom', 'bottom-right':'BottomRight' };
document.querySelectorAll('.grip').forEach((g) => g.addEventListener('mousedown', () => { if (selfWindowId) overwolf.windows.dragResize(selfWindowId, EDGE[g.dataset.edge]); }));

// Reliable window drag for Overwolf (CSS -webkit-app-region:drag is unreliable here).
document.querySelector('header').addEventListener('mousedown', (e) => {
  if (e.target.closest('button, input, .dropdown')) return;   // don't drag when using controls
  if (selfWindowId) overwolf.windows.dragMove(selfWindowId);
});

overwolf.windows.onMessageReceived.addListener((msg) => {
  if (msg.id === 'helper-event') render(msg.content);
  else if (msg.id === 'agent-status') { const h = document.querySelector('header h1'); if (h) h.textContent = msg.content.connected ? '🎯 Warzone Helper — Log' : '🎯 Warzone Helper — Log (agent offline)'; }
});
try { const bg = overwolf.windows.getMainWindow(); if (bg && bg.wzh && bg.wzh.events) bg.wzh.events.forEach(render); } catch {}

function flagImg(iso) {
  if (!iso || iso.length !== 2) return '';
  return `<img class="flag" src="https://flagcdn.com/20x15/${iso.toLowerCase()}.png" alt="${iso}" onerror="this.replaceWith('${iso} ')">`;
}

function render(entry) {
  const { name, data, at } = entry;
  const row = document.createElement('div');
  row.className = 'row'; row.dataset.name = name;
  if (!enabled.has(name)) row.style.display = 'none';
  row.innerHTML =
    `<div class="time">${new Date(at).toLocaleTimeString()}</div>` +
    `<div class="name ${NAME_CLASS[name] || ''}">${name}</div>` +
    `<div class="data">${summarize(name, data)}</div>`;
  logEl.prepend(row);
  while (logEl.children.length > 500) logEl.removeChild(logEl.lastChild);
}

function summarize(name, d) {
  if (!d) return '';
  if (name.startsWith('GAME_SERVER') || name.startsWith('SERVICE')) {
    const geo = [d.city, d.countryIso].filter(Boolean).join(', ');
    const vpn = d.isLikelyVPN ? `⚠VPN?(${d.vpnReason})` : '';
    const ping = d.pingMs >= 0 ? `${d.pingMs}ms` : 'ping n/a';
    return `${flagImg(d.countryIso)}${d.ip}:${d.port} ${d.protocol} ${geo} ${ping} ${d.bytesPerSec?d.bytesPerSec+'B/s':''} ${d.asnOrg||''} ${vpn}`;
  }
  if (name === 'CHAT_MESSAGE') return `💬 [${d.channel}] ${d.name}: ${d.text}`;
  if (name === 'KILLFEED_ENTRY') return d.event ? `${d.player} ${d.event}` : `${d.killer} ☠ ${d.victim}`;
  if (name === 'PLAYER_JOINED' || name === 'PLAYER_CHANGED' || name === 'PLAYER_LEFT')
    return `${d.team} ${d.name}${d.level?'('+d.level+')':''} [${d.status}]`;
  if (name === 'PARTY_LIST_CHANGED' || name === 'MATCH_LIST_CHANGED')
    return `${d.count} players: ` + (d.members||[]).map(m => `${m.name}${m.level?'('+m.level+')':''}`).join(', ');
  if (name === 'SPECTATING_PLAYER') return `👁 ${d.name}${d.id?'#'+d.id:''}`;
  if (name === 'PARTY_CODE_CHANGED') return `party code ${d.code}`;
  if (name === 'PLAYER_INSPECTED') return `#${d.activisionId} ${d.platform||''} lvl ${d.level||'?'} ${d.rank||''} ${d.input||''}`;
  if (name === 'PERF_STATS') return [d.latencyMs!=null?`net ${d.latencyMs}ms`:'', d.gameLatencyMs!=null?`game ${d.gameLatencyMs}ms`:'', d.packetLossPct!=null?`loss ${d.packetLossPct}%`:'', d.fps!=null?`${d.fps}fps`:'', d.gpuTemp!=null?`gpu ${d.gpuTemp}°`:'', d.vramPct!=null?`vram ${d.vramPct}%`:''].filter(Boolean).join('  ');
  if (name === 'HEALTH_CHANGED') return `health ${Math.round((d.health||0)*100)}%`;
  if (name === 'LOBBY_ID_CHANGED') return `lobby ${d.lobbyId}`;
  if (name === 'COD_STATUS_CHANGED') return d.ok ? 'all OK' : (d.activeIssues!=null ? `${d.activeIssues} issue(s)` : `${d.gameTitle}: ${d.change}`);
  if (name === 'LOG_FILE_CHANGED') return d.line ? d.line.slice(0,160) : (d.path||'');
  return JSON.stringify(d).slice(0,160);
}
