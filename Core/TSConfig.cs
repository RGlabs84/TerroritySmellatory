using BepInEx.Configuration;
using ServerSync;
using UnityEngine;

namespace TerroritySmellatory
{
    internal enum BorderMode
    {
        /// <summary>Never draw territory borders on the ground.</summary>
        Off,

        /// <summary>Only while the territory panel is open.</summary>
        PanelOnly,

        /// <summary>While the panel is open or a build tool is out - when a border is what you need to see.</summary>
        PanelOrBuilding,

        /// <summary>Whenever a territory is within range.</summary>
        Always
    }

    internal enum BannerStyle
    {
        /// <summary>The big title the game shows for a newly discovered biome, with the territory's own entry text beneath it.</summary>
        Epic,

        /// <summary>One line in the centre of the screen, as 0.1.x showed it.</summary>
        Simple
    }

    internal enum StingerMode
    {
        /// <summary>The discovery sound plays the first time you enter each territory in a session.</summary>
        FirstEntry,

        /// <summary>Every entry banner plays it.</summary>
        EveryEntry,

        /// <summary>Never.</summary>
        Off
    }

    internal static class TSConfig
    {
        // ServerSync's version handshake doubles as the "every client must run this" gate: ModRequired
        // kicks a client that does not have the mod, which is the only honest setting for a mod whose
        // protections are enforced ON the client. The server may turn it off (RequireClientMod) for a
        // mixed server, and is told plainly what that costs.
        internal static readonly ConfigSync Sync = new ConfigSync(Plugin.Guid)
        {
            DisplayName = Plugin.Name,
            CurrentVersion = TSBuild.Version,
            MinimumRequiredVersion = TSBuild.SyncFloor,
            ModRequired = true
        };

        // 0 - Server Sync
        internal static ConfigEntry<bool> LockConfiguration;

        // 1 - Server (locked to the server's value)
        internal static ConfigEntry<bool> RequireClientMod;
        internal static ConfigEntry<bool> AdminsBypassProtection;
        internal static ConfigEntry<bool> MineWorldPieces;
        internal static ConfigEntry<float> MaxHalfExtent;
        internal static ConfigEntry<int> MaxTerritories;
        internal static ConfigEntry<int> MaxParts;
        internal static ConfigEntry<float> AutoCloseDelay;
        internal static ConfigEntry<float> AutoClosePlayerClearance;
        internal static ConfigEntry<string> AutoCloseExcludedPrefabs;
        internal static ConfigEntry<int> GuardsPerPart;
        internal static ConfigEntry<int> MaxGuardsPerTerritory;
        internal static ConfigEntry<float> GuardCallRadius;
        internal static ConfigEntry<float> GuardCallCooldown;
        internal static ConfigEntry<float> GuardLingerSeconds;

        // 2 - Client (personal; never synced)
        internal static ConfigEntry<KeyboardShortcut> PanelKey;
        internal static ConfigEntry<BorderMode> Borders;
        internal static ConfigEntry<float> BorderRange;
        internal static ConfigEntry<bool> MapAreas;
        internal static ConfigEntry<bool> MapHideUnexplored;
        internal static ConfigEntry<bool> Banners;
        internal static ConfigEntry<BannerStyle> EntryBanner;
        internal static ConfigEntry<StingerMode> EntryStinger;
        internal static ConfigEntry<float> BannerCooldown;
        internal static ConfigEntry<bool> MinimapName;
        internal static ConfigEntry<bool> BarrierEffects;
        internal static ConfigEntry<float> PanelScale;
        internal static ConfigEntry<string> GuardCallWords;

        // 3 - Infinity Hammer (admin machines only; inert without IH)
        internal static ConfigEntry<bool> WriteHammerTool;
        internal static ConfigEntry<float> HammerInitialSize;

        // 9 - Debug
        internal static ConfigEntry<bool> Verbose;

        internal static void Init(ConfigFile cfg)
        {
            // Registered FIRST: ServerSync reads the lock while registering every later entry, and a mod
            // that forgot it shipped a whole line where any client could broadcast its own values
            // (MASTER_IMPLEMENTATIONS, ShadowsOfMidgard SOMConfig).
            LockConfiguration = cfg.Bind("0 - Server Sync", "LockConfiguration", true,
                "If true (default), every [1 - Server] setting is locked to the server's value for everyone who is not an admin. Territories themselves are always server-authoritative whatever this says - they are never taken from a client.");
            Sync.AddLockingConfigEntry(LockConfiguration);

            RequireClientMod = cfg.Bind("1 - Server", "RequireClientMod", true,
                "Kick players who join without TerroritySmellatory. Territory protections are applied by each player's own game, so a player without the mod is simply not bound by them. Only turn this off for a mixed server where that is acceptable. Read at startup.");
            Sync.AddConfigEntry(RequireClientMod);

            AdminsBypassProtection = cfg.Bind("1 - Server", "AdminsBypassProtection", true,
                "Server admins (adminlist.txt, as the SERVER decides it) are never stopped by a territory's protections. Admin status is told to each client by the server; a client never decides it for itself.");
            Sync.AddConfigEntry(AdminsBypassProtection);

            MineWorldPieces = cfg.Bind("1 - Server", "MineWorldPieces", true,
                "Inside a territory where 'Buildings cannot be damaged', pieces no player built - dvergr black marble, Ashlands grausten, ruins, anything baked into the world - can still be mined or chopped with a pickaxe or an axe, by the people the territory's Harvesting rule allows: everyone when it is off, the owner and members when it is on. Anything a player built stays protected whatever this says, and so does every hit from a creature, fire or another weapon. Turn it off where buildings are baked into the world (towns placed by world generation have no builder and count as world pieces). Ore veins and rocks (copper, tin, OreMines' nodes...) are never protected by the rule and need no setting.");
            Sync.AddConfigEntry(MineWorldPieces);

            MaxHalfExtent = cfg.Bind("1 - Server", "MaxRadius", 600f,
                new ConfigDescription("Largest radius (or half-width) a territory may have, in metres. The server clamps every placement to it. Raise it for a continent or a region marked in the game (up to 100 km); a territory read from the world's file is held to 100 km whatever this says, and is never shrunk by lowering it.",
                    new AcceptableValueRange<float>(5f, 100000f)));
            Sync.AddConfigEntry(MaxHalfExtent);

            MaxTerritories = cfg.Bind("1 - Server", "MaxTerritories", 500,
                new ConfigDescription("How many territories one world may hold.", new AcceptableValueRange<int>(1, 5000)));
            Sync.AddConfigEntry(MaxTerritories);

            MaxParts = cfg.Bind("1 - Server", "MaxParts", 16,
                new ConfigDescription("How many pieces of ground one territory may be made of, by combining territories or adding parts. Each part is a circle, square or rectangle; together they are one territory with one set of rules. 1 turns combining off.",
                    new AcceptableValueRange<int>(1, 64)));
            Sync.AddConfigEntry(MaxParts);

            // The three below serve the AutoCloseDoors rule (Rules/DoorCloser.cs), which only the server runs. They are
            // synced anyway: the panel and terr_list quote the delay, and a client should quote the server's.
            AutoCloseDelay = cfg.Bind("1 - Server", "AutoCloseDelay", 10f,
                new ConfigDescription("Seconds a door or gate may stay open before a territory with 'Doors close by themselves' closes it.",
                    new AcceptableValueRange<float>(1f, 600f)));
            Sync.AddConfigEntry(AutoCloseDelay);

            AutoClosePlayerClearance = cfg.Bind("1 - Server", "AutoClosePlayerClearance", 3f,
                new ConfigDescription("A door is not closed while any player is within this many metres of it, so nobody gets a door shut in their face. The delay keeps counting, and the door closes as soon as they step away. 0 turns the check off.",
                    new AcceptableValueRange<float>(0f, 30f)));
            Sync.AddConfigEntry(AutoClosePlayerClearance);

            AutoCloseExcludedPrefabs = cfg.Bind("1 - Server", "AutoCloseExcludedPrefabs", DoorMath.DefaultExcluded,
                "Comma-separated door prefab names that are never closed by themselves. Drawbridges are left out by default because raising one under a player halfway across is worse than leaving it down, and window shutters because nobody wants their light shut off. Case-insensitive. Key doors and one-way dungeon doors are always left alone.");
            Sync.AddConfigEntry(AutoCloseExcludedPrefabs);

            // The five below serve the Guards rule (Rules/Guards.cs). The server posts the guards and answers the calls; they
            // are synced so the panel quotes the server's numbers, and so a client's own cooldown matches the server's.
            GuardsPerPart = cfg.Bind("1 - Server", "GuardsPerPart", 2,
                new ConfigDescription("How many guards stand posted in each part of a territory with 'Guards', in a ring round the part's middle, facing out.",
                    new AcceptableValueRange<int>(1, 8)));
            Sync.AddConfigEntry(GuardsPerPart);

            MaxGuardsPerTerritory = cfg.Bind("1 - Server", "MaxGuardsPerTerritory", 8,
                new ConfigDescription("The most guards posted in one territory, however many parts it has. The first parts are served first.",
                    new AcceptableValueRange<int>(1, 32)));
            Sync.AddConfigEntry(MaxGuardsPerTerritory);

            GuardCallRadius = cfg.Bind("1 - Server", "GuardCallRadius", 80f,
                new ConfigDescription("When a player calls the guards, a posted guard of their territory this many metres from them answers. When none is that close, a guard appears beside the caller and goes when the fight is over.",
                    new AcceptableValueRange<float>(10f, 400f)));
            Sync.AddConfigEntry(GuardCallRadius);

            GuardCallCooldown = cfg.Bind("1 - Server", "GuardCallCooldown", 10f,
                new ConfigDescription("Seconds before the same player can call the guards again. The server keeps it too.",
                    new AcceptableValueRange<float>(1f, 300f)));
            Sync.AddConfigEntry(GuardCallCooldown);

            GuardLingerSeconds = cfg.Bind("1 - Server", "GuardLingerSeconds", 40f,
                new ConfigDescription("A guard who appeared for a call stays this many seconds after the fight is over, then goes.",
                    new AcceptableValueRange<float>(5f, 300f)));
            Sync.AddConfigEntry(GuardLingerSeconds);

            PanelKey = cfg.Bind("2 - Client", "PanelKey", new KeyboardShortcut(KeyCode.Home),
                "Opens and closes the territory panel. The console command 'terr' does the same.");

            Borders = cfg.Bind("2 - Client", "ShowBorders", BorderMode.Always,
                "When to draw territory borders on the ground: Off, PanelOnly, PanelOrBuilding (panel open or a build tool out), Always (whenever you are near a territory's edge). A territory's admin can also hide its border for everyone.");

            BorderRange = cfg.Bind("2 - Client", "BorderRange", 60f,
                new ConfigDescription("Borders show once you are this close to a territory's edge, and only the stretch of border within this range is drawn.",
                    new AcceptableValueRange<float>(10f, 500f)));

            MapAreas = cfg.Bind("2 - Client", "ShowOnMap", true,
                "Draw territories (those whose admin allowed it) on the large map and the minimap, with their names.");

            MapHideUnexplored = cfg.Bind("2 - Client", "HideUnexploredTerritories", true,
                "Draw a territory on the map, and its name, only over ground you have uncovered - by walking it, or from another player's shared map. Off draws territories over the whole map, fog included.");

            Banners = cfg.Bind("2 - Client", "EnterLeaveBanners", true,
                "Show a banner when you walk into or out of a territory that announces itself.");

            EntryBanner = cfg.Bind("2 - Client", "EntryBannerStyle", BannerStyle.Epic,
                "How entering a territory is announced: Epic (the big title the game shows for a newly discovered biome, with the territory's entry text beneath it) or Simple (one line in the centre of the screen). Leaving is always one centre line.");

            EntryStinger = cfg.Bind("2 - Client", "EntrySound", StingerMode.FirstEntry,
                "The biome-discovery sound with an Epic entry banner: FirstEntry (the first time you enter each territory in a session), EveryEntry, or Off.");

            BannerCooldown = cfg.Bind("2 - Client", "BannerCooldownSeconds", 90f,
                new ConfigDescription("After a territory's entry banner, walking back into it within this many seconds shows no new banner, so a path along a border cannot spam them. 0 turns the cooldown off.",
                    new AcceptableValueRange<float>(0f, 3600f)));

            MinimapName = cfg.Bind("2 - Client", "MinimapTerritoryName", true,
                "While you are inside a territory that is shown on the map, the minimap shows its name where the biome name usually is.");

            BarrierEffects = cfg.Bind("2 - Client", "BarrierEffects", true,
                "Show a runic barrier's blue flare, with its sound, where something pushes against it. The barrier keeps monsters out either way.");

            PanelScale = cfg.Bind("2 - Client", "PanelScale", 1f,
                new ConfigDescription("Size of the territory panel's text and layout.", new AcceptableValueRange<float>(0.7f, 2f)));

            GuardCallWords = cfg.Bind("2 - Client", "GuardCallWords", "guards",
                "Words that call the guards when you type them in chat inside a territory with guards, comma separated, matched as whole words in any case (\"Guards!\" works, \"safeguards\" does not). Empty: you never call them.");

            WriteHammerTool = cfg.Bind("3 - Infinity Hammer", "WriteHammerTool", true,
                "When Infinity Hammer is installed, keep 'infinity_tools_terroritysmellatory.yaml' in the config folder so the Territory tool appears in the hammer's tool menu. Delete the file's first line to take it over and stop the mod rewriting it.");

            HammerInitialSize = cfg.Bind("3 - Infinity Hammer", "InitialRadius", 20f,
                new ConfigDescription("The radius the Territory hammer tool starts at. Scroll to resize it as with any Infinity Hammer tool.",
                    new AcceptableValueRange<float>(2f, 500f)));

            Verbose = cfg.Bind("9 - Debug", "VerboseLogging", false,
                "Log every snapshot sent and received, and every territory decision that refused something.");

            Sync.ModRequired = RequireClientMod.Value;
        }
    }
}
