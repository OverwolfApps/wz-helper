// Minimal in-game overlay HUD. Consumes the same broadcast stream as the desktop window.
const el = (id) => document.getElementById(id);

overwolf.windows.onMessageReceived.addListener((msg) => {
  if (msg.id !== 'helper-event') return;
  const { name, data: d } = msg.content;
  switch (name) {
    case 'GAME_SERVER_CONNECTED':
      el('server').textContent = `${d.city || d.countryIso || d.ip}`;
      el('ping').textContent = d.pingMs >= 0 ? `${d.pingMs} ms` : '—';
      break;
    case 'GAME_SERVER_DISCONNECTED':
      el('server').textContent = '—'; el('ping').textContent = '—';
      break;
    case 'HEALTH_CHANGED':
      el('health').textContent = `${Math.round((d.health || 0) * 100)}%`;
      el('health').className = 'v';
      break;
    case 'PLAYER_DEAD':
      el('health').textContent = 'DEAD'; el('health').className = 'v dead';
      break;
    case 'LOBBY_ID_CHANGED':
      el('lobby').textContent = d.lobbyId;
      break;
  }
});
