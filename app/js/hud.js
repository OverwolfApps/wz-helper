// Warzone Helper — HUD: one line of "Label: value" (gray label / white value), in-game-metrics style.
let selfWindowId = null;
overwolf.windows.getCurrentWindow((r) => { if (r.status === 'success') selfWindowId = r.window.id; });
document.getElementById('close').onclick = () => selfWindowId && overwolf.windows.hide(selfWindowId, () => {});

const els = {
  game: document.getElementById('s-game'), server: document.getElementById('s-server'),
  ping: document.getElementById('s-ping'), health: document.getElementById('s-health'),
  lobby: document.getElementById('s-lobby'), party: document.getElementById('s-party'),
  status: document.getElementById('s-status'),
};

// Party code persists across matches until it changes.
els.party.textContent = localStorage.getItem('wzh_partycode') || '—';

function applyOpacity(v) { document.documentElement.style.setProperty('--bg-alpha', (v/100).toFixed(2)); }
applyOpacity(parseInt(localStorage.getItem('wzh_opacity') || '90', 10));

function flagImg(iso) {
  if (!iso || iso.length !== 2) return '';
  return `<img class="flag" src="https://flagcdn.com/20x15/${iso.toLowerCase()}.png" alt="${iso}" onerror="this.replaceWith('${iso} ')">`;
}

overwolf.windows.onMessageReceived.addListener((msg) => {
  if (msg.id === 'helper-event') update(msg.content.name, msg.content.data);
  else if (msg.id === 'agent-status' && !msg.content.connected) els.game.textContent = 'agent offline';
  else if (msg.id === 'set-opacity') { applyOpacity(msg.content.v); localStorage.setItem('wzh_opacity', msg.content.v); }
});
try { const bg = overwolf.windows.getMainWindow(); if (bg && bg.wzh && bg.wzh.events) bg.wzh.events.forEach(e => update(e.name, e.data)); } catch {}

function update(name, d) {
  if (!d) return;
  switch (name) {
    case 'COD_PROCESS_STARTED': els.game.textContent = 'running'; break;
    case 'COD_PROCESS_STOPPED': els.game.textContent = 'closed'; break;
    case 'GAME_SERVER_CONNECTED':
      els.server.innerHTML = `${flagImg(d.countryIso)}${d.city || d.countryIso || d.ip}${d.isLikelyVPN?' ⚠':''}`;
      els.ping.textContent = d.pingMs >= 0 ? `${d.pingMs} ms` : (d.bytesPerSec ? `n/a · ${d.bytesPerSec} B/s` : 'n/a'); break;
    case 'GAME_SERVER_DISCONNECTED': els.server.textContent = '—'; els.ping.textContent = '—'; break;
    case 'PERF_STATS': {
      const net = d.latencyMs, game = d.gameLatencyMs;
      if (net != null || game != null) els.ping.textContent = net != null ? `${net} ms${game != null ? ` (game ${game})` : ''}` : `game ${game} ms`;
      break;
    }
    case 'HEALTH_CHANGED': els.health.textContent = `${Math.round((d.health||0)*100)}%`; break;
    case 'PLAYER_DEAD': els.health.textContent = 'DEAD'; break;
    case 'LOBBY_ID_CHANGED': els.lobby.textContent = d.lobbyId; break;
    case 'PARTY_CODE_CHANGED':
      if (d.code) { els.party.textContent = d.code; localStorage.setItem('wzh_partycode', d.code); }
      break;
    case 'COD_STATUS_CHANGED':
      if (d.ok === true || d.change === 'all_ok') { els.status.textContent = 'OK'; els.status.className = 'v ok'; }
      else if (d.activeIssues != null && d.change === 'summary') { els.status.textContent = `${d.activeIssues} issue${d.activeIssues===1?'':'s'}`; els.status.className = 'v warn'; }
      else { els.status.textContent = `${d.gameTitle}: ${d.change}`; els.status.className = 'v warn'; }
      break;
  }
}
