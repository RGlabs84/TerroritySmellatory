using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using TerroritySmellatory;

namespace TerrE2E
{
    /// <summary>
    /// Proof that the barrier's flare puts pixels on a screen: only a --graphics client (a real GL device on the
    /// rig's private Xvfb) can run it. The driver stands a flare six metres in front of the game's own camera, then
    /// renders that camera into a texture at three moments - in the flash, mid-ripple and after the fade - each
    /// time twice in the same instant, with the flare's objects switched on and off, and compares the pair. "BarrierFlare.Drawn rose" only proves the mod ran its own code; this proves the GPU drew blue
    /// where there was none, that none of it is the pink of a broken shader, and that it is gone afterwards. The four
    /// frames are kept as PNGs in the run's shots/ directory. 0.3.0's flare would have failed here: it built nothing.
    /// </summary>
    internal static class FlareShot
    {
        private static readonly MethodInfo FlareMethod = typeof(Territory).Assembly.GetType("TerroritySmellatory.BarrierFlare")
            ?.GetMethod("Flare", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        private const int W = 640, H = 360;

        /// <summary>The DLL has BarrierFlare.Flare and this process has a graphics device.</summary>
        internal static bool Available => FlareMethod != null && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>
        /// The flare on a black backdrop: a camera of the driver's own, parked at 9000 m in the sky where no world
        /// geometry is, clear colour black, so every lit pixel is the flare's and the numbers are clean. Works in the
        /// main menu as well as in the world.
        /// </summary>
        internal static IEnumerator RunIsolated(string id, string tag)
        {
            var go = new GameObject("TerrE2E_FlareCamera");
            go.transform.position = new Vector3(9000f, 9000f, 9000f);
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;                     // rendered by hand, never to the screen
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = ~0;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 60f;
            cam.fieldOfView = 60f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            try { yield return Run(id, tag, cam, "on a black backdrop"); }
            finally { UnityEngine.Object.Destroy(go); }
        }

        /// <summary>The flare in front of the game's own camera: what a player sees, post-processing and all.</summary>
        internal static IEnumerator RunInGame(string id, string tag)
        {
            Camera cam = FindCamera();
            if (cam == null)
            {
                Report.Fail(id, "the flare puts blue pixels on a real frame", "no enabled camera to render");
                yield break;
            }
            yield return Run(id, tag, cam, $"in front of the game camera '{cam.name}'");
        }

        private static IEnumerator Run(string id, string tag, Camera cam, string where)
        {

            string dir = Path.Combine(E2EEnv.RunDir ?? ".", "shots");
            Directory.CreateDirectory(dir);

            int drawn0 = Features.FlareDrawn;
            try { Capture(cam, Path.Combine(dir, tag + "-0-before.png")); }   // for the eye only: the numbers compare on/off pairs taken in the same instant
            catch (Exception ex) { Report.Fail(id, "the flare puts blue pixels on a real frame", "could not render the camera: " + ex); yield break; }

            // Six metres ahead of the camera, on the level, facing back at it; the ground estimate is only for where the wall's foot stands.
            Transform ct = cam.transform;
            Vector3 flat = new Vector3(ct.forward.x, 0f, ct.forward.z);
            flat = flat.sqrMagnitude < 1e-4f ? Vector3.forward : flat.normalized;
            Vector3 at = ct.position + flat * 6f;
            at.y = ct.position.y - 1.7f;   // the wall's foot, a person's height below the lens
            FlareMethod.Invoke(null, new object[] { at, -flat, 1.8f, 0.85f });
            float born = Time.time;
            Report.Info($"{id}: flare placed at {World.P(at)}, camera '{cam.name}' at {World.P(ct.position)} looking {World.P(ct.forward)}, Drawn {drawn0} -> {Features.FlareDrawn}");

            var flash = new Pair(); var ripple = new Pair(); var gone = new Pair();
            // The frame rate is llvmpipe's, so the shots are taken by the flare's own clock (Time.time), not by frame counts.
            yield return Snap(cam, born + 0.12f, Path.Combine(dir, tag + "-1-flash.png"), flash);
            yield return Snap(cam, born + 0.45f, Path.Combine(dir, tag + "-2-ripple.png"), ripple);
            yield return Snap(cam, born + 1.9f, Path.Combine(dir, tag + "-3-gone.png"), gone);

            if (flash.With == null || ripple.With == null || gone.With == null)
            {
                Report.Fail(id, "the flare puts blue pixels on a real frame", "a frame could not be rendered (see the log)");
                yield break;
            }

            // Each shot was rendered twice in the same instant, with the flare's objects on and off, so the difference is the
            // flare and nothing else: a live world (a walking player, swaying grass) cannot leak into the numbers.
            int total = W * H;
            Diff(flash.Without, flash.With, out int litF, out int pinkF, out int peakF);
            Diff(ripple.Without, ripple.With, out int litR, out int pinkR, out int peakR);
            Diff(gone.Without, gone.With, out int litG, out int pinkG, out int peakG);
            int lit = Math.Max(litF, litR);
            int pink = Math.Max(pinkF, Math.Max(pinkR, pinkG));
            int drawn1 = Features.FlareDrawn;

            var why = new List<string>();
            if (drawn1 <= drawn0) why.Add($"BarrierFlare.Drawn did not rise ({drawn0} -> {drawn1}), so the flare never reached the screen path");
            if (lit < total / 100) why.Add($"only {lit} pixels ({100.0 * lit / total:0.00} %) of {total} got at least {LitStep} brighter in blue (and {BlueLead} more in blue than in red) while the flare was up (wanted 1 %): nothing, or almost nothing, was drawn");
            if (pink > total / 500) why.Add($"{pink} pixels are magenta-pink, the look of a shader the game could not compile");
            if (litG > total / 500) why.Add($"{litG} pixels are still lit 1.9 s after the push (peak {lit}): the flare did not fade away");
            Report.Check(id,
                $"a flare 6 m in front of a camera ({where}) lights {litF}/{litR} px (flash/ripple, of {total}; peak blue +{Math.Max(peakF, peakR)}) against the same instant without it, no pink ({pink} px), and {litG} px 1.9 s later; frames in {dir}",
                why.Count == 0, string.Join("; ", why));
        }

        private const int LitStep = 40, BlueLead = 25;

        /// <summary>
        /// 0.3.0 threw while building its texture and switched the flare off for the whole session. This proves the fix
        /// in a real process: the next build is made to fail (BarrierFlare.InjectBuildFaults), so the first flare must
        /// draw nothing, and the very next one must rebuild and draw. The ERROR line itself is read from the BepInEx log
        /// by whoever runs --spike a (it is not H08's business: H08 must stay red on any such line in a real run).
        /// </summary>
        internal static IEnumerator RetryProof(string id)
        {
            FieldInfo inject = typeof(Territory).Assembly.GetType("TerroritySmellatory.BarrierFlare")
                ?.GetField("InjectBuildFaults", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (inject == null || FlareMethod == null) yield break;   // an older DLL: nothing to prove
            var at = new Vector3(9000f, 9000f, 9006f);
            int d0 = Features.FlareDrawn;
            inject.SetValue(null, 1);
            FlareMethod.Invoke(null, new object[] { at, Vector3.back, 1.8f, 0.5f });
            int d1 = Features.FlareDrawn;
            yield return Flow.Seconds(0.5f);
            FlareMethod.Invoke(null, new object[] { at, Vector3.back, 1.8f, 0.5f });
            int d2 = Features.FlareDrawn;
            Report.Check(id, $"with the first asset build made to fail, that flare draws nothing (Drawn {d0} -> {d1}) and the next one rebuilds and draws (Drawn {d1} -> {d2}); faults left {inject.GetValue(null)}",
                d1 == d0 && d2 == d1 + 1, $"Drawn went {d0} -> {d1} -> {d2}");
        }

        private sealed class Pair { public Color32[] With, Without; }

        private static IEnumerator Snap(Camera cam, float atTime, string path, Pair into)
        {
            float guard = Time.realtimeSinceStartup + 30f;
            while (Time.time < atTime && Time.realtimeSinceStartup < guard) yield return null;
            var roots = new List<GameObject>(2);
            foreach (string name in new[] { "TS_BarrierFlares", "TS_BarrierRunes" })
            {
                GameObject g = GameObject.Find(name);
                if (g != null) roots.Add(g);
            }
            try
            {
                into.With = Capture(cam, path);
                foreach (GameObject g in roots) g.SetActive(false);
                try { into.Without = Capture(cam, null); }
                finally { foreach (GameObject g in roots) g.SetActive(true); }
            }
            catch (Exception ex) { Report.Info("FlareShot: render failed: " + ex); into.With = null; }
        }

        private static Camera FindCamera()
        {
            Camera c = Camera.main;
            if (c != null && c.enabled && c.gameObject.activeInHierarchy) return c;
            Camera best = null;
            foreach (Camera k in Camera.allCameras)
                if (k != null && k.enabled && k.targetTexture == null && (best == null || k.depth > best.depth)) best = k;
            return best;
        }

        /// <summary>Renders <paramref name="cam"/> into a private texture (not the screen: nothing depends on a window) and saves it.</summary>
        private static Color32[] Capture(Camera cam, string path)
        {
            RenderTexture rt = RenderTexture.GetTemporary(W, H, 24, RenderTextureFormat.ARGB32);
            RenderTexture prevTarget = cam.targetTexture, prevActive = RenderTexture.active;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = prevTarget;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0, false);
                tex.Apply(false);
                Color32[] px = tex.GetPixels32();
                if (path != null) File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
                return px;
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(tex);
            }
        }

        /// <summary>
        /// The same instant with the flare's objects off (a) and on (b): pixels at least LitStep brighter in blue AND BlueLead more in blue than in red
        /// (the flare adds #3FA9FF, so its blue rises about four times as far as its red; a walking player, moving grass or a
        /// change of light in the game's own view moves all channels alike and is not counted), pixels that turned magenta,
        /// and the largest blue rise.
        /// </summary>
        private static void Diff(Color32[] a, Color32[] b, out int lit, out int pink, out int peak)
        {
            lit = 0; pink = 0; peak = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int db = b[i].b - a[i].b, dr = b[i].r - a[i].r;
                if (db >= LitStep && db - dr >= BlueLead) lit++;
                if (db > peak) peak = db;
                if (b[i].r > 200 && b[i].b > 200 && b[i].g < 100 && !(a[i].r > 200 && a[i].b > 200 && a[i].g < 100)) pink++;
            }
        }
    }
}
