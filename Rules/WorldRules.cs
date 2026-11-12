using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>
    /// STRUCTURE PROTECTION. Every hit on a building arrives as WearNTear.RPC_Damage on the machine that
    /// owns the piece (PORTAL-ZDO-AND-DEATH-FACTS §5). Refusing it there, before resistances, the damage
    /// number and the hit effect, means a protected wall shows nothing at all - no "0" floating off it.
    /// </summary>
    /// <remarks>
    /// EVERY hit is refused: creatures, anything with no attacker (fire, lava), and players, members and
    /// admins included. 0.1.0 let members' hits through, and an admin's too when the admin's own machine
    /// owned the piece, so an owner or admin testing their own wall watched it take damage (playtest,
    /// 2026-09-29), and whether an admin's hit landed depended on who happened to own the piece. Members and
    /// admins take a building down with the hammer instead: Remove is not damage, and it refunds the
    /// materials.
    ///
    /// Wear from weather and loss of support go through ApplyDamage from UpdateWear, NOT through this RPC,
    /// and are deliberately left alone: a protected building still has to stand up.
    /// </remarks>
    [HarmonyPatch(typeof(WearNTear), "RPC_Damage")]
    internal static class StructureDamagePatch
    {
        private static bool Prefix(WearNTear __instance, HitData hit)
        {
            if (hit == null || !TerritoryIndex.Any) return true;

            Territory t = TerritoryIndex.GoverningAt(__instance.transform.position);
            return t == null || !t.Has(TerritoryFlags.StructureProtect);
        }
    }

    /// <summary>
    /// NO RAIDS. The raid picker runs only on the server (RandEventSystem.UpdateRandomEvent returns at once
    /// anywhere else), and every candidate - random or standalone - is one of the positions this method
    /// returns. Removing positions inside a no-raid territory means a raid simply never chooses that player.
    /// </summary>
    [HarmonyPatch(typeof(RandEventSystem), "GetValidEventPoints")]
    internal static class RaidPointsPatch
    {
        private static void Postfix(List<Vector3> __result)
        {
            if (__result == null || __result.Count == 0 || !TerritoryIndex.Any) return;
            __result.RemoveAll(p =>
            {
                Territory t = TerritoryIndex.GoverningAt(p);
                return t != null && t.Has(TerritoryFlags.NoRaids);
            });
        }
    }

    /// <summary>
    /// NO SKILL LOSS. Player.OnDeath lowers skills through exactly one call, Skills.OnDeath, and only on a
    /// hard death; skipping it inside the territory leaves everything else about dying untouched (tombstone,
    /// food, the no-skill-drain timer, the "hardcore" skill wipe world modifier, which is a separate branch).
    /// </summary>
    [HarmonyPatch(typeof(Skills), nameof(Skills.OnDeath))]
    internal static class SkillLossPatch
    {
        private static bool Prefix(Skills __instance)
        {
            Player player = __instance.m_player;
            if (player == null || player != Player.m_localPlayer || !TerritoryIndex.Any) return true;

            Territory t = TerritoryIndex.GoverningAt(player.transform.position);
            if (t == null || !t.Has(TerritoryFlags.NoSkillLoss)) return true;

            player.Message(MessageHud.MessageType.TopLeft, $"{t.DisplayName} spared your skills");
            return false;
        }
    }
}
