using System;
using System.Linq;
using BepInEx.Bootstrap;

namespace QuestTree
{
    /// <summary>Same detection QuickSell uses: a Fika headless client has no player and no UI, so
    /// there is no trader dialog to add a tab to and nothing for this mod to do there.</summary>
    internal static class ModEnvironment
    {
        private static bool? _isHeadless;

        public static bool IsHeadlessClient
        {
            get
            {
                if (_isHeadless.HasValue) return _isHeadless.Value;

                _isHeadless = Chainloader.PluginInfos.Keys
                    .Any(guid => guid.IndexOf("headless", StringComparison.OrdinalIgnoreCase) >= 0);

                return _isHeadless.Value;
            }
        }
    }
}
