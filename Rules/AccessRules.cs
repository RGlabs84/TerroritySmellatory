using System;
using HarmonyLib;
using UnityEngine;

namespace TerroritySmellatory
{
    // BUILD AND ACCESS PROTECTION - riding vanilla's own ward check.
    //
    // PrivateArea.CheckAccess is the single question vanilla asks before a player touches anything a ward
    // protects: placing (Player.UpdatePlacementGhost), removing (Player.RemovePiece), repairing
    // (Player.Repair), digging (Attack.SpawnOnHitTerrain), and using doors, chests, portals, signs, item and
    // armour stands, fermenters, beehives, sap collectors, incinerators, map tables and traps - 35 call
    // sites in 1.0.16, every one of them already wired to the right refusal (red ghost, "private zone",
    // door stays shut, shield flash). Answering "no" there for a territory's non-members makes a territory
    // behave exactly like a ward everywhere a ward is honoured, with no per-object patch to forget.
    //
    // One call site cannot tell building from using, so a BuildScope counter is raised around the four
    // build entry points: inside it the question is about building, outside it about access. The counter
    // is dropped in a FINALIZER, which runs even when the method throws, so it can never stick.

    internal static class BuildScope
    {
        private static int _depth;

        internal static bool Active => _depth > 0;

        internal static void Enter() => _depth++;

        internal static void Exit()
        {
            if (_depth > 0) _depth--;
        }
    }

    [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
    internal static class BuildScopePlacementPatch
    {
        private static void Prefix() => BuildScope.Enter();

        private static Exception Finalizer(Exception __exception)
        {
            BuildScope.Exit();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Player), "RemovePiece")]
    internal static class BuildScopeRemovePatch
    {
        private static void Prefix() => BuildScope.Enter();

        private static Exception Finalizer(Exception __exception)
        {
            BuildScope.Exit();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Player), "Repair")]
    internal static class BuildScopeRepairPatch
    {
        private static void Prefix() => BuildScope.Enter();

        private static Exception Finalizer(Exception __exception)
        {
            BuildScope.Exit();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Attack), nameof(Attack.SpawnOnHitTerrain))]
    internal static class BuildScopeTerrainPatch
    {
        private static void Prefix() => BuildScope.Enter();

        private static Exception Finalizer(Exception __exception)
        {
            BuildScope.Exit();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(PrivateArea), nameof(PrivateArea.CheckAccess))]
    internal static class PrivateAreaCheckAccessPatch
    {
        private static void Postfix(Vector3 point, float radius, bool flash, bool wardCheck, ref bool __result)
        {
            // Only ever turns a yes into a no. A vanilla ward (or another mod) that already refused stays
            // refused; nothing here can grant access vanilla denied.
            if (!__result || !TerritoryIndex.Any) return;

            Player player = Player.m_localPlayer;
            if (player == null) return;

            // wardCheck is vanilla placing a WARD: that is building, whatever scope it arrived through.
            TerritoryFlags protection = BuildScope.Active || wardCheck ? TerritoryFlags.BuildProtect : TerritoryFlags.AccessProtect;

            Territory denying = TerritoryIndex.Denying(point, radius, protection, player.GetPlayerID(), TerritoryClient.Bypass);
            if (denying == null) return;

            __result = false;
            if (flash) Feedback.Denied(denying, protection);
        }
    }

    /// <summary>Crops, berries, mushrooms, flax... vanilla wards do not protect pickables; a territory can.</summary>
    [HarmonyPatch(typeof(Pickable), nameof(Pickable.Interact))]
    internal static class PickableInteractPatch
    {
        private static bool Prefix(Pickable __instance, Humanoid character, bool repeat, ref bool __result)
        {
            if (!TerritoryIndex.Any || character == null || character != Player.m_localPlayer) return true;

            Territory denying = TerritoryIndex.Denying(__instance.transform.position, 0f, TerritoryFlags.HarvestProtect,
                Player.m_localPlayer.GetPlayerID(), TerritoryClient.Bypass);
            if (denying == null) return true;

            if (!repeat) Feedback.Denied(denying, TerritoryFlags.HarvestProtect);
            __result = false;
            return false;
        }
    }

    /// <summary>Says why, once - vanilla's refusals are silent or generic, and a territory has a name.</summary>
    internal static class Feedback
    {
        private static string _last = "";
        private static float _lastAt = -100f;

        internal static void Denied(Territory t, TerritoryFlags protection)
        {
            string text;
            switch (protection)
            {
                case TerritoryFlags.BuildProtect: text = $"{t.DisplayName}: only members may build here"; break;
                case TerritoryFlags.HarvestProtect: text = $"{t.DisplayName}: only members may harvest here"; break;
                default: text = $"{t.DisplayName}: only members may use this"; break;
            }

            if (text == _last && Time.time - _lastAt < 2f) return;
            _last = text;
            _lastAt = Time.time;

            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, text);
            Plugin.Debug($"refused ({protection}) by territory #{t.Id} '{t.DisplayName}'");
        }
    }
}
