// Warzone Helper — desktop dashboard. Renders the live event stream + summary chips.
let selfWindowId = null;

overwolf.windows.getCurrentWindow((r) => { if (r.status === 'success') selfWindowId = r.window.id; });

const els = {
  log: document.getElementById('log'),
  game: document.getElementById('s-game'),
  server: document.getElementById('s-server'),
  ping: document.getElementById('s-ping'),
  health: document.getElementById('s-health'),
  lobby: document.getElementById('s-lobby'),
  status: document.getElementById('s-status'),
};

const NAME_CLASS = {
  GAME_SERVER_CONNECTED: 'game', GAME_SERVER_DISCONNECTED: 'game',
  SERVICE_CONNECTED: 'svc', SERVICE_DISCONNECTED: 'svc',
  PLAYER_DEAD: 'dead', HEALTH_CHANGED: 'warn', COD_STATUS_CHANGED: 'warn',
  CHAT_MESSAGE: 'svc',
};

document.getElementById('clear').onclick = () => { els.log.innerHTML = ''; };
document.getElementById('close').onclick = () =>
  overwolf.windows.getCurrentWindow((r) => overwolf.windows.minimize(r.window.id, () => {}));

// Receive events broadcast by the background controller.
overwolf.windows.onMessageReceived.addListener((msg) => {
  if (msg.id === 'helper-event') render(msg.content);
});

// On open, backfill from the background ring buffer.
overwolf.windows.getMainWindow && backfill();
function backfill() {
  try {
    const bg = overwolf.windows.getMainWindow();
    if (bg && bg.wzh && bg.wzh.events) bg.wzh.events.forEach(render);
  } catch (e) { /* getMainWindow not available in this window type */ }
}

function render(entry) {
  const { name, data, at } = entry;
  updateSummary(name, data);

  const row = document.createElement('div');
  row.className = 'row';
  const t = new Date(at).toLocaleTimeString();
  const cls = NAME_CLASS[name] || '';
  row.innerHTML =
    `<div class="time">${t}</div>` +
    `<div class="name ${cls}">${name}</div>` +
    `<div class="data">${summarizeData(name, data)}</div>`;
  els.log.prepend(row);
  while (els.log.children.length > 400) els.log.removeChild(els.log.lastChild);
}

function summarizeData(name, d) {
  if (!d) return '';
  if (name.startsWith('GAME_SERVER') || name.startsWith('SERVICE')) {
    const geo = [d.city, d.countryIso].filter(Boolean).join(', ');
    const dist = d.distanceKm != null ? `${d.distanceKm}km` : '';
    const vpn = d.isLikelyVPN ? `⚠ VPN?(${d.vpnReason})` : '';
    return `${d.ip}:${d.port} ${d.protocol} ${flag(d.countryIso)} ${geo} ${d.pingMs >= 0 ? d.pingMs + 'ms' : ''} ${dist} ${d.asnOrg || ''} ${vpn}`;
  }
  if (name === 'CHAT_MESSAGE') return `💬 ${d.text}`;
  if (name === 'HEALTH_CHANGED') return `health ${Math.round((d.health || 0) * 100)}%`;
  if (name === 'LOBBY_ID_CHANGED') return `lobby ${d.lobbyId}`;
  if (name === 'COD_STATUS_CHANGED') return `${d.gameTitle} [${d.platform}] ${d.change}`;
  if (name === 'LOG_FILE_CHANGED') return d.line ? d.line.slice(0, 160) : (d.path || '');
  return JSON.stringify(d).slice(0, 160);
}

function updateSummary(name, d) {
  switch (name) {
    case 'COD_PROCESS_STARTED': els.game.textContent = 'running'; break;
    case 'COD_PROCESS_STOPPED': els.game.textContent = 'closed'; break;
    case 'GAME_SERVER_CONNECTED':
      els.server.textContent = `${flag(d.countryIso)} ${d.city || d.countryIso || d.ip}${d.isLikelyVPN ? ' ⚠VPN?' : ''}`;
      els.ping.textContent = d.pingMs >= 0 ? `${d.pingMs} ms` : '—';
      break;
    case 'GAME_SERVER_DISCONNECTED': els.server.textContent = '—'; els.ping.textContent = '—'; break;
    case 'HEALTH_CHANGED': els.health.textContent = `${Math.round((d.health || 0) * 100)}%`; break;
    case 'PLAYER_DEAD': els.health.textContent = 'DEAD'; break;
    case 'LOBBY_ID_CHANGED': els.lobby.textContent = d.lobbyId; break;
    case 'COD_STATUS_CHANGED': els.status.textContent = `${d.gameTitle}: ${d.change}`; break;
  }
}

function flag(iso) {
  if (!iso || iso.length !== 2) return '';
  return String.fromCodePoint(...[...iso.toUpperCase()].map((c) => 0x1f1e6 + c.charCodeAt(0) - 65));
}
