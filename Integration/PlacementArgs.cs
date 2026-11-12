using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>
    /// Turns what Infinity Hammer hands `terr_place` into a territory's ground. Pure - no game state - so the
    /// test harness (tests/) runs it against IH's own substitution rules.
    /// </summary>
    internal static class PlacementArgs
    {
        /// <summary>key=value arguments after the command name, case-insensitive keys.</summary>
        internal static Dictionary<string, string> KeyValues(string[] args)
        {
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < args.Length; i++)
            {
                int eq = args[i].IndexOf('=');
                if (eq <= 0) continue;
                kv[args[i].Substring(0, eq)] = args[i].Substring(eq + 1);
            }
            return kv;
        }

        /// <summary>IH formats every number with the invariant culture; so does this, whatever the machine's locale.</summary>
        internal static bool TryFloat(Dictionary<string, string> kv, string key, out float value)
        {
            value = 0f;
            return kv.TryGetValue(key, out string s)
                   && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                   && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>
        /// The ground from a terr_place line. False without x and z. IH leaves r= for a circle (and ring),
        /// w= and d= for a square, rectangle (and frame); a frame's d= arrives as "min-max", fails to parse,
        /// and so reads as a square - the right fallback. No size at all falls back to <paramref name="defaultRadius"/>.
        /// </summary>
        internal static bool TryParse(string[] args, float fallbackY, float defaultRadius, out Territory geometry)
        {
            geometry = null;
            Dictionary<string, string> kv = KeyValues(args);
            if (!TryFloat(kv, "x", out float x) || !TryFloat(kv, "z", out float z)) return false;

            geometry = new Territory
            {
                X = x,
                Z = z,
                Y = TryFloat(kv, "y", out float y) ? y : fallbackY,
                Yaw = TryFloat(kv, "a", out float a) ? a : 0f
            };

            if (TryFloat(kv, "r", out float r))
            {
                geometry.Kind = TerritoryShapeKind.Circle;
                geometry.HalfX = geometry.HalfZ = r;
            }
            else if (TryFloat(kv, "w", out float w))
            {
                float d = TryFloat(kv, "d", out float dd) ? dd : w;
                geometry.Kind = Mathf.Approximately(w, d) ? TerritoryShapeKind.Square : TerritoryShapeKind.Rectangle;
                geometry.HalfX = w;
                geometry.HalfZ = d;
            }
            else
            {
                geometry.Kind = TerritoryShapeKind.Circle;
                geometry.HalfX = geometry.HalfZ = defaultRadius;
            }
            return true;
        }

        /// <summary>
        /// Whether IH would find a tool of this name in this yaml: a "name:" key (IH's naming is camelCase, so the key
        /// is lower case), its value compared as IH's `tool` compares names - ignoring case. Pure, for a harness.
        /// </summary>
        internal static bool DefinesTool(string yaml, string tool)
        {
            if (string.IsNullOrEmpty(yaml) || string.IsNullOrEmpty(tool)) return false;
            foreach (string raw in yaml.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("-", StringComparison.Ordinal)) line = line.Substring(1).TrimStart();
                if (!line.StartsWith("name:", StringComparison.Ordinal)) continue;

                string value = line.Substring(5).Trim();
                int comment = value.IndexOf(" #", StringComparison.Ordinal);
                if (comment >= 0) value = value.Substring(0, comment).TrimEnd();
                if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0])
                    value = value.Substring(1, value.Length - 2);
                if (string.Equals(value, tool, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// Default Infinity Hammer tool definitions (InitialData.Get() in Infinity Hammer / InfinityTools).
        /// Seeded if infinity_tools.yaml is missing or empty, so IH's wildcard scan does not skip default tool generation.
        /// </summary>
        internal const string DefaultInfinityHammerToolsYaml = @"hammer:
- name: Pipette
  description: |-
    Press <mod1> to pick up.
    Press <mod2> to freeze.
  icon: hammer
  command: hammer pick=<mod1> freeze=<mod2>
  index: 1
- name: Building pipette
  description: |-
    Select entire buildings.
    Press <mod1> to pick up.
    Press <mod2> to freeze.
  icon: hammer
  command: hammer connect pick=<mod1> freeze=<mod2>
- name: Area pipette
  description: |-
    Select multiple objects.
    Press <mod1> to pick up.
    Press <mod2> to freeze.
  icon: hammer
  command: hammer pick=<mod1> freeze=<mod2> height=<h> <area>
  initialHeight: 0
  highlight: true
hoe:
- name: Level
  description: |-
    Flattens terrain.
    Hold <mod1> to smooth.
    Hold <mod2> for free mode.
    Hold <alt> for player height.
  icon: mud_road
  command: terrain level smooth=<mod1>?.5:0 <area>
  index: 10
  terrainGrid: -<mod2>
  playerHeight: <alt>
- name: Raise
  description: |-
    Raises terrain.
    Hold <mod1> to smooth.
    Hold <mod2> for free mode.
  icon: raise
  command: terrain raise=<h> smooth=<mod1>?.5:0 <area>
  initialHeight: 0.2
  terrainGrid: -<mod2>
  snapGround : true
- name: Pave
  description: |-
    Paves terrain.
    Hold <mod1> for single use.
    Hold <mod2> for free mode.
  icon: paved_road
  command: terrain paint=paved <area>
  continuous: -<mod1>
  terrainGrid: -<mod2>
  snapGround : true
- name: Grass
  description: |-
    Grass.
    Hold <mod1> for single use.
    Hold <mod2> for free mode.
  icon: replant
  command: terrain paint=grass <area>
  continuous: -<mod1>
  terrainGrid: -<mod2>
  snapGround : true
- name: Dirt
  description: |-
    Dirt.
    Hold <mod1> for single use.
    Hold <mod2> for free mode.
  icon: Hoe
  command: terrain paint=dirt <area>
  continuous: -<mod1>
  terrainGrid: -<mod2>
  snapGround : true
- name: Cultivate
  description: |-
    Cultivates terrain.
    Hold <mod1> for single use.
    Hold <mod2> for free mode.
  icon: cultivate
  command: terrain paint=cultivated <area>
  continuous: -<mod1>
  terrainGrid: -<mod2>
  snapGround : true
- name: DarkGrass
  description: |-
    Dark Grass.
    Hold <mod1> for single use.
    Hold <mod2> for free mode.
  icon: trophyabomination
  command: terrain paint=grass_dark <area>
  continuous: -<mod1>
  terrainGrid: -<mod2>
  snapGround : true
- name: PatchyGrass
  description: |-
    Patchy Grass.
    Hold <mod1> for single use.
    Hold <mod2> for free mode.
  icon: iron_wall_2x2
  command: terrain paint=patches <area>
  continuous: -<mod1>
  terrainGrid: -<mod2>
  snapGround : true
- name: MossyPaving
  description: |-
    Paving with moss.
    Hold <mod1> for single use.
    Hold <mod2> for free mode.
  icon: trophygreydwarfshaman
  command: terrain paint=paved_moss <area>
  continuous: -<mod1>
  terrainGrid: -<mod2>
  snapGround : true
- name: DarkPaving
  description: |-
    Dark Paving.
    Hold <mod1> for single use.
    Hold <mod2> for free mode.
  icon: tar
  command: terrain paint=paved_dark <area>
  continuous: -<mod1>
  terrainGrid: -<mod2>
  snapGround : true
- name: Reset
  description: |-
    Resets terrain.
    Hold <mod1> for single use.
    Hold <mod2> for free mode.
  icon: Hoe
  command: terrain reset <area>
  continuous: -<mod1>
  terrainGrid: -<mod2>
  snapGround : true
- name: Slope
  description: Slope between you and aim point.
  icon: wood_wall_roof_45
  command: terrain slope <to>
  targetEdge: true
  initialShape: rectangle
- name: Remove
  description: |-
    Removes objects.
    Hold <mod1> to also reset the terrain.
  icon: softdeath
  commands:
  - command: object remove id=* <area>
  - command: terrain reset <area>
    keys: <mod1>
  highlight: true
- name: Tame
  description: |-
    Tames creatures.
    Hold <mod1> to untame
  icon: Carrot
  commands:
  - command: object tame <area>
    keys: -<mod1>
  - command: object wild <area>
    keys: <mod1>
";
    }
}
