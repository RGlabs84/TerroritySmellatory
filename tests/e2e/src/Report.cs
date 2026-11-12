using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Logging;

namespace TerrE2E
{
    /// <summary>
    /// The one output format of the rig. Each check is ONE line:
    ///   [E2E] PASS &lt;id&gt; &lt;what&gt;
    ///   [E2E] FAIL &lt;id&gt; &lt;what&gt;: &lt;why&gt;
    ///   [E2E] SOFT &lt;id&gt; &lt;what&gt;: &lt;why&gt;      (reported, never fails the run: headless cannot observe it)
    /// and every role ends with "[E2E] DONE role=&lt;r&gt; passed=&lt;n&gt; failed=&lt;m&gt; soft=&lt;k&gt;".
    /// The same lines go to the BepInEx log and to &lt;rundir&gt;/results-&lt;role&gt;.txt (run-e2e.sh reads the file).
    /// </summary>
    internal static class Report
    {
        private static ManualLogSource _log;
        private static string _role = "?";
        private static string _file;
        private static int _pass, _fail, _soft;
        private static bool _done;
        private static readonly HashSet<string> _ids = new HashSet<string>();
        private static readonly object _lock = new object();

        internal static int Failed => _fail;

        internal static void Init(ManualLogSource log, string role)
        {
            _log = log;
            _role = role;
            string dir = E2EEnv.RunDir;
            if (!string.IsNullOrEmpty(dir))
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    // One file per PROCESS: the rig's name for it (TERR_E2E_TAG), so two "join" clients never share one.
                    string tag = Environment.GetEnvironmentVariable("TERR_E2E_TAG");
                    _file = Path.Combine(dir, $"results-{(string.IsNullOrEmpty(tag) ? role : tag)}.txt");
                }
                catch (Exception ex)
                {
                    _log.LogError($"[E2E] INFO cannot use the run dir {dir}: {ex.Message}");
                }
            }
        }

        private static void Emit(string line, bool error = false)
        {
            lock (_lock)
            {
                if (error) _log?.LogError(line);
                else _log?.LogMessage(line);
                if (_file == null) return;
                try { File.AppendAllText(_file, line + "\n", new UTF8Encoding(false)); }
                catch (Exception ex) { _log?.LogError($"[E2E] INFO results file write failed: {ex.Message}"); }
            }
        }

        private static string Clean(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " | ");

        private static string Unique(string id)
        {
            // An id checked twice (a retried step, a solo run passing the same code twice) keeps both lines apart.
            if (_ids.Add(id)) return id;
            for (int i = 2; ; i++)
                if (_ids.Add($"{id}#{i}")) return $"{id}#{i}";
        }

        internal static void Pass(string id, string what)
        {
            _pass++;
            Emit($"[E2E] PASS {Unique(id)} {Clean(what)}");
        }

        internal static void Fail(string id, string what, string why)
        {
            _fail++;
            Emit($"[E2E] FAIL {Unique(id)} {Clean(what)}: {Clean(why)}", error: true);
        }

        internal static void Soft(string id, string what, string why)
        {
            _soft++;
            Emit($"[E2E] SOFT {Unique(id)} {Clean(what)}: {Clean(why)}");
        }

        internal static bool Check(string id, string what, bool ok, string why)
        {
            if (ok) Pass(id, what);
            else Fail(id, what, why);
            return ok;
        }

        internal static void Info(string text) => Emit($"[E2E] INFO {Clean(text)}");

        internal static void Done()
        {
            if (_done) return;
            _done = true;
            Emit($"[E2E] DONE role={_role} passed={_pass} failed={_fail} soft={_soft}");
        }
    }
}
