using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TerroritySmellatory
{
    // NO MONSTER SPAWNS - three funnels, all run by whichever machine owns the zone or spawner (a player's
    // client almost always), which is why this is enforced by the client mod and not by the server.
    //
    //   SpawnSystem.IsSpawnPointGood - every world spawn AND every raid spawn picks its point here, and
    //                                  vanilla's own "not inside a player base" rule lives in it too.
    //   CreatureSpawner.Spawn        - fixed spawn points: villages, camps, crypt entrances.
    //   SpawnArea.SpawnOne           - nests and other spawner structures.
    //
    // Only HOSTILE creatures are stopped: deer, boars, hares, birds and fish keep spawning, and so do a
    // player's own summons (Players / PlayerSpawned factions). Better Continents' spawn map works by
    // rewriting the spawn list around UpdateSpawnList; this sits inside the point check below that, only
    // ever turns a yes into a no, and so composes with it in either order.

    internal static class Hostility
    {
        private static readonly Dictionary<int, bool> _cache = new Dictionary<int, bool>();

        /// <summary>Would this prefab fight players? MonsterAI and not on the players' side.</summary>
        internal static bool IsHostile(GameObject prefab)
        {
            if (prefab == null) return false;

            int key = prefab.GetInstanceID();
            if (_cache.TryGetValue(key, out bool hostile)) return hostile;

            hostile = false;
            if (prefab.GetComponent<MonsterAI>() != null)
            {
                Character c = prefab.GetComponent<Character>();
                hostile = c == null || (c.m_faction != Character.Faction.Players && c.m_faction != Character.Faction.PlayerSpawned);
            }

            _cache[key] = hostile;
            return hostile;
        }

        internal static bool BlockedAt(Vector3 p)
        {
            Territory t = TerritoryIndex.GoverningAt(p);
            return t != null && t.Has(TerritoryFlags.NoMonsterSpawns);
        }
    }

    [HarmonyPatch(typeof(SpawnSystem), "IsSpawnPointGood")]
    internal static class SpawnPointPatch
    {
        private static void Postfix(SpawnSystem.SpawnData spawn, ref Vector3 spawnPoint, ref bool __result)
        {
            if (!__result || spawn == null || !TerritoryIndex.Any) return;
            if (!Hostility.IsHostile(spawn.m_prefab)) return;
            if (Hostility.BlockedAt(spawnPoint)) __result = false;
        }
    }

    [HarmonyPatch(typeof(CreatureSpawner), "Spawn")]
    internal static class CreatureSpawnerPatch
    {
        // Returning null is what vanilla itself does when spawning is switched off (SpawnSystem.m_nospawn),
        // so every caller already handles it.
        private static bool Prefix(CreatureSpawner __instance, ref ZNetView __result)
        {
            if (!TerritoryIndex.Any || !Hostility.IsHostile(__instance.m_creaturePrefab)) return true;
            if (!Hostility.BlockedAt(__instance.transform.position)) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(SpawnArea), "SpawnOne")]
    internal static class SpawnAreaPatch
    {
        private static bool Prefix(SpawnArea __instance, ref bool __result)
        {
            if (!TerritoryIndex.Any || !Hostility.BlockedAt(__instance.transform.position)) return true;

            bool anyHostile = false;
            foreach (SpawnArea.SpawnData data in __instance.m_prefabs)
            {
                if (data != null && Hostility.IsHostile(data.m_prefab))
                {
                    anyHostile = true;
                    break;
                }
            }
            if (!anyHostile) return true;

            __result = false;
            return false;
        }
    }
}
