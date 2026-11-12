# Handoff: Infinity Hammer Tool Conflict & Resolution

**Status:** RESOLVED in TerroritySmellatory v0.4.2  
**Date:** 2026-10-14  
**Component:** `TerroritySmellatory.Integration.InfinityHammerBridge`  
**Related Mods:** `JereKuusela-Infinity_Hammer` (v1.84 - v1.90), `JereKuusela-Server_devcommands`

---

## 1. Problem Statement & Symptoms

When `TerroritySmellatory` is installed on a client alongside `Infinity Hammer`:
1. The Hammer's build menu only displays the 4 Territory tools (`Territory`, `Territory remove`, `Territory edit`, `Territory combine`).
2. The Hammer is missing its default selection tools: **Pipette**, **Building pipette**, and **Area pipette**.
3. The Hoe is completely stripped of all Infinity Hammer extensions: **Remove tool**, **Slope**, **Level**, **Raise**, **Pave**, **Grass**, **Dirt**, **Cultivate**, **Reset**, **Tame**, etc.
4. Client `BepInEx/LogOutput.log` reports:
   ```text
   [Info :Server Devcommands] Reloading 4 tools.
   ```
   Only the 4 Territory tools are ever loaded.

---

## 2. Root Cause Mechanism in Infinity Hammer

Infinity Hammer's tool system (`InfinityHammer.ToolManager` / ServerDevcommands `Yaml`) discovers tools using:
```csharp
public static string DefaultFile = "infinity_tools.yaml";
public static string Folder = "tools";
public static string Pattern = "infinity_tools*.yaml";
```

During initialization in `ToolManager.FromFiles()`:
```csharp
Yaml.LoadDictFromDirectory<List<ToolData>>(Paths.ConfigPath, Pattern, Folder, LoadTool);
if (ToolData.Count == 0)
{
    if (Yaml.AnyFileExists(Paths.ConfigPath, Pattern, Folder))
        Log.Warning($"Failed to load any tools.");
    else
    {
        CreateFile(); // <--- Writes Paths.ConfigPath/infinity_tools.yaml with InitialData.Get()
        Yaml.LoadDictFromDirectory<List<ToolData>>(Paths.ConfigPath, Pattern, Folder, LoadTool);
    }
}
```

### The Conflict:
1. `InfinityHammerBridge.EnsureToolFile()` creates `BepInEx/config/infinity_tools_terroritysmellatory.yaml`.
2. Because the file name starts with `infinity_tools`, it matches the wildcard pattern `"infinity_tools*.yaml"`.
3. When Infinity Hammer's `ToolManager` scans `BepInEx/config`, it loads the 4 territory tools from `infinity_tools_terroritysmellatory.yaml`.
4. As a result, `ToolData.Count` is 4, which is **not 0**.
5. Infinity Hammer's check `if (ToolData.Count == 0)` evaluates to **false**.
6. Therefore, **`ToolManager.CreateFile()` is NEVER executed**.
7. The default `infinity_tools.yaml` (which contains the Hammer's pipettes and all 14 Hoe tools) is never generated, or remains an empty 0-byte file if touched.
8. Infinity Hammer proceeds with *only* the tools defined in `infinity_tools_terroritysmellatory.yaml`.

---

## 3. The Required Fix in TerroritySmellatory

To prevent TerroritySmellatory from suppressing Infinity Hammer's default toolset on new installations or clean profiles, `InfinityHammerBridge.EnsureToolFile()` must ensure `infinity_tools.yaml` exists and is populated before (or when) writing its own tool file.

### Implementation Strategy for `Integration/InfinityHammerBridge.cs`

In `InfinityHammerBridge.EnsureToolFile()`:
1. Check if `infinity_tools.yaml` exists in `Paths.ConfigPath` (and has size > 0).
2. If `infinity_tools.yaml` is missing or empty (0 bytes):
   - **First Attempt (Reflection):** If the `InfinityHammer` assembly is present in AppDomain, call `InfinityHammer.ToolManager.CreateFile()` via reflection.
   - **Second Attempt (Fallback):** If reflection is unavailable or fails, directly write the standard default YAML content into `Paths.ConfigPath/infinity_tools.yaml`.
3. Then proceed with writing `infinity_tools_terroritysmellatory.yaml` as usual.

---

## 4. Proposed Code Changes for `Integration/InfinityHammerBridge.cs`

```csharp
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
        Type toolManagerType = Type.GetType("InfinityHammer.ToolManager, InfinityHammer");
        if (toolManagerType != null)
        {
            var createMethod = toolManagerType.GetMethod("CreateFile", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (createMethod != null)
            {
                createMethod.Invoke(null, null);
                createdViaReflection = File.Exists(defaultPath) && new FileInfo(defaultPath).Length > 0;
            }
        }

        if (!createdViaReflection)
        {
            // Fallback: write standard default Infinity Hammer toolset
            File.WriteAllText(defaultPath, DefaultInfinityHammerToolsYaml);
            Plugin.Log.LogInfo("Seeded default infinity_tools.yaml to prevent Infinity Hammer tool suppression.");
        }
    }
    catch (Exception ex)
    {
        Plugin.Log.LogWarning($"Could not verify/seed default infinity_tools.yaml: {ex.Message}");
    }
}

private const string DefaultInfinityHammerToolsYaml = @"hammer:
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
```

---

## 5. Documentation Update for `libs-Tools`

In `libs-Tools/INFINITY-HAMMER-TOOLS-FACTS.md`, under section 1, add the following warning:
> **Bootstrap hazard:** Infinity Hammer only generates its default `infinity_tools.yaml` (pipettes, hoe tools) if `ToolData.Count == 0` after scanning all files matching `infinity_tools*.yaml`. If your mod writes `infinity_tools_<mod>.yaml` onto a clean profile before `infinity_tools.yaml` has been generated, IH sees tools loaded, skips `CreateFile()`, and default tools are permanently lost. Ensure `infinity_tools.yaml` exists and is non-empty before or when writing your custom tool file.
