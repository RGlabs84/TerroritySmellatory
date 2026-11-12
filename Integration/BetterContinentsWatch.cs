using BepInEx;
using BepInEx.Bootstrap;

namespace TerroritySmellatory
{
    /// <summary>
    /// Says at start-up whether Better Continents is loaded, and which version. "BC compatible" is a
    /// requirement, so a log should show at a glance which setup a report came from.
    /// </summary>
    /// <remarks>
    /// Nothing here depends on BC and nothing changes with it: the mod composes with it as it is (see
    /// libs-Tools/IMPLEMENTATIONS/TerroritySmellatory.md, section 13). The id is BC's own
    /// [BepInPlugin("BetterContinents", ...)].
    /// </remarks>
    internal static class BetterContinentsWatch
    {
        internal const string Guid = "BetterContinents";

        internal static void Report()
        {
            if (Chainloader.PluginInfos.TryGetValue(Guid, out PluginInfo info))
                Plugin.Log.LogInfo($"Better Continents {info.Metadata.Version} detected.");
            else
                Plugin.Log.LogInfo("Better Continents not loaded.");
        }
    }
}
