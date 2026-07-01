// Standard Overwolf plugin loader (adapted from overwolf/overwolf-plugins).
// Wraps overwolf.extensions.current.getExtraObject in a promise-friendly helper.
class OverwolfPlugin {
  constructor(pluginName, autoInitialize = true) {
    this._pluginName = pluginName;
    this._plugin = null;
    this._loadPromise = null;
    if (autoInitialize) this.initialize();
  }

  initialize() {
    if (this._loadPromise) return this._loadPromise;
    this._loadPromise = new Promise((resolve) => {
      overwolf.extensions.current.getExtraObject(this._pluginName, (result) => {
        if (result && result.status === 'success') {
          this._plugin = result.object;
          resolve(true);
        } else {
          console.error(`[wzh] failed to load plugin ${this._pluginName}:`, result);
          resolve(false);
        }
      });
    });
    return this._loadPromise;
  }

  get() {
    return this._plugin;
  }
}

window.OverwolfPlugin = OverwolfPlugin;
