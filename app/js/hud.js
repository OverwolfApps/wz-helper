// Warzone Helper — HUD: one compact line of "Label: value" (gray label / white value). Auto-sizes
// to content, hides fields that have no value yet.
let selfWindowId = null;
overwolf.windows.getCurrentWindow((r) => { if (r.status === 'success') selfWindowId = r.window.id; });
document.getElementById('close').onclick = () => selfWindowId && overwolf.windows.hide(selfWindowId, () => {});

const els = {
  game: document.getElementById('s-game'), server: document.getElementById('s-server'),
  ping: document.getElementById('s-ping'), health: document.getElementById('s-health'),
  lobby: document.getElementById('s-lobby'), party: document.getElementById('s-party'),
  status: document.getElementById('s-status'),
};
const bar = document.querySelector('.bar');

// Hide every field until it has a value; party code persists across matches.
Object.values(els).forEach((el) => { el.closest('.f').style.display = 'none'; });
const savedParty = localStorage.getItem('wzh_partycode');
if (savedParty) setField(els.party, savedParty);

function setField(el, text, cls) {
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
    case 'COD_PROCESS_STARTED': setField(els.game, 'running'); break;
    case 'COD_PROCESS_STOPPED': setField(els.game, 'closed'); break;
    case 'GAME_SERVER_CONNECTED':
      setFieldHtml(els.server, `${flagImg(d.countryIso)}${d.city || d.countryIso || d.ip}${d.isLikelyVPN?' ⚠':''}`);
      setField(els.ping, d.pingMs >= 0 ? `${d.pingMs} ms` : (d.bytesPerSec ? `n/a · ${d.bytesPerSec} B/s` : 'n/a')); break;
    case 'GAME_SERVER_DISCONNECTED': setField(els.server, '—'); setField(els.ping, '—'); break;
    case 'PERF_STATS': {
      const net = d.latencyMs, game = d.gameLatencyMs;
      if (net != null || game != null) setField(els.ping, net != null ? `${net} ms${game != null ? ` (game ${game})` : ''}` : `game ${game} ms`);
      break;
    }
    case 'HEALTH_CHANGED': setField(els.health, `${Math.round((d.health||0)*100)}%`); break;
    case 'PLAYER_DEAD': setField(els.health, 'DEAD'); break;
    case 'LOBBY_ID_CHANGED': setField(els.lobby, d.lobbyId); break;
    case 'PARTY_CODE_CHANGED':
      if (d.code) { setField(els.party, d.code); localStorage.setItem('wzh_partycode', d.code); }
      break;
    case 'COD_STATUS_CHANGED':
      if (d.ok === true || d.change === 'all_ok') setField(els.status, 'OK', 'ok');
      else if (d.activeIssues != null && d.change === 'summary') setField(els.status, `${d.activeIssues} issue${d.activeIssues===1?'':'s'}`, 'warn');
      else setField(els.status, `${d.gameTitle}: ${d.change}`, 'warn');
      break;
  }
}
