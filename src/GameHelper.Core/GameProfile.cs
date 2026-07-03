using System.Collections.Generic;
using GameHelper.Core.Config;
using GameHelper.Core.Events;
using GameHelper.Core.Geo;
using GameHelper.Core.Monitors;
using GameHelper.Core.Screen;

namespace GameHelper.Core
{
    /// <summary>
    /// Everything a game's monitors need from the core host. The core builds the shared pieces
    /// (process/log/network/status monitors, OCR engine, frame source) and hands this to the
    /// profile so it can construct only the game-specific monitors on top.
    /// </summary>
    public sealed class GameContext
    {
        public HelperConfig Config;
        public EventBus Bus;
        public ProcessTracker Proc;
        public MatchState Match;
        public GeoIpResolver Geo;
        public IOcrEngine Ocr;
        /// <summary>Shared frame source (GDI self-capture or Overwolf pushed frames), or null when
        /// screen CV is disabled. Owned by the core; game monitors read from it.</summary>
        public IFrameSource FrameSource;
    }

    /// <summary>
    /// A game plug-in for the generic helper. Supplies the game's config type + defaults, its OCR
    /// field registry, and the game-specific monitors. A new game = one implementation of this.
    /// </summary>
    public interface IGameProfile
    {
        /// <summary>Short lowercase id used for the per-game config folder (e.g. "warzone").</summary>
        string Name { get; }

        /// <summary>A fresh, fully-defaulted config of this game's concrete type.</summary>
        HelperConfig CreateDefaultConfig();

        /// <summary>The game's OCR fields keyed by spec name, for ocr.jsonc overrides.</summary>
        IReadOnlyDictionary<string, OcrField> OcrFields { get; }

        /// <summary>The game's perf-strip OCR whitelist (get/set so ocr.jsonc can override it).</summary>
        string PerfStripWhitelist { get; set; }

        /// <summary>Parser for the status-API response body, or null to use the generic raw-body
        /// default. Lets a game interpret its vendor's status shape without Core knowing it.</summary>
        IStatusParser CreateStatusParser(HelperConfig cfg);

        /// <summary>The game's own event descriptors, merged with Core's into the HELPER_STARTED
        /// catalog. Empty/null if the game adds no events.</summary>
        IEnumerable<Events.EventDoc> Events { get; }

        /// <summary>Build the game-specific monitors (screen analysis, roster, ...).</summary>
        IEnumerable<IMonitor> CreateGameMonitors(GameContext ctx);
    }
}
