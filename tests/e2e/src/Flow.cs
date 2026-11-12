using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace TerrE2E
{
    internal sealed class Box<T>
    {
        public T Value;
        public Box() { }
        public Box(T v) { Value = v; }
    }

    /// <summary>Coroutine plumbing: nested steps, exceptions turned into FAIL lines, timeouts.</summary>
    internal static class Flow
    {
        /// <summary>
        /// Runs a coroutine tree (a step may yield another IEnumerator) and turns an exception anywhere in it
        /// into <paramref name="onError"/> instead of Unity's silent coroutine death.
        /// </summary>
        internal static IEnumerator Guard(string what, IEnumerator root, Action<Exception> onError)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                bool moved;
                object current = null;
                try
                {
                    moved = top.MoveNext();
                    if (moved) current = top.Current;
                }
                catch (Exception ex)
                {
                    onError?.Invoke(ex);
                    yield break;
                }
                if (!moved)
                {
                    stack.Pop();
                    continue;
                }
                if (current is IEnumerator nested)
                {
                    stack.Push(nested);
                    continue;
                }
                yield return current;
            }
        }

        /// <summary>A step: its exception becomes a FAIL line with the step's id, and the scenario goes on.</summary>
        internal static IEnumerator Step(string id, string what, IEnumerator body)
        {
            bool threw = false;
            yield return Guard(id, body, ex =>
            {
                threw = true;
                Report.Fail(id, what, "the step threw: " + ex);
            });
            if (threw) yield return null;
        }

        /// <summary>Waits until <paramref name="cond"/> holds (result true) or <paramref name="timeout"/> passes (false).</summary>
        internal static IEnumerator Until(Func<bool> cond, float timeout, Box<bool> result)
        {
            float start = Time.realtimeSinceStartup;
            while (true)
            {
                bool ok;
                try { ok = cond(); }
                catch { ok = false; }
                if (ok)
                {
                    result.Value = true;
                    yield break;
                }
                if (Time.realtimeSinceStartup - start > timeout)
                {
                    result.Value = false;
                    yield break;
                }
                yield return null;
            }
        }

        internal static IEnumerator Seconds(float s)
        {
            float end = Time.realtimeSinceStartup + s;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        internal static IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }
    }

    /// <summary>
    /// Coordination between the processes of one run, through files in the shared run directory. A barrier is
    /// a JSON file written atomically (tmp + rename); waiting is polling for it.
    /// </summary>
    internal static class RunDir
    {
        private static string Dir
        {
            get
            {
                string d = Path.Combine(E2EEnv.RunDir ?? ".", "barriers");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        internal static string FilePath(string name) => Path.Combine(Dir, name + ".json");

        internal static void Signal(string name, object payload = null)
        {
            string path = FilePath(name);
            string tmp = path + ".tmp";
            string json = JsonConvert.SerializeObject(payload ?? new { at = DateTime.UtcNow.ToString("o") }, Formatting.Indented);
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            Report.Info($"barrier '{name}' signalled");
        }

        internal static bool Has(string name) => File.Exists(FilePath(name));

        internal static JObject Read(string name)
        {
            try { return JObject.Parse(File.ReadAllText(FilePath(name), Encoding.UTF8)); }
            catch { return null; }
        }

        /// <summary>Waits for another process's barrier. Result: its JSON, or null on timeout.</summary>
        internal static IEnumerator Wait(string name, float timeout, Box<JObject> result)
        {
            var ok = new Box<bool>();
            yield return Flow.Until(() => Has(name), timeout, ok);
            result.Value = ok.Value ? Read(name) : null;
            if (!ok.Value) Report.Info($"barrier '{name}' did not arrive within {timeout:0} s");
        }

        internal static void WriteText(string name, string text) =>
            File.WriteAllText(Path.Combine(E2EEnv.RunDir, name), text, new UTF8Encoding(false));
    }
}
