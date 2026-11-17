using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace TerroritySmellatory
{
    // THE GUARD'S WEAPON: the game's own iron atgeir (the user, 2026-10-07, N5 "A spear or halberd", then "Use the already existing [atgeir] ...
    // it's basically a halberd": no new weapon model, no Meshy credits for it). Two halves:
    // - what he fights with: TS_GuardAtgeir (ValtimaOnline's VALtima_GuardAtgeir), a copy of AtgeirIron with the atgeir's damage and reach but the Dvergr's own melee attack
    //   (Dverger_melee: an animation the Dvergr's animator has, so the hit lands on that animation's event; an atgeir's own swing is a
    //   player animation the Dvergr does not have, and it would never land), no stamina, eitr or health cost and no wear. The user's Dvergr
    //   Allies arms its Dvergr with vanilla swords the same way. It is registered in ObjectDB: an item the game cannot find there has no drop
    //   prefab, and VisEquipment throws on it. It replaces the Dvergr's weapons and suit (its crossbow among them: a halberdier does not shoot);
    // - what he is seen holding: AtgeirIron's own model in the Meshy body's right hand (GuardHalberd). The copy VisEquipment hangs on the
    //   donor's hidden hand is hidden the moment it appears, not at the next 2 s re-hide (a second atgeir would float by his side meanwhile).
    // Without AtgeirIron or Dverger_melee in the game, the guards keep the Dvergr's weapons and say so in the log.
    internal static class GuardWeapon
    {
        internal const string ItemName = "TS_GuardAtgeir";
        internal const string ModelItem = "AtgeirIron";
        private const string MeleeDonor = "Dverger_melee";

        private static GameObject _box, _prefab;
        private static bool _warned, _armedLogged;

        internal static GameObject Prefab => _prefab;

        [HarmonyPatch(typeof(ObjectDB), "Awake")]
        internal static class AwakePatch
        {
            private static void Postfix(ObjectDB __instance) => Register(__instance);
        }

        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
        internal static class CopyPatch
        {
            private static void Postfix(ObjectDB __instance) => Register(__instance);
        }

        // VisEquipment hangs the equipped item on the donor's hand. On a guard wearing a Meshy body that hand is hidden, so the item is too, now.
        [HarmonyPatch(typeof(VisEquipment), "SetRightHandEquipped")]
        internal static class HideDonorItemPatch
        {
            private static void Postfix(VisEquipment __instance, bool __result)
            {
                if (!__result || TerritoryGuard.LiveCount == 0 || __instance.GetComponent<TerritoryGuard>() == null) return;
                GuardBody body = __instance.GetComponentInChildren<GuardBody>();
                if (body != null) body.HideDonorNow();
            }
        }

        // Each world session builds a new ObjectDB (and the menu has its own): the copy is made once and added to each, and the guard prefabs
        // already built are armed with it (ZNetScene.Awake may have built them before this ObjectDB woke)
        internal static void Register(ObjectDB db)
        {
            try
            {
                if (db == null || db.m_items == null || db.m_items.Count == 0) return;
                if (_prefab == null) _prefab = Build(db);
                if (_prefab == null) return;
                int hash = ItemName.GetStableHashCode();
                // UpdateRegisters adds each item's hash to a dictionary and throws on a second one: never add it twice
                if (!db.m_items.Contains(_prefab) && !db.m_items.Any(i => i != null && i.name.GetStableHashCode() == hash))
                {
                    db.m_items.Add(_prefab);
                    db.UpdateRegisters();
                }
                Guards.ArmPrefabs();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[guards] could not register the guards' atgeir: {e}");
            }
        }

        private static GameObject Build(ObjectDB db)
        {
            GameObject model = db.GetItemPrefab(ModelItem), melee = db.GetItemPrefab(MeleeDonor);
            ItemDrop modelDrop = model != null ? model.GetComponent<ItemDrop>() : null, meleeDrop = melee != null ? melee.GetComponent<ItemDrop>() : null;
            if (modelDrop == null || meleeDrop == null || meleeDrop.m_itemData.m_shared.m_attack == null)
            {
                // the main menu's ObjectDB lacks them (look walk 14): the game's own, a moment later, has them
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log.LogInfo($"[guards] this ObjectDB ({db.m_items.Count} items) has no {(modelDrop == null ? ModelItem : MeleeDonor)}: the guards' atgeir waits " +
                                       "for one that has (until then a guard keeps the Dvergr's weapons)");
                }
                return null;
            }
            if (_box == null)
            {
                _box = new GameObject("TS_GuardWeapon");
                _box.SetActive(false);   // under an inactive parent the copy never wakes (no ItemDrop or ZNetView of its own in the world)
                UnityEngine.Object.DontDestroyOnLoad(_box);
            }
            GameObject go = UnityEngine.Object.Instantiate(model, _box.transform, false);
            go.name = ItemName;
            ItemDrop.ItemData.SharedData s = go.GetComponent<ItemDrop>().m_itemData.m_shared, dverger = meleeDrop.m_itemData.m_shared;
            float atgeirReach = modelDrop.m_itemData.m_shared.m_attack != null ? modelDrop.m_itemData.m_shared.m_attack.m_attackRange : 0f;
            float reach = Mathf.Max(dverger.m_attack.m_attackRange, atgeirReach);
            s.m_attack = Free(dverger.m_attack.Clone(), reach);
            s.m_secondaryAttack = Free((dverger.m_secondaryAttack ?? dverger.m_attack).Clone(), reach);
            s.m_animationState = dverger.m_animationState;
            s.m_aiAttackRange = Mathf.Max(dverger.m_aiAttackRange, reach - 0.3f);
            s.m_aiAttackRangeMin = dverger.m_aiAttackRangeMin;
            s.m_aiAttackInterval = dverger.m_aiAttackInterval;
            s.m_aiAttackMaxAngle = dverger.m_aiAttackMaxAngle;
            s.m_aiPrioritized = dverger.m_aiPrioritized;
            s.m_aiTargetType = dverger.m_aiTargetType;
            s.m_useDurability = false;
            HitData.DamageTypes d = s.m_damages;
            Plugin.Log.LogInfo($"[guards] the guards' weapon: {ItemName}, {ModelItem}'s damage (pierce {d.m_pierce:0}, slash {d.m_slash:0}, blunt {d.m_blunt:0}) " +
                               $"with {MeleeDonor}'s attack '{s.m_attack.m_attackAnimation}', reach {reach:0.0} m (the atgeir's {atgeirReach:0.0}, the Dvergr's " +
                               $"{dverger.m_attack.m_attackRange:0.0}), the AI strikes from {s.m_aiAttackRange:0.0} m every {s.m_aiAttackInterval:0.0} s");
            return go;
        }

        private static Attack Free(Attack a, float reach)
        {
            a.m_attackRange = reach;
            a.m_attackStamina = 0f;
            a.m_attackEitr = 0f;
            a.m_attackHealth = 0f;
            a.m_attackHealthPercentage = 0f;
            return a;
        }

        // The guard prefab carries the atgeir as its only weapon (GiveDefaultItems runs on each machine from these arrays). The Dvergr's own
        // weapons go (look walk 14: DvergerArbalest and its _shoot, the crossbow it shot with, and Dverger_melee); what it wears stays (its
        // suit, and the hair it draws from the "shield" list), as the female guards still wear the Dvergr's body
        internal static void Arm(Humanoid h)
        {
            if (h == null || _prefab == null) return;
            if (h.m_defaultItems != null && h.m_defaultItems.Contains(_prefab)) return;
            if (!_armedLogged)
            {
                _armedLogged = true;
                string Names(IEnumerable<GameObject> gos) => string.Join(", ", (gos ?? Enumerable.Empty<GameObject>()).Where(g => g != null).Distinct()
                    .Select(g => g.GetComponent<ItemDrop>() is ItemDrop d ? $"{g.name} ({d.m_itemData.m_shared.m_itemType}{(Worn(g) ? ", kept" : "")})" : g.name));
                Plugin.Log.LogInfo($"[guards] the guards carry {ItemName} in place of the Dvergr's weapons: it had default [{Names(h.m_defaultItems)}], " +
                                   $"weapons [{Names(h.m_randomWeapon)}], sets [{Names(h.m_randomSets?.SelectMany(x => x.m_items))}], shields [{Names(h.m_randomShield)}], " +
                                   $"armour [{Names(h.m_randomArmor)}], items [{Names(h.m_randomItems?.Select(x => x.m_prefab))}]");
            }
            h.m_defaultItems = (h.m_defaultItems ?? Array.Empty<GameObject>()).Where(Worn).Append(_prefab).ToArray();
            h.m_randomWeapon = Array.Empty<GameObject>();
            h.m_randomArmor = (h.m_randomArmor ?? Array.Empty<GameObject>()).Where(Worn).ToArray();
            h.m_randomShield = (h.m_randomShield ?? Array.Empty<GameObject>()).Where(Worn).ToArray();
            h.m_randomSets = (h.m_randomSets ?? Array.Empty<Humanoid.ItemSet>())
                .Select(x => new Humanoid.ItemSet { m_name = x.m_name, m_items = (x.m_items ?? Array.Empty<GameObject>()).Where(Worn).ToArray() }).ToArray();
            h.m_randomItems = (h.m_randomItems ?? Array.Empty<Humanoid.RandomItem>()).Where(x => x != null && Worn(x.m_prefab)).ToArray();
        }

        // an item the Dvergr wears (its suit, its hair), not one it fights with or carries: armour and customisation only. Not "not a weapon":
        // DvergerArbalest is no weapon to the game (its shot is DvergerArbalest_shoot) but it is the crossbow the Dvergr is seen holding
        // (look walk 15 kept it)
        private static bool Worn(GameObject go)
        {
            ItemDrop d = go != null ? go.GetComponent<ItemDrop>() : null;
            if (d == null || d.m_itemData.IsWeapon()) return false;
            switch (d.m_itemData.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Shoulder:
                case ItemDrop.ItemData.ItemType.Hands:
                case ItemDrop.ItemData.ItemType.Customization:
                    return true;
                default:
                    return false;
            }
        }
    }
}
