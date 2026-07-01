// Overwolf Game Events (GEP) bridge for Call of Duty (game id 27860).
// GEP is unreliable and only fires while in-game, so we (a) retry setRequiredFeatures until it
// succeeds, (b) re-arm whenever the game launches, and (c) forward every event/info-update to the
// caller, which relays them to the agent. Treated purely as low-confidence hints.
const COD_GEP_ID = 27860;

function registerCodGep(onGepEvent) {
  const required = ['match_info', 'game_info'];
  let featuresSet = false;
  let retryTimer = null;

  function trySetFeatures() {
    overwolf.games.events.setRequiredFeatures(required, (info) => {
      if (info && info.success) {
        featuresSet = true;
        clearTimeout(retryTimer);
        console.log('[wzh][gep] required features set:', JSON.stringify(info.supportedFeatures || required));
      } else {
        // Usually "Not in a game" — back off and retry; also re-armed on game launch below.
        featuresSet = false;
        clearTimeout(retryTimer);
        retryTimer = setTimeout(trySetFeatures, 5000);
      }
    });
  }

  // (Re)arm when a supported game starts running.
  overwolf.games.onGameInfoUpdated.removeListener(onGameInfo);
  overwolf.games.onGameInfoUpdated.addListener(onGameInfo);
  function onGameInfo(e) {
    const running = e && e.gameInfo && e.gameInfo.isRunning;
    const isCod = e && e.gameInfo && Math.floor(e.gameInfo.id / 10) === COD_GEP_ID;
    if (running && isCod && (e.runningChanged || e.gameChanged)) trySetFeatures();
  }

  // Event + info listeners (idempotent).
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

  // Kick off immediately in case the game is already running.
  trySetFeatures();
}

window.registerCodGep = registerCodGep;
window.COD_GEP_ID = COD_GEP_ID;
