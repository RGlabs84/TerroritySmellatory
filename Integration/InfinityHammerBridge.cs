using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;

namespace TerroritySmellatory
{
    /// <summary>
    /// Marking, removing, picking and combining territories with Infinity Hammer - with no code reference to
    /// Infinity Hammer at all. IH is for PLACEMENT only: nothing a player experiences depends on it.
    /// </summary>
    /// <remarks>
    /// Infinity Hammer 1.84's tool system (InfinityTools, decompiled in libs-Tools/1.0/thirdparty/
    /// terrain-mods-2026-09-22) loads every BepInEx/config/infinity_tools*.yaml and watches the folder, so a
    /// file dropped there becomes hammer tools within moments. A tool is a console command with
    /// placeholders; on placement IH substitutes the ghost's position (&lt;x&gt; &lt;y&gt; &lt;z&gt;), the ruler's size
    /// (&lt;r&gt; radius, &lt;w&gt;/&lt;d&gt; half-width/half-depth) and rotation (&lt;a&gt;), strips whichever of &lt;r&gt; or &lt;w&gt;/&lt;d&gt;
    /// the chosen shape does not use (ToolSelection.RemoveUnusedShapeParameters), and runs it through
    /// Console.TryRunCommand on the admin's own machine (PlacedCommand.Start). The commands are this mod's
    /// terr_place / terr_remove_at / terr_edit_at / terr_combine_at (Integration/Commands.cs), which carry the
    /// request to the server - the server, not the hammer, decides.
    ///
    /// A tool whose command has no &lt;r&gt;/&lt;w&gt;/&lt;d&gt; gets no ruler, just the aim point: exactly what "the
    /// territory I am pointing at" needs for remove, edit and combine.
    /// </remarks>
    internal static class InfinityHammerBridge
    {
        internal const string Guid = "infinity_hammer";
        internal const string FileName = "infinity_tools_terroritysmellatory.yaml";

        internal const string PlaceTool = "Territory";
        internal const string RemoveTool = "Territory remove";
        internal const string EditTool = "Territory edit";
        internal const string CombineTool = "Territory combine";

        /// <summary>Keys are parsed by name, so their order does not matter.</summary>
        internal const string PlaceCommand = "terr_place x=<x> z=<z> y=<y> r=<r> w=<w> d=<d> a=<a>";
        internal const string RemoveCommand = "terr_remove_at x=<x> z=<z>";
        internal const string EditCommand = "terr_edit_at x=<x> z=<z>";
        internal const string CombineCommand = "terr_combine_at x=<x> z=<z>";

        /// <summary>First line of a file this mod owns. Remove it and the file is yours; the mod stops rewriting it.</summary>
        private const string ManagedMarker = "# Managed by TerroritySmellatory";

        internal static bool Installed => Chainloader.PluginInfos.ContainsKey(Guid);

        internal static string ToolFilePath => Path.Combine(Paths.ConfigPath, FileName);

        internal static void EnsureToolFile()
        {
            if (!Installed || !TSConfig.WriteHammerTool.Value) return;

            try
            {
                // 1. Ensure Infinity Hammer's default tools file is bootstrapped first.
                // If infinity_tools_terroritysmellatory.yaml exists while infinity_tools.yaml does not,
                // Infinity Hammer's wildcard scan sees ToolData.Count > 0 and will never generate its default tools!
                EnsureInfinityHammerDefaultTools();

                // 2. Ensure TerroritySmellatory's tools file is written.
                string content = BuildYaml();
                string path = ToolFilePath;
                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path);
                    if (!existing.StartsWith(ManagedMarker, StringComparison.Ordinal))
                    {
                        Plugin.Log.LogInfo($"{FileName} has been taken over by hand (no '{ManagedMarker}' first line) - leaving it alone.");
                        return;
                    }
                    if (existing == content) return;
                }

                File.WriteAllText(path, content);
                Plugin.Log.LogInfo($"Wrote the Infinity Hammer territory tools to {path}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not write the Infinity Hammer tool file (the panel's hammer buttons still work): {ex.Message}");
            }
        }

        private static void EnsureInfinityHammerDefaultTools()
        {
            try
            {
                string defaultPath = Path.Combine(Paths.ConfigPath, "infinity_tools.yaml");
                string toolsSubdirPath = Path.Combine(Paths.ConfigPath, "tools", "infinity_tools.yaml");

                bool hasDefaultFile = (File.Exists(defaultPath) && new FileInfo(defaultPath).Length > 0) ||
                                      (File.Exists(toolsSubdirPath) && new FileInfo(toolsSubdirPath).Length > 0);

                if (hasDefaultFile) return;

                // Try invoking Infinity Hammer's own CreateFile via reflection
                bool createdViaReflection = false;
                Type toolManagerType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    toolManagerType = asm.GetType("InfinityTools.ToolManager") ?? asm.GetType("InfinityHammer.ToolManager");
                    if (toolManagerType != null) break;
                }
                if (toolManagerType == null)
                {
                    toolManagerType = Type.GetType("InfinityTools.ToolManager, InfinityHammer") ??
                                      Type.GetType("InfinityHammer.ToolManager, InfinityHammer");
                }

                if (toolManagerType != null)
                {
                    var createMethod = toolManagerType.GetMethod("CreateFile", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (createMethod != null)
                    {
                        createMethod.Invoke(null, null);
                        createdViaReflection = (File.Exists(defaultPath) && new FileInfo(defaultPath).Length > 0) ||
                                               (File.Exists(toolsSubdirPath) && new FileInfo(toolsSubdirPath).Length > 0);
                        if (createdViaReflection)
                            Plugin.Log.LogInfo("Invoked Infinity Hammer's ToolManager.CreateFile() to seed default infinity_tools.yaml.");
                    }
                }

                if (!createdViaReflection)
                {
                    // Fallback: write standard default Infinity Hammer toolset
                    Directory.CreateDirectory(Paths.ConfigPath);
                    File.WriteAllText(defaultPath, PlacementArgs.DefaultInfinityHammerToolsYaml);
                    Plugin.Log.LogInfo("Seeded default infinity_tools.yaml to prevent Infinity Hammer tool suppression.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not verify/seed default infinity_tools.yaml: {ex.Message}");
            }
        }

        private static string BuildYaml()
        {
            string size = TSConfig.HammerInitialSize.Value.ToString("0.##", CultureInfo.InvariantCulture);
            string seconds = Commands.CombineSeconds.ToString("0", CultureInfo.InvariantCulture);
            return
                ManagedMarker + " - delete this line to take the file over.\n" +
                "# Territory tools. Rules for new territories are set in the Territory panel (the Home key, or 'terr').\n" +
                "hammer:\n" +
                "- name: " + PlaceTool + "\n" +
                "  description: |-\n" +
                "    Marks a territory with the rules from the Territory panel, or adds\n" +
                "    ground to (or moves a part of) a territory armed for it in the panel.\n" +
                "    Scroll to resize; the hammer's shape key cycles circle, square and rectangle.\n" +
                "  icon: guard_stone\n" +
                "  command: " + PlaceCommand + "\n" +
                "  initialShape: circle\n" +
                "  initialSize: " + size + "\n" +
                "  snapGround: true\n" +
                "- name: " + RemoveTool + "\n" +
                "  description: |-\n" +
                "    Removes the territory you place this inside.\n" +
                "    Place it twice on the same territory to confirm.\n" +
                "  icon: softdeath\n" +
                "  command: " + RemoveCommand + "\n" +
                "- name: " + EditTool + "\n" +
                "  description: |-\n" +
                "    Opens the Territory panel on the territory you place this inside,\n" +
                "    to change its rules or members, or move and reshape it.\n" +
                "  icon: sign\n" +
                "  command: " + EditCommand + "\n" +
                "- name: " + CombineTool + "\n" +
                "  description: |-\n" +
                "    Place inside the territory to keep, then inside another within " + seconds + " s.\n" +
                "    The second joins the first: its ground, owner and members.\n" +
                "  icon: Chain\n" +
                "  command: " + CombineCommand + "\n";
        }

        internal static bool SelectPlace(out string why) => Select(PlaceTool, PlaceCommand, out why);

        internal static bool SelectRemove(out string why) => Select(RemoveTool, RemoveCommand, out why);

        internal static bool SelectEdit(out string why) => Select(EditTool, EditCommand, out why);

        internal static bool SelectCombine(out string why) => Select(CombineTool, CombineCommand, out why);

        /// <summary>Puts a territory tool on the admin's hammer. False, with the reason, when it cannot.</summary>
        private static bool Select(string tool, string command, out string why)
        {
            why = "";
            if (!Installed)
            {
                why = "Infinity Hammer is not installed on this machine.";
                return false;
            }
            if (Console.instance == null)
            {
                why = "The console is not ready yet.";
                return false;
            }

            // Prefer the named tool (icon, description, starting size); fall back to an ad-hoc command tool when
            // the yaml does not define it - no file at all, or one taken over by hand before this tool existed
            // (a 0.1.x file has no Territory combine). IH's `tool` with an unknown name builds a tool whose
            // command is the NAME, and placing it would run "Territory combine" as a console command.
            bool named = ToolFileDefines(tool);
            string verb = named ? "tool" : "tool_cmd";

            if (!Terminal.commands.TryGetValue(verb, out Terminal.ConsoleCommand cmd))
            {
                why = $"Infinity Hammer's '{verb}' command is missing - is Infinity Hammer 1.84 or newer loaded?";
                return false;
            }
            if (!cmd.IsValid(Console.instance))
            {
                why = "Infinity Hammer's tools need devcommands on - enable them first (Server Devcommands).";
                return false;
            }

            Console.instance.TryRunCommand(named ? $"tool {tool}" : $"tool_cmd {command}");
            return true;
        }

        private static readonly HashSet<string> _toldUnnamed = new HashSet<string>();

        /// <summary>Does the tool file define a tool of this name? Read afresh each time: it is small, and it may be edited by hand.</summary>
        private static bool ToolFileDefines(string tool)
        {
            string path = ToolFilePath;
            try
            {
                if (!File.Exists(path)) return false;
                if (DefinesTool(File.ReadAllText(path), tool)) return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not read {FileName} ({ex.Message}); using an unnamed hammer command for '{tool}'.");
                return false;
            }

            if (_toldUnnamed.Add(tool))
                Plugin.Log.LogInfo($"{FileName} does not define '{tool}' (taken over by hand?) - the hammer gets it as an unnamed command, without its icon. Put the marker line back, or copy the tool in, to name it.");
            return false;
        }

        /// <summary>
        /// Whether IH would find a tool of this name in this yaml: a "name:" key (IH's naming is camelCase, so the key
        /// is lower case), its value compared as IH's `tool` compares names - ignoring case. Pure, for a harness.
        /// </summary>
        internal static bool DefinesTool(string yaml, string tool) => PlacementArgs.DefinesTool(yaml, tool);
    }
}
