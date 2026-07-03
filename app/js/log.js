// Game Helper — Log window: event rows + filter/clear/opacity/resize. (HUD chips live in hud.js.)
let selfWindowId = null;
overwolf.windows.getCurrentWindow((r) => { if (r.status === 'success') selfWindowId = r.window.id; });

// Fallback list used only until the agent's HELPER_STARTED catalog arrives (then it's replaced by
// the self-described event names, so nothing here is hardcoded long-term).
let ALL_EVENTS = [
  'HELPER_STARTED','HELPER_STOPPED','GAME_PROCESS_STARTED','GAME_PROCESS_STOPPED',
  'GAME_SERVER_CONNECTED','GAME_SERVER_DISCONNECTED','SERVICE_CONNECTED','SERVICE_DISCONNECTED',
  'GAME_STATUS_CHANGED','MATCH_STARTED','MATCH_ENDED','SCENE_CHANGED','MODE_CHANGED',
  'DEPLOYED','HEALTH_CHANGED','PLAYER_DEAD','LOBBY_ID_CHANGED','CHAT_MESSAGE',
  'PARTY_LIST_CHANGED','MATCH_LIST_CHANGED','SPECTATING_PLAYER','PERF_STATS','KILLFEED_ENTRY',
  'PARTY_CODE_CHANGED','PLAYER_INSPECTED','PLAYER_JOINED','PLAYER_LEFT','PLAYER_CHANGED',
  'LOG_FILE_ADDED','LOG_FILE_REMOVED','LOG_LINE_ADDED',
];
let EVENT_DOCS = {};   // name -> { description, fields:[{name,type,description}] } from the catalog
const LS_EVENTS_HASH = 'wzh_events_hash';
const NAME_CLASS = {
  GAME_SERVER_CONNECTED:'game', GAME_SERVER_DISCONNECTED:'game', SERVICE_CONNECTED:'svc',
  SERVICE_DISCONNECTED:'svc', PLAYER_DEAD:'dead', HEALTH_CHANGED:'warn', GAME_STATUS_CHANGED:'warn',
  CHAT_MESSAGE:'svc', PARTY_LIST_CHANGED:'game', MATCH_LIST_CHANGED:'warn', SPECTATING_PLAYER:'warn',
  PLAYER_JOINED:'game', PLAYER_LEFT:'dead', KILLFEED_ENTRY:'dead',
};

const LS_DISABLED = 'wzh_disabled', LS_OPACITY = 'wzh_opacity';
// We persist only the DISABLED events (unchecked). Everything else is on — so new events default
// on and the user's unchecks survive restarts. Stored as ["EVENT_A","EVENT_B", ...].
let disabled = loadDisabled();
function loadDisabled() {
  try { const j = JSON.parse(localStorage.getItem(LS_DISABLED)); if (Array.isArray(j)) return new Set(j); } catch {}
  return new Set();
}
function saveDisabled() { localStorage.setItem(LS_DISABLED, JSON.stringify([...disabled])); }
function isEnabled(name) { return !disabled.has(name); }

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
function buildFilterList() {
  filterList.innerHTML = '';
  for (const name of [...ALL_EVENTS].sort((a, b) => a.localeCompare(b))) {
    const lbl = document.createElement('label');
    const doc = EVENT_DOCS[name];
    if (doc && doc.description) lbl.title = doc.description;   // hover shows the event's description
    const cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = isEnabled(name); cb.dataset.name = name;
    cb.onchange = () => { cb.checked ? disabled.delete(name) : disabled.add(name); saveDisabled(); applyFilter(); };
    lbl.appendChild(cb); lbl.appendChild(document.createTextNode(name));
    filterList.appendChild(lbl);
  }
}
buildFilterList();

// Adopt the agent's self-described event catalog (from HELPER_STARTED): rebuild the event list +
// filter from it, and flag when the schema changed since the last run this window saw.
function applyCatalog(data) {
  const docs = Array.isArray(data.events) ? data.events : [];
  if (!docs.length) return;
  EVENT_DOCS = {}; for (const d of docs) EVENT_DOCS[d.name] = d;
  ALL_EVENTS = docs.map(d => d.name);
  // Nothing to reconcile: unchecked events live in `disabled`, everything else (incl. new events)
  // is on by default.
  buildFilterList();
  applyFilter();

  const prev = localStorage.getItem(LS_EVENTS_HASH);
  if (data.eventsHash && prev && prev !== data.eventsHash)
    noticeRow(`event schema changed (${docs.length} events, ${prev} → ${data.eventsHash})`);
  if (data.eventsHash) localStorage.setItem(LS_EVENTS_HASH, data.eventsHash);
}

function noticeRow(text) {
  const row = document.createElement('div');
  row.className = 'row'; row.dataset.name = '_notice';
  row.innerHTML = `<div class="time">${new Date().toLocaleTimeString()}</div>` +
    `<div class="name warn">SCHEMA</div><div class="data">${text}</div>`;
  logEl.prepend(row);
}
document.getElementById('filter-all').onclick = () => setAll(true);
document.getElementById('filter-none').onclick = () => setAll(false);
function setAll(on) { disabled = on ? new Set() : new Set(ALL_EVENTS); filterList.querySelectorAll('input').forEach((cb) => cb.checked = on); saveDisabled(); applyFilter(); }
function applyFilter() { logEl.querySelectorAll('.row').forEach((r) => { r.style.display = isEnabled(r.dataset.name) ? '' : 'none'; }); }

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
  else if (msg.id === 'agent-status') { const h = document.querySelector('header h1'); if (h) h.textContent = msg.content.connected ? '🎯 Game Helper — Log' : '🎯 Game Helper — Log (agent offline)'; }
});
try { const bg = overwolf.windows.getMainWindow(); if (bg && bg.wzh && bg.wzh.events) bg.wzh.events.forEach(render); } catch {}

function flagImg(iso) {
  if (!iso || iso.length !== 2) return '';
  return `<img class="flag" src="https://flagcdn.com/20x15/${iso.toLowerCase()}.png" alt="${iso}" onerror="this.replaceWith('${iso} ')">`;
}

// Endpoint URL scheme: UDP stays udp; TCP becomes https/http on 443/80, else tcp.
function endpointScheme(d) {
  const p = (d.protocol || '').toUpperCase();
  if (p === 'UDP') return 'udp';
  if (p === 'TCP') return d.port === 443 ? 'https' : d.port === 80 ? 'http' : 'tcp';
  return (d.protocol || '?').toLowerCase();
}

function render(entry) {
  const { name, data, at } = entry;
  if (name === 'HELPER_STARTED' && data && Array.isArray(data.events)) applyCatalog(data);
  const row = document.createElement('div');
  row.className = 'row'; row.dataset.name = name;
  if (!isEnabled(name)) row.style.display = 'none';
  row.innerHTML =
    `<div class="time">${new Date(at).toLocaleTimeString()}</div>` +
    `<div class="name ${NAME_CLASS[name] || ''}">${name}</div>` +
    `<div class="data">${summarize(name, data)}</div>`;
  logEl.prepend(row);
  while (logEl.children.length > 500) logEl.removeChild(logEl.lastChild);
}

function summarize(name, d) {
  if (!d) return '';
  if (name === 'HELPER_STARTED')
    return `🚀 agent online · v${d.version||'?'} · game: ${d.game||'?'} · ${d.eventCount||(d.events||[]).length} events`;
  if (name.startsWith('GAME_SERVER') || name.startsWith('SERVICE')) {
    // flag scheme://ip:port | loc | 10ms | 50kb/s | ORG/ASN
    return [
      `${flagImg(d.countryIso)}${endpointScheme(d)}://${d.ip}:${d.port}`,
      [d.city, d.countryIso].filter(Boolean).join(', '),
      d.pingMs >= 0 ? `${d.pingMs}ms` : 'n/a',
      (d.peakBytesPerSec ?? d.bytesPerSec) != null ? `${Math.round((d.peakBytesPerSec ?? d.bytesPerSec) / 1000)}kb/s` : '',
      [d.asnOrg, d.asn != null ? 'AS' + d.asn : ''].filter(Boolean).join('/'),
      d.isLikelyVPN ? `⚠VPN?(${d.vpnReason})` : '',
    ].filter(Boolean).join(' | ');
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
  if (name === 'GAME_STATUS_CHANGED') return d.ok ? 'all OK' : (d.activeIssues!=null ? `${d.activeIssues} issue(s)` : `${d.gameTitle}: ${d.change}`);
  if (name === 'LOG_LINE_ADDED') return (d.level ? `[${d.level}] ` : '') + (d.line ? d.line.slice(0,160) : '');
  if (name === 'LOG_FILE_ADDED' || name === 'LOG_FILE_REMOVED') return d.path || '';
  if (name === 'MATCH_STATE_CHANGED') return d.inMatch ? '▶ IN MATCH' : '⏹ not in match';
  if (name === 'GAME_VERSION_CHANGED') {
    const parts = [d.version, d.changelist && ('cl ' + d.changelist), d.epoch && new Date(+d.epoch * 1000).toLocaleDateString()].filter(Boolean).join(' · ');
    return `🏷 ${parts || d.raw || ''}${d.previous ? ' (updated)' : ''}`;
  }
  if (name === 'GAME_PROCESS_STARTED') {
    const mb = d.sizeBytes ? Math.round(d.sizeBytes / 1048576) + 'MB' : '';
    const mod = d.modifiedUtc ? new Date(d.modifiedUtc).toLocaleDateString() : '';
    return ['running', d.exe ? d.exe.split(/[\\/]/).pop() : '', d.fileVersion ? 'v' + d.fileVersion : '', mb, mod].filter(Boolean).join(' · ');
  }
  if (name === 'SCENE_CHANGED') return `scene → ${d.raw}`;
  if (name === 'MODE_CHANGED') return `mode → ${d.raw}`;
  if (name === 'GEP_INFO') return (d.raw || '').slice(0, 140);
  if (name === 'GAME_LAUNCHED' || name === 'GAME_TERMINATED') {
    try { const g = JSON.parse(d.raw || '{}'); return `${g.title || g.displayName || ''} pid ${g.processId || '?'}${g.reason ? ' · ' + g.reason.join(',') : ''}`; }
    catch { return d.gepName || ''; }
  }
  return JSON.stringify(d).slice(0,160);
}
