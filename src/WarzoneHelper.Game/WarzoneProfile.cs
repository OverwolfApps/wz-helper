using System.Collections.Generic;
using System.Text.RegularExpressions;
using GameHelper.Core;
using GameHelper.Core.Config;
using GameHelper.Core.Events;
using GameHelper.Core.Monitors;
using GameHelper.Core.Screen;

namespace WarzoneHelper.Game
{
    /// <summary>
    /// Call of Duty: Warzone game plug-in. Supplies the Warzone config defaults, its OCR field
    /// registry, and the game-specific monitors (screen analysis + player roster) that sit on top of
    /// the generic GameHelper.Core infrastructure.
    /// </summary>
    public sealed class WarzoneProfile : IGameProfile
    {
        public string Name => "warzone";

        // Activision display name: optional [tag] (1-5 chars), 2-16 unicode name (must contain a
        // letter), optional #discriminator. Named groups: tag / name / discriminator.
        public string PlayerNamePattern =>
            @"^(?:\[(?<tag>[^\]]{1,5})\])?\s*(?<name>(?=[\p{L}\p{N} _.\-]*\p{L})[\p{L}\p{N} _.\-]{2,16})\s*(?:#(?<discriminator>\d{5,12}))?$";

        public WarzoneProfile()
        {
            // The OCR name validation reuses this profile's pattern.
            Game.OcrFields.NamePattern = new Regex(PlayerNamePattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }

        public HelperConfig CreateDefaultConfig() => new WarzoneConfig();

        public IStatusParser CreateStatusParser(HelperConfig cfg) =>
            new WarzoneStatusParser(cfg?.StatusGameTitles);

        public IEnumerable<EventDoc> Events => EventDef.DocsFrom(typeof(WarzoneEvents));

        public string PerfStripWhitelist
        {
            get => Game.OcrFields.PerfStripWhitelist;
            set => Game.OcrFields.PerfStripWhitelist = value;
        }

        public IReadOnlyDictionary<string, OcrField> OcrFields { get; } =
            new Dictionary<string, OcrField>
            {
                { Game.OcrFields.LobbyId.Name, Game.OcrFields.LobbyId },
                { Game.OcrFields.GameVersion.Name, Game.OcrFields.GameVersion },
                { Game.OcrFields.PlayerName.Name, Game.OcrFields.PlayerName },
                { Game.OcrFields.Level.Name, Game.OcrFields.Level },
                { Game.OcrFields.SpectateId.Name, Game.OcrFields.SpectateId },
                { Game.OcrFields.PartyCode.Name, Game.OcrFields.PartyCode },
                { Game.OcrFields.ChatChannel.Name, Game.OcrFields.ChatChannel },
                { Game.OcrFields.Fps.Name, Game.OcrFields.Fps },
                { Game.OcrFields.Latency.Name, Game.OcrFields.Latency },
                { Game.OcrFields.GameLatency.Name, Game.OcrFields.GameLatency },
                { Game.OcrFields.PacketLoss.Name, Game.OcrFields.PacketLoss },
                { Game.OcrFields.GpuTemp.Name, Game.OcrFields.GpuTemp },
                { Game.OcrFields.VramPct.Name, Game.OcrFields.VramPct },
                { Game.OcrFields.Clock.Name, Game.OcrFields.Clock },
            };

        public IEnumerable<IMonitor> CreateGameMonitors(GameContext ctx)
        {
            var cfg = ctx.Config as WarzoneConfig ?? new WarzoneConfig();

            // Screen CV (only when a frame source is available).
            if (ctx.FrameSource != null)
            {
                var analyzer = new WarzoneScreenAnalyzer(cfg.Regions, ctx.Ocr, cfg.OcrGrayscaleByValue);
                yield return new ScreenMonitor(cfg, ctx.Bus, ctx.Proc, ctx.FrameSource, analyzer, ctx.Match);
            }

            // Unified player roster — consumes list/chat/killfeed events, emits PLAYER_* deltas.
            yield return new PlayerRoster(cfg, ctx.Bus);

            // Clipboard watcher — catches copied party codes the OCR can't read.
            yield return new ClipboardPartyCodeMonitor(ctx.Bus);
        }
    }
}
