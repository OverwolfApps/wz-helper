// Game Helper — HUD: one compact line of "Label: value" (gray label / white value). Auto-sizes
// to content, hides fields that have no value yet.
let selfWindowId = null;
overwolf.windows.getCurrentWindow((r) => { if (r.status === 'success') selfWindowId = r.window.id; });
const _close = document.getElementById('close');
if (_close) _close.onclick = () => selfWindowId && overwolf.windows.hide(selfWindowId, () => {});

const els = {
  game: document.getElementById('s-game'), match: document.getElementById('s-match'),
  players: document.getElementById('s-players'), squad: document.getElementById('s-squad'),
  server: document.getElementById('s-server'),
  ping: document.getElementById('s-ping'),
  lobby: document.getElementById('s-lobby'), party: document.getElementById('s-party'),
  status: document.getElementById('s-status'),
};

// Register the live-event listener FIRST — before any init that could throw — so the HUD always
// receives updates even if a later step fails. (The callback runs async, after sync init finishes,
// so the helpers/consts it uses are ready by then.)
overwolf.windows.onMessageReceived.addListener((msg) => {
  if (msg.id === 'helper-event') update(msg.content.name, msg.content.data);
  else if (msg.id === 'agent-status' && !msg.content.connected) setField(els.game, 'agent offline');
  else if (msg.id === 'set-opacity') { applyOpacity(msg.content.v); localStorage.setItem('wzh_opacity', msg.content.v); }
});

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
// Declared before the first setField() call below — setField() -> resize() reads resizeTimer, and
// the cached-party-code line runs before this point, so a `let` further down would throw a TDZ
// "Cannot access 'resizeTimer' before initialization" and abort the whole HUD script on startup.
let resizeTimer = null;

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

function resize() {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => {
    // selfWindowId is fetched async; during the startup backfill burst it may not be set yet.
    // Reschedule instead of dropping, or the widest content measured during startup is lost and the
    // window stays too small until some later event happens to widen the bar again.
    if (!selfWindowId) { resize(); return; }
    // .bar is width:max-content so it's never clipped by the window; getBoundingClientRect is the
    // true content width. Buffer covers sub-pixel rounding + DPI scaling so the last field/close
    // button never gets cut off.
    const r = bar.getBoundingClientRect();
    overwolf.windows.changeSize(
      { window_id: selfWindowId, width: Math.ceil(r.width) + 12, height: Math.ceil(r.height) + 4 }, () => {});
  }, 60);
}
// Re-fit whenever the bar's rendered size changes for ANY reason — a field shown/hidden, a value
// changed, a flag image finishing loading, font metrics settling. This is the catch-all that makes
// the auto-size reliable regardless of event timing.
try { new ResizeObserver(() => resize()).observe(bar); } catch {}
// Fallback for the async flag <img> loads (covered by ResizeObserver too, but harmless).
document.addEventListener('load', (e) => { if (e.target.tagName === 'IMG') resize(); }, true);

function applyOpacity(v) { document.documentElement.style.setProperty('--bg-alpha', (v/100).toFixed(2)); }
applyOpacity(parseInt(localStorage.getItem('wzh_opacity') || '90', 10));

function flagImg(iso) {
  if (!iso || iso.length !== 2) return '';
  return `<img class="flag" src="https://flagcdn.com/20x15/${iso.toLowerCase()}.png" alt="${iso}" onerror="this.replaceWith('${iso} ')">`;
}

// Backfill current state from the background ring buffer (runs after all consts are defined).
try {
  const bg = overwolf.windows.getMainWindow();
  const evs = bg && bg.wzh && (bg.wzh.backfill ? bg.wzh.backfill() : bg.wzh.events);
  console.log(`[hud] window loaded; backfilled ${evs ? evs.length : 0} events`);
  if (evs) evs.forEach(e => update(e.name, e.data));
} catch (e) { console.error('[hud] backfill failed:', e && e.message); }

function update(name, d) {
  if (!d) return;
  switch (name) {
    case 'GAME_PROCESS_STARTED': setField(els.game, String((d.pids && d.pids[0]) || 'running')); break;
    case 'GAME_PROCESS_STOPPED': setField(els.game, 'closed'); roster.clear(); listSquad = listPlayers = null; updateCounts(); break;
    case 'MATCH_STATE_CHANGED': {
      const label = { searching: 'searching', found: 'lobby', started: 'in match', ended: 'ended' }[d.phase]
        || (d.inMatch ? 'in match' : 'lobby');
      setField(els.match, label, d.inMatch ? 'ok' : 'warn'); break;
    }
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
