using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TerroritySmellatory;
using UnityEngine;

namespace TerrE2E
{
    /// <summary>
    /// The command lines Infinity Hammer's three territory tools run, built the way IH 1.84 builds them:
    /// the mod's own templates (InfinityHammerBridge.PlaceCommand / RemoveCommand / EditCommand, read from the
    /// DLL under test) through ToolSelection.HandleCommand's substitution, ported as in the mod's off-game
    /// harness (tests/Program.cs, IhSubstitute). IH itself is not installed in the rig: this is its output.
    /// </summary>
    internal static class IhCommand
    {
        internal enum RulerShape { Circle, Square, Rectangle }

        private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);

        internal static string Place(RulerShape shape, Vector3 pos, float halfX, float halfZ, float yaw) =>
            Substitute(InfinityHammerBridge.PlaceCommand, shape, halfX, 0f, halfZ, pos, yaw);

        internal static string Remove(Vector3 pos) => Substitute(InfinityHammerBridge.RemoveCommand, RulerShape.Circle, 0f, 0f, 0f, pos, 0f);

        internal static string Edit(Vector3 pos) => Substitute(InfinityHammerBridge.EditCommand, RulerShape.Circle, 0f, 0f, 0f, pos, 0f);

        /// <summary>The "Territory combine" tool (0.2.0): a const of the mod that 0.1.1 lacks, so it is read by reflection.</summary>
        internal static string Combine(Vector3 pos) => Substitute(Features.CombineTemplate, RulerShape.Circle, 0f, 0f, 0f, pos, 0f);

        internal static RulerShape ShapeOf(TerritoryShapeKind kind) =>
            kind == TerritoryShapeKind.Circle ? RulerShape.Circle : kind == TerritoryShapeKind.Square ? RulerShape.Square : RulerShape.Rectangle;

        private static string Substitute(string command, RulerShape shape, float X, float Y, float Z, Vector3 pos, float yaw)
        {
            string text = F(X);
            string text2 = F(Math.Min(X, Z));
            string text3 = F(Math.Max(X, Z));
            string d = F(Z);
            string w = F(X);
            if (shape == RulerShape.Circle) { text2 = text; text3 = text; }
            if (shape != RulerShape.Rectangle) d = w;
            if (shape == RulerShape.Square) { text2 = text; text3 = text; }
            if (shape == RulerShape.Rectangle) { text2 = w; text3 = w; }

            string c = command;
            if (c.Contains("<r>") && (c.Contains("<w>") || c.Contains("<d>")))
            {
                bool round = shape == RulerShape.Circle;
                List<string> parts = c.Split(' ').ToList();
                for (int i = parts.Count - 1; i > -1; i--)
                {
                    if (round && (parts[i].Contains("<w>") || parts[i].Contains("<d>"))) parts.RemoveAt(i);
                    else if (!round && parts[i].Contains("<r>")) parts.RemoveAt(i);
                }
                c = string.Join(" ", parts);
            }

            c = c.Replace("<d>", d);
            return c.Replace("<r>", text).Replace("<r2>", text3).Replace("<w>", w).Replace("<w2>", text3)
                .Replace("<a>", F(yaw)).Replace("<x>", F(pos.x)).Replace("<y>", F(pos.y)).Replace("<z>", F(pos.z))
                .Replace("<h>", F(Y));
        }
    }
}
