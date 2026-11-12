using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TerroritySmellatory
{
    // TERRORITYSMELLATORY - admin-marked multiplayer territories.
    //
    // THE FLOW. Every file in this project serves one leg of it:
    //
    //   1. An ADMIN sets a territory's rules in the panel (UI/TerritoryPanel) and marks it on the ground with
    //      Infinity Hammer. Integration/InfinityHammerBridge writes the hammer's Territory tool; placing it
    //      runs `terr_place` with the ruler's position, size, shape and rotation (Integration/Commands).
    //   2. The SERVER checks the sender is an admin by the connection the request arrived on, clamps it,
    //      and saves it per world (Net/TerritoryServer, Net/TerritoryStore).
    //   3. The server SENDS the list to everyone, down each peer's own connection.
    //   4. Every CLIENT reads it (Net/TerritoryClient -> Core/TerritoryIndex) and applies it through the funnels
    //      vanilla already routes its own rules through (Rules/*): the ward access check, the spawn-point
    //      check, the structure damage RPC, the death skill penalty, the PvP toggle, the raid picker.
    //
    // Infinity Hammer is only ever on admin machines, so nothing depends on it: the tool is a yaml file
    // written only when IH is present, and a player's client runs everything else without it.
    [BepInPlugin(Guid, Name, TSBuild.Version)]
    [BepInDependency(InfinityHammerBridge.Guid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "wubarrk.terroritysmellatory";
        public const string Name = "TerroritySmellatory";

        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            TSConfig.Init(Config);

            _harmony = new Harmony(Guid);
            int failed = PatchIsolated();

            Commands.Register();

            Log.LogInfo($"{Name} {TSBuild.Version} loaded" +
                        (failed > 0 ? $" - {failed} patch class(es) failed to bind, see errors above." : "."));
        }

        private void Start()
        {
            // After the chainloader has finished, so Infinity Hammer's and Better Continents' presence is known
            // either way.
            InfinityHammerBridge.EnsureToolFile();
            BetterContinentsWatch.Report();
        }

        // Harmony's PatchAll, with one difference that matters after every game update: a patch class whose
        // target moved is logged and skipped instead of throwing out of Awake and taking every other patch
        // with it. A missing target should cost one feature, not the mod (the ShadowsOfMidgard 2.0 rule).
        // Each Rules/ class patches exactly one target for the same reason.
        private int PatchIsolated()
        {
            int failed = 0;
            foreach (Type type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
            {
                try
                {
                    List<MethodInfo> patched = _harmony.CreateClassProcessor(type).Patch();
                    if (patched != null && patched.Count > 0 && TSConfig.Verbose.Value)
                        Log.LogInfo($"patched {type.Name}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.LogError($"Patch class {type.FullName} failed to bind; that feature is off this session. {ex}");
                }
            }
            return failed;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            Guarded("server", () => TerritoryServer.Tick(dt));
            Guarded("client", () => TerritoryClient.Tick(dt));
            Guarded("presence", () => Presence.Tick(dt));
            // A static method group, not a lambda: C# caches its delegate, so the hold that runs every frame near a
            // barrier allocates nothing here (Rules/Barrier.cs).
            Guarded("barrier", BarrierTick);
            Guarded("borders", Borders.Tick);
            // The server's own rule, run by no client: a static method group like the rest (Rules/DoorCloser.cs).
            Guarded("doors", DoorCloser.Tick);
            Guarded("panel", TerritoryPanel.Tick);
        }

        private static void BarrierTick() => Barrier.Tick(Time.deltaTime);

        private void OnGUI()
        {
            Guarded("panel draw", TerritoryPanel.OnGUI);
        }

        private void OnDestroy()
        {
            // Before the patches go: with nothing left to rewrite it, a territory's name on the minimap would
            // stay there until the player next walked into another biome.
            MapBiomeLabel.Release();
            _harmony?.UnpatchSelf();
        }

        // A tick that throws every frame would bury the log in one line repeated sixty times a second and
        // hide the first, useful occurrence. Log each failing tick once per 30 s and keep running the rest.
        private static readonly Dictionary<string, float> _lastError = new Dictionary<string, float>();

        private static void Guarded(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                if (!_lastError.TryGetValue(what, out float last) || Time.realtimeSinceStartup - last > 30f)
                {
                    _lastError[what] = Time.realtimeSinceStartup;
                    Log.LogError($"{what} tick failed (non-fatal, repeats suppressed for 30 s): {ex}");
                }
            }
        }

        internal static void Debug(string message)
        {
            if (TSConfig.Verbose != null && TSConfig.Verbose.Value) Log.LogInfo(message);
        }
    }
}
