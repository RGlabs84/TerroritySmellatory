// Unity editor script: builds terroritysmellatory-guards.bundle, the territory guards' Meshy bodies (Visuals/GuardBodies.cs), which the
// csproj EMBEDS in TerroritySmellatory.dll. It is ValtimaOnline's BuildValtimaNpcBodies (unity/Editor/BuildValtimaNpcBodies.cs there), unchanged
// in what it does to a body, run over the two guard looks only - guard_v2 (the approved pilot guard, Generic) and guard_warrior_f (Humanoid) -
// with the shared Humanoid clips the second plays and no tool models. Its notes on the rigs, the A-pose and the landmines are kept below.
// Use: tools/build-guard-bodies.sh.
//
// (ValtimaOnline's header) Unity editor script: builds valtima-npc-bodies.bundle for VALtimaOnline, the Meshy town NPC bodies (Npcs/MeshyBodies.cs loads it from
// beside the DLL). Valkyries Cargo's IngvarBundleBuilder is the pattern and its landmines are kept: a graphics device or nothing, the FBX
// (never the GLB: glTFast's importer hides the rig settings), a rig from the model itself, the exporter's "Armature|" prefix cut off every
// clip name BEFORE the loop table is read, Read/Write on, scale 1, and the bundle checked by what is IN it, not by the exit code.
// Each body is tagged as its whole rigged prefab (LoadAsset<GameObject>(id) gives the SkinnedMeshRenderer, the armature, its Avatar and its own
// clips) plus its albedo and normal maps; the material is made at runtime from a donor material of the game's (the AFH lesson), so the bundle
// carries no shader and serves Windows and Linux alike.
//
// TWO KINDS OF BODY (2026-10-11, Britain's cast):
//   - guard_v2, the approved pilot guard: a GENERIC rig whose clips were all bought on his own skeleton. Exactly as before: plain clip names
//     (Idle, Walk, Attack ...), textures at 2048. Nothing is retargeted.
//   - every other body: a HUMANOID rig (an Avatar per body, Unity's own auto-map of Meshy's 24 bones; the queue's explicit bone map is
//     upside down and is never used). Its own clips are "<id>.Walk" (its own Casual_Walk, the only walk) and "<id>.Work" (its work loop).
//     The civilian clips (Idle, Talk, Wave ...) and the guard pack (Attack, Hit, Death ...) were bought only on the guard's rig; they are
//     imported ONCE as Humanoid clips from the guard's own bake (shared/guard_humanoid.fbx, a copy of guard_v2.fbx), saved as standalone
//     "h.<Name>" clips, and play on every Humanoid body through its Avatar. Walk and Work of the guard are NOT shared (every body walks with
//     its own Casual_Walk, nothing else).
//
// THE A-POSE. Meshy rigs every body in an A-pose whose arms hang 21 to 55 degrees off the vertical (tools/npc/build_body.py's study of the 24
// rigs). A Humanoid avatar made "from this model" takes the pose it finds as its reference, so the same muscle values would swing the arms to
// directions that differ by the difference of the two A-poses. Every Humanoid avatar here is therefore made with its reference pose put into a
// T-pose first (EnforceTPose: the arms level, the legs straight down, the feet where they were), the guard's shared source included, so the
// clips mean the same absolute limb directions on every body. VALTIMA_AVATAR=auto skips it (NpcBodyCheck compares the two).
//
// TOOLS (sheet v51, 2026-10-11): our own small tool models ("props/prop_<name>.fbx" with "prop_<name>_albedo.png", a row of flat colour swatches; tools/npc/build_props.py
// makes them in Blender) ride in the same bundle: Npcs/NpcProps.cs holds the saw, the broom and the crier's bell in a working NPC's right hand where the game has no such item
// (its own Hammer, FishingRod, Hoe and SwordIron are used as they are). A prop is a plain readable mesh (the plugin measures its shaft from the vertices) and its palette has no
// mip maps (every part of a model sits at the centre of one swatch, and a coarse mip would blend the swatches).
//
// Use: tools/build-npc-bodies.sh copies each body's bake (tools/npc/build_body.py output) into Assets/VALtimaNpcBodies/ and this file
// into Assets/Editor/, then runs -executeMethod BuildValtimaNpcBodies.Build with a graphics device. A failure throws (batch mode exits 1).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

public static class BuildTerritoryGuards
{
    private const string BundleName = "terroritysmellatory-guards.bundle";
    internal const string Folder = "Assets/TSGuards";
    internal const string SharedFolder = "Assets/TSGuards/shared";
    internal const string ClipFolder = "Assets/TSGuards/clips";
    internal const string PropFolder = "Assets/TSGuards/props";
    // the tool models the plugin asks the bundle for (Npcs/Rules/PropChoice.cs, PropSpec.Own): a bundle without one of them fails its check
    internal static readonly string[] RequiredProps = { };   // the guards hold the game's own atgeir: no tool models
    internal const string OutputFolder = "Build/TSGuards";
    internal const string GuardId = "guard_v2";              // the Generic body; every other id is Humanoid
    internal const string SharedSource = "guard_humanoid";   // shared/guard_humanoid.fbx: the guard's bake imported as Humanoid, the source of the shared clips
    internal const string SharedPrefix = "h.";               // the shared clips' names in the bundle
    private const int TexMaxGuard = 2048, TexMaxBody = 1024;

    // Auto: the avatar as Unity makes it from the model (its reference pose is whatever pose the FBX's nodes hold: Blender writes a frame of the
    // first take there, not the rest pose); Bind: the reference pose is the mesh's own bind pose (the A-pose the body was rigged in); TPose: that,
    // with the arms levelled and the legs straight (the default: see the note above)
    internal enum AvatarMode { Auto, Bind, TPose }
    internal static AvatarMode DefaultMode
    {
        get
        {
            string v = Environment.GetEnvironmentVariable("VALTIMA_AVATAR") ?? "";
            return v.Equals("auto", StringComparison.OrdinalIgnoreCase) ? AvatarMode.Auto : v.Equals("bind", StringComparison.OrdinalIgnoreCase) ? AvatarMode.Bind : AvatarMode.TPose;
        }
    }

    // clips that loop; every other clip plays once (a non-looping idle freezes on its last frame)
    internal static readonly HashSet<string> Looping = new HashSet<string> { "Idle", "IdleSway", "Walk", "Run", "Talk", "TalkHand", "Sit", "Drink", "Work" };

    // the guard's own clips that are NOT shared with the Humanoid bodies: a body walks with its own Casual_Walk and works with its own loop
    private static readonly HashSet<string> NotShared = new HashSet<string> { "Walk", "Work" };
    internal static bool SharedKeep(string clip) => !NotShared.Contains(clip);

    // the user's rule: nobody walks with anything but Casual_Walk ("no catwalks"). A clip with one of these in its name fails the build.
    private static readonly string[] Forbidden = { "Confident", "WalkGuard", "WalkWoman", "Walking_Woman", "walking_man", "Woman_Walk" };

    internal static string[] DiscoverBodies()
    {
        var only = (Environment.GetEnvironmentVariable("VALTIMA_BODIES") ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var ids = Directory.GetFiles(Folder, "*.fbx", SearchOption.TopDirectoryOnly).Select(p => Path.GetFileNameWithoutExtension(p)).OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (only.Length > 0) ids = ids.Where(i => only.Contains(i)).ToList();
        return ids.ToArray();
    }

    internal static bool IsHuman(string id) => id != GuardId;

    [MenuItem("TerroritySmellatory/Build Guard Bodies Bundle")]
    public static void Build()
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            throw new Exception("[VALtima] no graphics device: run the editor without -nographics");
        string[] bodies = DiscoverBodies();
        if (bodies.Length == 0) throw new Exception($"[VALtima] no body FBX in {Folder}: run tools/npc/build_body.py first");
        Debug.Log($"[VALtima] bodies: {string.Join(", ", bodies)}");

        var assets = new List<string>();
        foreach (string id in bodies)
        {
            string fbx = $"{Folder}/{id}.fbx", albedo = $"{Folder}/{id}_albedo.png", normal = $"{Folder}/{id}_normal.png";
            if (!File.Exists(albedo)) throw new Exception($"[VALtima] {albedo} not found");
            int texMax = id == GuardId ? TexMaxGuard : TexMaxBody;
            AssetDatabase.ImportAsset(fbx, ImportAssetOptions.ForceUpdate);
            ConfigureModel(id, fbx, IsHuman(id));
            ConfigureTexture(albedo, false, texMax);
            assets.Add(fbx);
            assets.Add(albedo);
            if (File.Exists(normal))
            {
                ConfigureTexture(normal, true, texMax);
                assets.Add(normal);
            }
        }
        if (bodies.Any(IsHuman)) assets.AddRange(MakeSharedClips());
        string[] props = DiscoverProps();
        foreach (string prop in props)
        {
            string fbx = $"{PropFolder}/prop_{prop}.fbx", palette = $"{PropFolder}/prop_{prop}_albedo.png";
            if (!File.Exists(palette)) throw new Exception($"[VALtima] {palette} not found");
            AssetDatabase.ImportAsset(fbx, ImportAssetOptions.ForceUpdate);
            ConfigureProp(prop, fbx);
            ConfigureTexture(palette, false, 1024, mips: false);
            assets.Add(fbx);
            assets.Add(palette);
        }
        Debug.Log($"[VALtima] tool models: {(props.Length > 0 ? string.Join(", ", props) : "none")}");

        // LANDMINE (Ingvar's 2): BuildAssetBundles compiles player scripts first; com.unity.collections fails there with CS7036 and the build
        // RETURNS WITHOUT THROWING, leaving a 0-byte bundle. The define makes that compile pass.
        var nbt = NamedBuildTarget.Standalone;
        string prev = PlayerSettings.GetScriptingDefineSymbols(nbt);
        bool had = prev.Split(';').Contains("ENABLE_UNITY_COLLECTIONS_CHECKS");
        if (!had) PlayerSettings.SetScriptingDefineSymbols(nbt, string.IsNullOrEmpty(prev) ? "ENABLE_UNITY_COLLECTIONS_CHECKS" : prev + ";ENABLE_UNITY_COLLECTIONS_CHECKS");
        Directory.CreateDirectory(OutputFolder);
        AssetBundleManifest manifest;
        try
        {
            var build = new AssetBundleBuild { assetBundleName = BundleName, assetNames = assets.ToArray() };
            manifest = BuildPipeline.BuildAssetBundles(OutputFolder, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        }
        finally
        {
            if (!had) PlayerSettings.SetScriptingDefineSymbols(nbt, prev);
        }
        string path = Path.Combine(OutputFolder, BundleName);
        long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (manifest == null || bytes == 0) throw new Exception("[VALtima] the bundle is missing or empty after the build: see the errors above");
        Verify(path, bytes, bodies, props);
    }

    // the tool models in props/ ("prop_saw.fbx" -> "saw")
    internal static string[] DiscoverProps()
    {
        if (!Directory.Exists(PropFolder)) return new string[0];
        return Directory.GetFiles(PropFolder, "prop_*.fbx", SearchOption.TopDirectoryOnly).Select(p => Path.GetFileNameWithoutExtension(p).Substring(5)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }

    // a tool model: a plain readable mesh at scale 1 (metres), no animation, no imported material (the plugin dresses it in the body's own material)
    private static void ConfigureProp(string name, string fbx)
    {
        var mi = AssetImporter.GetAtPath(fbx) as ModelImporter;
        if (mi == null) throw new Exception($"[VALtima] {fbx} did not import as a model");
        mi.animationType = ModelImporterAnimationType.None;
        mi.importAnimation = false;
        mi.materialImportMode = ModelImporterMaterialImportMode.None;
        mi.importCameras = false;
        mi.importLights = false;
        mi.isReadable = true;      // the plugin measures the shaft from the vertices
        mi.globalScale = 1f;       // the FBX is in metres already
        mi.meshCompression = ModelImporterMeshCompression.Off;
        mi.SaveAndReimport();
        var go = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
        var filter = go != null ? go.GetComponentInChildren<MeshFilter>(true) : null;
        if (filter == null || filter.sharedMesh == null) throw new Exception($"[VALtima] prop {name}: no mesh came out of {fbx}");
        var size = filter.sharedMesh.bounds.size;
        Debug.Log($"[VALtima] prop {name}: {filter.sharedMesh.vertexCount} vertices, {filter.sharedMesh.triangles.Length / 3} triangles, box {size.x:0.000} x {size.y:0.000} x {size.z:0.000} m, readable {filter.sharedMesh.isReadable}");
        AssetImporter.GetAtPath(fbx).SetAssetBundleNameAndVariant("", "");
    }

    // ---- the checks on the artefact -----------------------------------------------------------------------------------------------------------

    private static void Verify(string path, long bytes, string[] bodies, string[] props)
    {
        // the artefact, not the exit code: open it and list what is inside
        var bundle = AssetBundle.LoadFromFile(path);
        if (bundle == null) throw new Exception("[VALtima] the built bundle does not open");
        var problems = new List<string>();
        try
        {
            string[] names = bundle.GetAllAssetNames();
            Debug.Log($"[VALtima] built {path}: {bytes} bytes, {names.Length} asset(s)");
            var clips = bundle.LoadAllAssets<AnimationClip>().Where(c => !c.name.StartsWith("__preview")).ToArray();
            var clipNames = new HashSet<string>(clips.Select(c => c.name));
            foreach (var c in clips)
            {
                if (c.name.Contains("|")) problems.Add($"clip '{c.name}' still carries the exporter's prefix");
                foreach (string bad in Forbidden)
                    if (c.name.IndexOf(bad, StringComparison.OrdinalIgnoreCase) >= 0) problems.Add($"clip '{c.name}' is a forbidden walk");
            }
            foreach (string id in bodies)
            {
                var go = bundle.LoadAsset<GameObject>(id);
                var smr = go != null ? go.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
                if (go == null || smr == null) { problems.Add($"{id}: no rigged prefab in the bundle"); continue; }
                var animator = go.GetComponentInChildren<Animator>(true);
                bool human = animator != null && animator.avatar != null && animator.avatar.isHuman;
                string own = id == GuardId ? "" : id + ".";
                var mine = clipNames.Where(n => id == GuardId ? !n.Contains(".") : n.StartsWith(own, StringComparison.Ordinal)).OrderBy(n => n).ToArray();
                if (IsHuman(id) && !human) problems.Add($"{id}: the avatar in the bundle is not a valid Humanoid");
                if (!IsHuman(id) && human) problems.Add($"{id}: expected a Generic rig");
                foreach (string want in id == GuardId ? new[] { "Idle", "Walk" } : new[] { own + "Walk", own + "Work" })
                    if (!clipNames.Contains(want)) problems.Add($"{id}: no '{want}' clip");
                if (bundle.LoadAsset<Texture2D>(id + "_albedo") == null) problems.Add($"{id}: no albedo in the bundle");
                var albedo = bundle.LoadAsset<Texture2D>(id + "_albedo");
                Debug.Log($"[VALtima] {id}: {smr.bones.Length} bones, {smr.sharedMesh.vertexCount} vertices, {smr.sharedMesh.triangles.Length / 3} triangles, " +
                          $"{(human ? "Humanoid" : "Generic")}, albedo {(albedo != null ? albedo.width + "x" + albedo.height : "missing")}, own clips: {string.Join(", ", mine)}");
            }
            foreach (string need in RequiredProps)
                if (!props.Contains(need)) problems.Add($"no tool model '{need}' (props/prop_{need}.fbx): run tools/npc/build_props.py");
            foreach (string prop in props)
            {
                var pgo = bundle.LoadAsset<GameObject>("prop_" + prop);
                var pf = pgo != null ? pgo.GetComponentInChildren<MeshFilter>(true) : null;
                if (pf == null || pf.sharedMesh == null) { problems.Add($"prop {prop}: no mesh in the bundle"); continue; }
                if (!pf.sharedMesh.isReadable) problems.Add($"prop {prop}: its mesh is not readable");
                var palette = bundle.LoadAsset<Texture2D>("prop_" + prop + "_albedo");
                if (palette == null) problems.Add($"prop {prop}: no palette in the bundle");
                Debug.Log($"[VALtima] prop {prop}: {pf.sharedMesh.vertexCount} vertices, palette {(palette != null ? palette.width + "x" + palette.height : "missing")}");
            }
            var shared = clipNames.Where(n => n.StartsWith(SharedPrefix, StringComparison.Ordinal)).OrderBy(n => n).ToArray();
            Debug.Log($"[VALtima] shared Humanoid clips ({shared.Length}): {string.Join(", ", shared)}");
            if (bodies.Any(IsHuman) && shared.Length == 0) problems.Add("no shared Humanoid clips in the bundle");
            foreach (var c in clips.Where(c => c.name.StartsWith(SharedPrefix, StringComparison.Ordinal)))
                if (!c.isHumanMotion) problems.Add($"shared clip '{c.name}' is not a Humanoid clip");
        }
        finally
        {
            bundle.Unload(true);
        }
        if (problems.Count > 0) throw new Exception("[VALtima] the bundle is wrong:\n  " + string.Join("\n  ", problems));
        Debug.Log("[VALtima] bundle verified");
    }

    // ---- the models -----------------------------------------------------------------------------------------------------------------------------

    private static void ConfigureModel(string id, string fbx, bool human)
    {
        if (human) { ConfigureHuman(id, fbx, DefaultMode, null, null, true); return; }
        var mi = AssetImporter.GetAtPath(fbx) as ModelImporter;
        if (mi == null) throw new Exception($"[VALtima] {fbx} did not import as a model");
        mi.animationType = ModelImporterAnimationType.Generic;
        mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        mi.importAnimation = true;
        mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
        mi.importCameras = false;
        mi.importLights = false;
        mi.isReadable = true;      // a mesh without Read/Write gives EMPTY vertices to BakeMesh at runtime
        mi.globalScale = 1f;       // the FBX is in metres already
        mi.clipAnimations = Takes(mi, id, false, null);
        mi.SaveAndReimport();
        var clips = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview")).OrderBy(c => c.name).ToArray();
        foreach (var c in clips) Debug.Log($"[VALtima] {id} clip '{c.name}': {c.length:0.00} s, loop {c.isLooping}, humanoid {c.isHumanMotion}");
        var wrong = clips.Where(c => c.name.Contains("|")).Select(c => c.name).ToArray();
        if (wrong.Length > 0) throw new Exception($"[VALtima] {id}: clip names still carry the exporter's prefix: {string.Join(", ", wrong)}");
        foreach (string want in new[] { "Walk", "Idle" })
            if (!clips.Any(c => c.name == want)) throw new Exception($"[VALtima] {id}: no '{want}' clip");
        AssetImporter.GetAtPath(fbx).SetAssetBundleNameAndVariant("", "");
    }

    // One Humanoid model: an avatar from the model (Unity's own auto-map), its clips named and flagged, then (mode TPose) the avatar's reference pose
    // put into a T-pose. `keep` / `prefix` select and name the clips (the shared set); `materials` keeps the model's own material import.
    internal static void ConfigureHuman(string id, string fbx, AvatarMode mode, Func<string, bool> keep, string prefix, bool materials)
    {
        var mi = AssetImporter.GetAtPath(fbx) as ModelImporter;
        if (mi == null) throw new Exception($"[VALtima] {fbx} did not import as a model");
        mi.animationType = ModelImporterAnimationType.Human;
        mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;   // Unity's own auto-map: the queue's explicit map is upside down
        mi.importAnimation = true;
        mi.materialImportMode = materials ? ModelImporterMaterialImportMode.ImportStandard : ModelImporterMaterialImportMode.None;
        mi.importCameras = false;
        mi.importLights = false;
        mi.isReadable = true;
        mi.globalScale = 1f;
        mi.clipAnimations = Takes(mi, id, true, keep, prefix);
        mi.SaveAndReimport();
        RequireHumanoid(id, fbx, "from the model");
        if (mode != AvatarMode.Auto)
        {
            WriteReferencePose(id, fbx, mode == AvatarMode.TPose);
            RequireHumanoid(id, fbx, mode == AvatarMode.TPose ? "in a T-pose" : "in its bind pose");
        }
        var clips = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview")).OrderBy(c => c.name).ToArray();
        foreach (var c in clips) Debug.Log($"[VALtima] {id} clip '{c.name}': {c.length:0.00} s, loop {c.isLooping}, humanoid {c.isHumanMotion}");
        var wrong = clips.Where(c => c.name.Contains("|")).Select(c => c.name).ToArray();
        if (wrong.Length > 0) throw new Exception($"[VALtima] {id}: clip names still carry the exporter's prefix: {string.Join(", ", wrong)}");
        if (keep == null)
        {
            string own = (prefix ?? id + ".");
            foreach (string want in new[] { "Walk", "Work" })
                if (!clips.Any(c => c.name == own + want)) throw new Exception($"[VALtima] {id}: no '{own + want}' clip");
        }
        AssetImporter.GetAtPath(fbx).SetAssetBundleNameAndVariant("", "");
    }

    // Puts every bone of an instantiated model into the pose its mesh was skinned in (bone-to-world = the renderer's matrix times the inverse bind
    // pose), parents before children. An FBX from Blender holds a frame of its first take in its nodes (the walk's first step), never the rest pose,
    // so neither Unity's own avatar nor a measurement may start from the nodes as they come.
    internal static void SampleBindPose(GameObject go)
    {
        foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Transform[] bones = smr.bones;
            if (smr.sharedMesh == null || bones == null || bones.Length == 0 || smr.sharedMesh.bindposes.Length != bones.Length) continue;
            Matrix4x4[] binds = smr.sharedMesh.bindposes;
            var order = Enumerable.Range(0, bones.Length).Where(i => bones[i] != null).OrderBy(i => Depth(bones[i])).ToArray();
            foreach (int i in order)
            {
                Matrix4x4 m = smr.transform.localToWorldMatrix * binds[i].inverse;
                bones[i].SetPositionAndRotation(m.GetColumn(3), m.rotation);
            }
        }
    }

    private static int Depth(Transform t) { int d = 0; for (; t.parent != null; t = t.parent) d++; return d; }

    // The avatar's reference pose: the bind pose (and, with `tpose`, a T-pose made of it). The model is instantiated, every bone put into its bind pose,
    // then (T-pose) the arms levelled (upper arm and forearm along the line from the body's right to its left, shortest-arc, so the palms keep facing
    // down) and the legs put straight down with the feet keeping their own flat turn; every transform's local place goes into the importer's
    // HumanDescription.skeleton, which Unity takes as the pose the avatar's muscles are measured from. The mesh and its skin are not touched (the
    // animator moves the bones, the bind poses stay the A-pose the mesh was skinned in).
    internal static void WriteReferencePose(string id, string fbx, bool tpose)
    {
        var mi = AssetImporter.GetAtPath(fbx) as ModelImporter;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
        if (mi == null) throw new Exception($"[VALtima] {id}: {fbx} is not a model");
        if (prefab == null) throw new Exception($"[VALtima] {id}: {fbx} did not load for its reference pose");
        HumanDescription hd = mi.humanDescription;
        if (hd.human == null || hd.human.Length < 15) throw new Exception($"[VALtima] {id}: the auto-map left {(hd.human == null ? 0 : hd.human.Length)} bones in the description");
        var go = UnityEngine.Object.Instantiate(prefab);
        go.name = prefab.name;
        try
        {
            var all = go.GetComponentsInChildren<Transform>(true);
            Transform T(string n) => all.FirstOrDefault(t => t.name == n) ?? throw new Exception($"[VALtima] {id}: no bone '{n}'");
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            string nodes = $"arms {ArmAngle(T, "Left"):0}/{ArmAngle(T, "Right"):0} deg off vertical, thighs {LegAngle(T, "Left"):0}/{LegAngle(T, "Right"):0}";
            SampleBindPose(go);
            Vector3 left = T("LeftUpLeg").position - T("RightUpLeg").position;
            left.y = 0f;
            left.Normalize();
            string bind = $"arms {ArmAngle(T, "Left"):0}/{ArmAngle(T, "Right"):0} deg off vertical, thighs {LegAngle(T, "Left"):0}/{LegAngle(T, "Right"):0}";
            string after = "";
            if (tpose)
            {
                foreach (string side in new[] { "Left", "Right" })
                {
                    Vector3 dir = side == "Left" ? left : -left;
                    Transform arm = T(side + "Arm"), fore = T(side + "ForeArm"), hand = T(side + "Hand");
                    Swing(arm, fore.position - arm.position, dir);
                    Swing(fore, hand.position - fore.position, dir);
                    Transform up = T(side + "UpLeg"), leg = T(side + "Leg"), foot = T(side + "Foot");
                    Quaternion footKeeps = foot.rotation;
                    Swing(up, leg.position - up.position, Vector3.down);
                    Swing(leg, foot.position - leg.position, Vector3.down);
                    foot.rotation = footKeeps;
                }
                after = $" -> T-pose arms {ArmAngle(T, "Left"):0}/{ArmAngle(T, "Right"):0}, thighs {LegAngle(T, "Left"):0}/{LegAngle(T, "Right"):0}";
            }
            hd.skeleton = all.Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray();
            mi.humanDescription = hd;
            mi.SaveAndReimport();
            Debug.Log($"[VALtima] {id}: avatar reference pose: the FBX's nodes held {nodes}; bind pose {bind}{after} ({hd.skeleton.Length} transforms)");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    private static void Swing(Transform bone, Vector3 from, Vector3 to) { bone.rotation = Quaternion.FromToRotation(from, to) * bone.rotation; }
    private static float ArmAngle(Func<string, Transform> T, string side) => Vector3.Angle(T(side + "ForeArm").position - T(side + "Arm").position, Vector3.down);
    private static float LegAngle(Func<string, Transform> T, string side) => Vector3.Angle(T(side + "Leg").position - T(side + "UpLeg").position, Vector3.down);

    // the clip list of one model: names without the exporter's prefix (then the body's id for a Humanoid), loop flags, the root baked into the pose
    // (in place: the game moves the NPC, not the clip). `keep` (when not null) keeps only the clips it names.
    internal static ModelImporterClipAnimation[] Takes(ModelImporter mi, string id, bool human, Func<string, bool> keep, string prefixOverride = null)
    {
        var takes = mi.defaultClipAnimations.Where(t => !t.name.StartsWith("__preview")).ToList();
        var result = new List<ModelImporterClipAnimation>();
        foreach (var t in takes)
        {
            int bar = t.name.LastIndexOf('|');   // Blender names every action "Armature|Clip": strip it FIRST (Ingvar's landmine 5)
            string plain = bar >= 0 ? t.name.Substring(bar + 1) : t.name;
            if (keep != null && !keep(plain)) continue;
            t.name = (prefixOverride ?? (human ? id + "." : "")) + plain;
            t.loopTime = Looping.Contains(plain);
            t.lockRootRotation = true;
            t.lockRootPositionXZ = true;                 // in place: the game moves the NPC, not the clip
            if (human)
            {
                // a Humanoid clip as authored (Original), all of it kept in the pose: the NPC is moved by the game (root motion is off)
                t.lockRootHeightY = true;
                t.keepOriginalOrientation = true;
                t.keepOriginalPositionY = true;
                t.keepOriginalPositionXZ = true;
                t.loopPose = t.loopTime;
            }
            else
            {
                t.keepOriginalPositionY = true;
                t.keepOriginalPositionXZ = false;
            }
            result.Add(t);
        }
        return result.ToArray();
    }

    private static void RequireHumanoid(string id, string fbx, string how)
    {
        var mi = AssetImporter.GetAtPath(fbx) as ModelImporter;
        var avatar = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<Avatar>().FirstOrDefault();
        if (avatar == null || !avatar.isValid || !avatar.isHuman)
            throw new Exception($"[VALtima] {id}: Unity did not make a valid Humanoid avatar from the model (valid {avatar != null && avatar.isValid}, human {avatar != null && avatar.isHuman})");
        Debug.Log($"[VALtima] {id}: Humanoid avatar {how}, {mi.humanDescription.human.Length} bones mapped");
    }

    // The civilian clips and the guard pack, imported ONCE as Humanoid from the guard's own bake and saved as standalone clips ("h.<Name>"):
    // a Humanoid clip is the same on every avatar, which is the retarget. The guard's Walk and Work stay out (see NotShared).
    private static List<string> MakeSharedClips()
    {
        string fbx = $"{SharedFolder}/{SharedSource}.fbx";
        if (!File.Exists(fbx)) throw new Exception($"[VALtima] {fbx} not found: tools/build-npc-bodies.sh copies guard_v2.fbx there");
        AssetDatabase.ImportAsset(fbx, ImportAssetOptions.ForceUpdate);
        ConfigureHuman(SharedSource, fbx, DefaultMode, SharedKeep, SharedPrefix, false);

        if (Directory.Exists(ClipFolder)) foreach (string old in Directory.GetFiles(ClipFolder, "*.anim")) AssetDatabase.DeleteAsset(old.Replace('\\', '/'));
        Directory.CreateDirectory(ClipFolder);
        var made = new List<string>();
        foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview")).OrderBy(c => c.name))
        {
            var copy = UnityEngine.Object.Instantiate(clip);
            copy.name = clip.name;
            string p = $"{ClipFolder}/{clip.name}.anim";
            AssetDatabase.CreateAsset(copy, p);
            made.Add(p);
            Debug.Log($"[VALtima] shared clip '{copy.name}': {copy.length:0.00} s, loop {copy.isLooping}, humanoid {copy.isHumanMotion}");
        }
        AssetDatabase.SaveAssets();
        if (made.Count == 0) throw new Exception("[VALtima] no shared clips came out of " + fbx);
        AssetImporter.GetAtPath(fbx).SetAssetBundleNameAndVariant("", "");
        return made;
    }

    private static void ConfigureTexture(string path, bool normal, int maxSize, bool mips = true)
    {
        var ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) throw new Exception($"[VALtima] {path} did not import as a texture");
        ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        ti.maxTextureSize = maxSize;
        ti.textureCompression = mips ? TextureImporterCompression.Compressed : TextureImporterCompression.Uncompressed;   // a palette's flat swatches stay exactly as painted
        ti.crunchedCompression = false;
        ti.sRGBTexture = !normal;
        ti.mipmapEnabled = mips;
        ti.filterMode = mips ? FilterMode.Bilinear : FilterMode.Point;
        ti.isReadable = false;
        ti.SaveAndReimport();
        AssetImporter.GetAtPath(path).SetAssetBundleNameAndVariant("", "");
    }
}
