// Players window — maintains the roster from PLAYER_JOINED/CHANGED/LEFT deltas and renders it
// self first, then own squad, then enemies; disconnected/dead grayed and sunk to the bottom.
let selfWindowId = null;
overwolf.windows.getCurrentWindow((r) => { if (r.status === 'success') selfWindowId = r.window.id; });

const players = new Map(); // key -> player object
const listEl = document.getElementById('list');
const countEl = document.getElementById('count');

document.getElementById('close').onclick = () => selfWindowId && overwolf.windows.hide(selfWindowId, () => {});
document.getElementById('min').onclick = () => selfWindowId && overwolf.windows.minimize(selfWindowId, () => {});
document.getElementById('grip').addEventListener('mousedown', () => {
  if (selfWindowId) overwolf.windows.dragResize(selfWindowId, 'BottomRight');
});

function applyOpacity(v) { document.documentElement.style.setProperty('--bg-alpha', (v/100).toFixed(2)); }
applyOpacity(parseInt(localStorage.getItem('wzh_opacity') || '90', 10));

overwolf.windows.onMessageReceived.addListener((msg) => {
  if (msg.id === 'set-opacity') { applyOpacity(msg.content.v); localStorage.setItem('wzh_opacity', msg.content.v); return; }
  if (msg.id !== 'helper-event') return;
  const { name, data } = msg.content;
  if (name === 'PLAYER_JOINED' || name === 'PLAYER_CHANGED') { players.set(data.key, data); render(); }
  else if (name === 'PLAYER_LEFT') { players.delete(data.key); render(); }
});

// Backfill from the background ring buffer on open.
try {
  const bg = overwolf.windows.getMainWindow();
  if (bg && bg.wzh && bg.wzh.events) {
    for (const e of bg.wzh.events) {
      if (e.name === 'PLAYER_JOINED' || e.name === 'PLAYER_CHANGED') players.set(e.data.key, e.data);
      else if (e.name === 'PLAYER_LEFT') players.delete(e.data.key);
    }
    render();
  }
} catch {}

const TEAM_ORDER = { self: 0, squad: 1, enemy: 2, unknown: 3 };
const TEAM_LABEL = { self: 'You', squad: 'Squad', enemy: 'Enemies', unknown: 'Lobby' };

function sortKey(p) {
  const disc = p.status === 'disconnected' ? 1 : 0;  // sink disconnected within their team
  return [disc, TEAM_ORDER[p.team] ?? 3, (p.name || '').toLowerCase()];
}

function render() {
  const arr = [...players.values()].sort((a, b) => {
    const ka = sortKey(a), kb = sortKey(b);
    for (let i = 0; i < ka.length; i++) { if (ka[i] < kb[i]) return -1; if (ka[i] > kb[i]) return 1; }
    return 0;
  });
  countEl.textContent = arr.length;

  listEl.innerHTML = '';
  let curTeam = null;
  for (const p of arr) {
    if (p.team !== curTeam) {
      curTeam = p.team;
      const s = document.createElement('div'); s.className = 'sec';
      s.textContent = TEAM_LABEL[p.team] || p.team;
      listEl.appendChild(s);
    }
    const row = document.createElement('div');
    row.className = `p ${p.team}${p.status === 'dead' ? ' dead' : ''}${p.status === 'disconnected' ? ' disconnected' : ''}`;
    const bits = [];
    if (p.rank) bits.push(p.rank);
    if (p.platform) bits.push(p.platform);
    if (p.banned) bits.push('BAN');
    if (p.status === 'disconnected') bits.push('DC');
    else if (p.status === 'dead') bits.push('☠');
    row.innerHTML =
      `<span class="lvl">${p.level != null ? p.level : ''}</span>` +
      `<span class="nm">${escapeHtml(p.name || p.key)}</span>` +
      `<span class="st">${escapeHtml(bits.join(' · '))}</span>`;
    listEl.appendChild(row);
  }
}

function escapeHtml(s) { return (s || '').replace(/[&<>"]/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;' }[c])); }
