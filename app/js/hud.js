// Game Helper — HUD: one compact line of "Label: value" (gray label / white value). Auto-sizes
// to content, hides fields that have no value yet.
let selfWindowId = null;
overwolf.windows.getCurrentWindow((r) => { if (r.status === 'success') selfWindowId = r.window.id; });
document.getElementById('close').onclick = () => selfWindowId && overwolf.windows.hide(selfWindowId, () => {});

const els = {
  game: document.getElementById('s-game'), match: document.getElementById('s-match'),
  players: document.getElementById('s-players'), squad: document.getElementById('s-squad'),
  server: document.getElementById('s-server'),
  ping: document.getElementById('s-ping'),
  lobby: document.getElementById('s-lobby'), party: document.getElementById('s-party'),
  status: document.getElementById('s-status'),
};

// Player/squad counts. In a match the roster (PLAYER_* deltas, incl. enemies seen) is richest;
// out of match it's empty, so fall back to the party/match list counts — which is what the log
// shows (PARTY_LIST_CHANGED / MATCH_LIST_CHANGED). Keeps the HUD consistent with the log.
const roster = new Map();          // key -> team, from PLAYER_* deltas
let listSquad = null, listPlayers = null;   // last PARTY_LIST / MATCH_LIST counts
function updateCounts() {
  const teams = [...roster.values()];
  const rosterTotal = teams.length;
  const rosterSquad = teams.filter(t => t === 'self' || t === 'squad').length;
  setField(els.players, String(rosterTotal || listPlayers || 0));
  setField(els.squad, String(rosterSquad || listSquad || 0));
}
const bar = document.querySelector('.bar');

// Hide every field until it has a value; party code persists across matches.
Object.values(els).forEach((el) => { if (el) el.closest('.f').style.display = 'none'; });
// Party codes are exactly 5 uppercase alphanumerics; drop any stale/invalid cached value.
const PARTY_CODE_RE = /^[A-Z0-9]{5}$/;
const savedParty = localStorage.getItem('wzh_partycode');
if (savedParty && PARTY_CODE_RE.test(savedParty)) setField(els.party, savedParty);
else localStorage.removeItem('wzh_partycode');

function setField(el, text, cls) {
  if (!el) return;
  const has = text != null && text !== '' && text !== '—';
  el.closest('.f').style.display = has ? '' : 'none';
  if (has) { el.textContent = text; if (cls !== undefined) el.className = 'v ' + cls; }
  resize();
}
function setFieldHtml(el, html) {
  el.closest('.f').style.display = html ? '' : 'none';
  if (html) el.innerHTML = html;
  resize();
}

let resizeTimer = null;
function resize() {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => {
    if (!selfWindowId) return;
    const w = Math.ceil(bar.scrollWidth) + 4;
    const h = Math.ceil(bar.scrollHeight) + 4;
    overwolf.windows.changeSize({ window_id: selfWindowId, width: w, height: h }, () => {});
  }, 60);
}

function applyOpacity(v) { document.documentElement.style.setProperty('--bg-alpha', (v/100).toFixed(2)); }
applyOpacity(parseInt(localStorage.getItem('wzh_opacity') || '90', 10));

function flagImg(iso) {
  if (!iso || iso.length !== 2) return '';
  return `<img class="flag" src="https://flagcdn.com/20x15/${iso.toLowerCase()}.png" alt="${iso}" onerror="this.replaceWith('${iso} ')">`;
}

overwolf.windows.onMessageReceived.addListener((msg) => {
  if (msg.id === 'helper-event') update(msg.content.name, msg.content.data);
  else if (msg.id === 'agent-status' && !msg.content.connected) setField(els.game, 'agent offline');
  else if (msg.id === 'set-opacity') { applyOpacity(msg.content.v); localStorage.setItem('wzh_opacity', msg.content.v); }
});
try { const bg = overwolf.windows.getMainWindow(); if (bg && bg.wzh && bg.wzh.events) bg.wzh.events.forEach(e => update(e.name, e.data)); } catch {}

function update(name, d) {
  if (!d) return;
  switch (name) {
    case 'GAME_PROCESS_STARTED': setField(els.game, String((d.pids && d.pids[0]) || 'running')); break;
    case 'GAME_PROCESS_STOPPED': setField(els.game, 'closed'); roster.clear(); listSquad = listPlayers = null; updateCounts(); break;
    case 'MATCH_STATE_CHANGED': setField(els.match, d.inMatch ? 'in match' : 'lobby', d.inMatch ? 'ok' : 'warn'); break;
    case 'PLAYER_JOINED':
    case 'PLAYER_CHANGED': if (d.key) { roster.set(d.key, d.team); updateCounts(); } break;
    case 'PLAYER_LEFT': if (d.key) { roster.delete(d.key); updateCounts(); } break;
    case 'PARTY_LIST_CHANGED': listSquad = d.count; if (listPlayers == null) listPlayers = d.count; updateCounts(); break;
    case 'MATCH_LIST_CHANGED': listPlayers = d.count; updateCounts(); break;
    case 'GAME_SERVER_CONNECTED':
      setFieldHtml(els.server, `${flagImg(d.countryIso)}${d.city || d.countryIso || d.ip}${d.isLikelyVPN?' ⚠':''}`);
      setField(els.ping, d.pingMs >= 0 ? `${d.pingMs} ms` : (d.bytesPerSec ? `n/a · ${d.bytesPerSec} B/s` : 'n/a')); break;
    case 'GAME_SERVER_DISCONNECTED': setField(els.server, '—'); setField(els.ping, '—'); break;
    case 'PERF_STATS': {
      const net = d.latencyMs, game = d.gameLatencyMs;
      if (net != null || game != null) setField(els.ping, net != null ? `${net} ms${game != null ? ` (game ${game})` : ''}` : `game ${game} ms`);
      break;
    }
    case 'LOBBY_ID_CHANGED': setField(els.lobby, d.lobbyId); break;
    case 'PARTY_CODE_CHANGED':
      if (d.code && PARTY_CODE_RE.test(d.code)) { setField(els.party, d.code); localStorage.setItem('wzh_partycode', d.code); }
      break;
    case 'GAME_STATUS_CHANGED':
      if (d.ok === true || d.change === 'all_ok') setField(els.status, 'OK', 'ok');
      else if (d.activeIssues != null && d.change === 'summary') setField(els.status, `${d.activeIssues} issue${d.activeIssues===1?'':'s'}`, 'warn');
      else setField(els.status, `${d.gameTitle}: ${d.change}`, 'warn');
      break;
  }
}
