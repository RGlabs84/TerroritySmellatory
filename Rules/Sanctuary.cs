using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>
    /// The territory's status effect: an icon in the HUD naming where you are, carrying whatever regen,
    /// carry and armour values the admin set. A plain vanilla SE_Stats - vanilla already applies every one
    /// of those fields through ModifyHealthRegen / ModifyMaxCarryWeight / ModifyArmorMods and already
    /// lists them in the tooltip, so nothing here touches the player's stats directly.
    /// </summary>
    /// <remarks>
    /// ONE template, applied as a clone (SEMan.AddStatusEffect clones) and then edited in place: the live
    /// instance's fields are read by vanilla every tick, so walking from one sanctuary into another simply
    /// rewrites the numbers instead of removing and re-adding the effect. m_ttl 0 never expires; leaving
    /// the territory removes it.
    ///
    /// Registered in ObjectDB from both Awake and CopyOtherDB - vanilla builds a second ObjectDB for the
    /// world and copies the first, and an effect in only one of them is missing whenever something looks
    /// one up by hash (the dual-hook rule, MASTER_IMPLEMENTATIONS / DeathBeforeDishonor). NameHash() hashes
    /// the ScriptableObject's .name, not m_name, so .name is the stable identity and m_name the display.
    /// </remarks>
    internal static class Sanctuary
    {
        internal const string EffectName = "SE_TerroritySanctuary";

        private static SE_Stats _template;
        private static int _hash;
        private static Territory _tooltipFor;   // the very record: a parent's edit changes a sub-territory's record, not its Revision
        private static string _tooltip = "";

        internal static int Hash => _hash != 0 ? _hash : (_hash = EffectName.GetStableHashCode());

        internal static SE_Stats Template
        {
            get
            {
                if (_template != null) return _template;
                _template = ScriptableObject.CreateInstance<SE_Stats>();
                _template.name = EffectName;
                _template.m_name = "Territory";
                _template.m_tooltip = "";
                _template.m_ttl = 0f;
                _template.hideFlags = HideFlags.HideAndDontSave;
                return _template;
            }
        }

        internal static void RegisterIn(ObjectDB db)
        {
            if (db == null || db.m_StatusEffects == null) return;
            if (db.m_StatusEffects.Any(se => se != null && se.name == EffectName)) return;
            db.m_StatusEffects.Add(Template);
        }

        internal static void Apply(Player player, Territory t)
        {
            SEMan seman = player.GetSEMan();
            if (seman == null) return;

            SE_Stats se = seman.GetStatusEffect(Hash) as SE_Stats;
            if (se == null)
            {
                se = seman.AddStatusEffect(Template) as SE_Stats;
                if (se == null) return;
            }

            if (se.m_icon == null) se.m_icon = Icon();
            se.m_name = t.DisplayName;
            se.m_healthRegenMultiplier = t.HealthRegen;
            se.m_staminaRegenMultiplier = t.StaminaRegen;
            se.m_eitrRegenMultiplier = t.EitrRegen;
            se.m_addMaxCarryWeight = t.CarryBonus;
            se.m_addArmor = t.ArmorBonus;
            se.m_tooltip = Tooltip(t);
        }

        internal static void Remove(Player player)
        {
            SEMan seman = player.GetSEMan();
            if (seman != null && seman.HaveStatusEffect(Hash))
                seman.RemoveStatusEffect(Hash, quiet: true);
        }

        /// <summary>The territory's own rules; SE_Stats appends the regen/carry/armour lines itself.</summary>
        private static string Tooltip(Territory t)
        {
            if (ReferenceEquals(t, _tooltipFor)) return _tooltip;

            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(t.OwnerName)) sb.Append("Owner: ").Append(t.OwnerName).Append('\n');
            foreach (string line in t.FeatureLines(TSConfig.AutoCloseDelay.Value))
            {
                if (line.StartsWith("Health regen") || line.StartsWith("Stamina regen") || line.StartsWith("Eitr regen") ||
                    line.StartsWith("Carry weight") || line.StartsWith("Armor")) continue;
                sb.Append(line).Append('\n');
            }

            _tooltipFor = t;
            _tooltip = sb.ToString().TrimEnd('\n');
            return _tooltip;
        }

        /// <summary>The guard stone's own build icon - the thing players already read as "someone's land".</summary>
        private static Sprite Icon()
        {
            if (ZNetScene.instance == null) return null;
            GameObject ward = ZNetScene.instance.GetPrefab("guard_stone");
            return ward != null ? ward.GetComponent<Piece>()?.m_icon : null;
        }
    }

    [HarmonyPatch(typeof(ObjectDB), "Awake")]
    internal static class ObjectDBAwakePatch
    {
        private static void Postfix(ObjectDB __instance) => Sanctuary.RegisterIn(__instance);
    }

    [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
    internal static class ObjectDBCopyPatch
    {
        private static void Postfix(ObjectDB __instance) => Sanctuary.RegisterIn(__instance);
    }
}
