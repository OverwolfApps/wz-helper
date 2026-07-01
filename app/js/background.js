// Warzone Helper — background controller.
// Owns the plugin lifecycle, fans events out to UI windows, relays GEP hints, and (optionally)
// pushes Overwolf in-memory screenshots into the plugin so CV works in exclusive fullscreen
// where a native GDI grab would be black.

const PLUGIN_NAME = 'warzone-helper';
const EVENT_BUFFER_MAX = 500;

const state = {
  plugin: null,
  events: [],          // ring buffer of recent events
  started: false,
  pushLoop: null,
  // When true, we feed frames from overwolf.media instead of the plugin self-capturing.
  pushFramesFromOverwolf: true,
  pushIntervalMs: 1000,
};

window.wzh = state; // let UI windows read via overwolf.windows.getMainWindow()

async function main() {
  const owp = new OverwolfPlugin(PLUGIN_NAME, true);
  const loaded = await owp.initialize();
  if (!loaded) { console.error('[wzh] plugin failed to load'); return; }
  state.plugin = owp.get();

  // Structured events: (name, jsonPayload)
  state.plugin.onEvent.addListener((name, json) => {
    let data = null;
    try { data = JSON.parse(json); } catch { data = { raw: json }; }
    onHelperEvent(name, data);
  });
  state.plugin.onLog.addListener((msg) => console.log('[wzh][plugin]', msg));

  // Start monitoring (null => use config.json beside the DLL / defaults).
  state.plugin.start(null, (res) => {
    console.log('[wzh] start:', res);
    state.started = true;
  });

  // GEP hints (best-effort).
  try {
    registerCodGep((gepName, gepData) => {
      if (state.plugin) state.plugin.reportGepEvent(gepName, gepData, () => {});
    });
  } catch (e) { console.warn('[wzh] gep register failed', e); }

  // Frame pushing for CV.
  if (state.pushFramesFromOverwolf) startFramePushLoop();

  // Open the desktop window on launch.
  overwolf.windows.obtainDeclaredWindow('desktop', (r) => {
    if (r.status === 'success') overwolf.windows.restore(r.window.id, () => {});
  });
}

function onHelperEvent(name, data) {
  const entry = { name, data, at: Date.now() };
  state.events.push(entry);
  if (state.events.length > EVENT_BUFFER_MAX) state.events.shift();

  // Broadcast to open UI windows.
  broadcast('helper-event', entry);
  console.log('[wzh][event]', name, JSON.stringify(data));
}

function broadcast(id, content) {
  for (const win of ['desktop', 'in_game']) {
    overwolf.windows.sendMessage(win, id, content, () => {});
  }
}

// --- Overwolf in-memory screenshot -> plugin.pushFrame -------------------------------------
function startFramePushLoop() {
  if (state.pushLoop) return;
  state.pushLoop = setInterval(captureAndPush, state.pushIntervalMs);
}

function captureAndPush() {
  if (!state.plugin) return;
  const params = { roundAwayFromZero: false }; // MemoryScreenshotParams; full-frame capture
  try {
    overwolf.media.getScreenshotUrl(params, (result) => {
      if (!result || result.status !== 'success' || !result.url) return;
      // Fetch the in-memory screenshot and re-encode to base64 for the plugin.
      const img = new Image();
      img.onload = () => {
        try {
          const canvas = document.createElement('canvas');
          canvas.width = img.naturalWidth;
          canvas.height = img.naturalHeight;
          canvas.getContext('2d').drawImage(img, 0, 0);
          const dataUrl = canvas.toDataURL('image/jpeg', 0.6);
          state.plugin.pushFrame(dataUrl, () => {});
        } catch (e) { /* frame skipped */ }
      };
      img.src = result.url;
    });
  } catch (e) { /* capture unavailable this tick */ }
}

overwolf.windows.onStateChanged.addListener(() => {});
main();
