// Overwolf Game Events (GEP) bridge for Call of Duty (game id 27860).
// GEP is unreliable and only sometimes fires — we relay it into the plugin as a low-confidence
// HINT that corroborates our own network/CV events. Never depend on it.
const COD_GEP_ID = 27860;

function registerCodGep(onGepEvent) {
  const required = ['match_info', 'game_info'];

  overwolf.games.events.setRequiredFeatures(required, (info) => {
    console.log('[wzh][gep] setRequiredFeatures:', JSON.stringify(info));
  });

  overwolf.games.events.onNewEvents.removeListener(handleEvents);
  overwolf.games.events.onInfoUpdates2.removeListener(handleInfo);
  overwolf.games.events.onNewEvents.addListener(handleEvents);
  overwolf.games.events.onInfoUpdates2.addListener(handleInfo);

  function handleEvents(e) {
    if (!e || !e.events) return;
    for (const ev of e.events) onGepEvent(ev.name, JSON.stringify(ev.data ?? null));
  }
  function handleInfo(e) {
    if (!e || !e.info || !e.info.game_info) return;
    const gi = e.info.game_info;
    if (gi.scene !== undefined) onGepEvent('scene', String(gi.scene));
    if (gi.mode !== undefined) onGepEvent('mode', String(gi.mode));
  }
}

window.registerCodGep = registerCodGep;
window.COD_GEP_ID = COD_GEP_ID;
