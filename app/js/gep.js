// Overwolf Game Events (GEP) bridge for Call of Duty (game id 27860).
// GEP is unreliable and only fires while in-game, so we (a) retry setRequiredFeatures until it
// succeeds, (b) re-arm whenever the game launches, and (c) forward every event/info-update to the
// caller, which relays them to the agent. Treated purely as low-confidence hints.
const GAME_GEP_ID = 27860;

function registerCodGep(onGepEvent) {
  // gep_internal exposes the GEP version_info; match_info/game_info are the CoD feature sets.
  const required = ['gep_internal', 'match_info', 'game_info'];
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
    const gi = e && e.gameInfo;
    const isCod = gi && Math.floor(gi.id / 10) === GAME_GEP_ID;
    if (!gi || !isCod) return;
    if (gi.isRunning && (e.runningChanged || e.gameChanged)) trySetFeatures();
    // Surface the GEP game launch/terminate (with the full gameInfo: pid, renderer, exe,
    // terminationUnixEpochTime, reason, ...) so consumers get game start/stop from GEP too.
    if (e.runningChanged)
      onGepEvent(gi.isRunning ? 'game_launched' : 'game_terminated', JSON.stringify(gi));
  }

  // Event + info listeners (idempotent).
  overwolf.games.events.onNewEvents.removeListener(handleEvents);
  overwolf.games.events.onInfoUpdates2.removeListener(handleInfo);
  overwolf.games.events.onNewEvents.addListener(handleEvents);
  overwolf.games.events.onInfoUpdates2.addListener(handleInfo);

  // Forward EVERY GEP event verbatim (match_start, match_end, kill, death, ...).
  function handleEvents(e) {
    if (!e || !e.events) return;
    for (const ev of e.events) onGepEvent(ev.name, JSON.stringify(ev.data ?? null));
  }

  // Forward EVERY info update. Keep scene/mode explicit (the agent derives match state from them;
  // CoD reports the mode as game_info.game_mode), and relay the whole info object as a generic
  // 'info' hint so nothing (version_info, match_info, ...) is lost.
  function handleInfo(e) {
    if (!e || !e.info) return;
    const gi = e.info.game_info;
    if (gi) {
      if (gi.scene !== undefined) onGepEvent('scene', String(gi.scene));
      const mode = gi.game_mode !== undefined ? gi.game_mode : gi.mode;
      if (mode !== undefined) onGepEvent('mode', String(mode));
    }
    onGepEvent('info', JSON.stringify(e.info));
  }

  // Kick off immediately in case the game is already running.
  trySetFeatures();
}

window.registerCodGep = registerCodGep;
window.GAME_GEP_ID = GAME_GEP_ID;
