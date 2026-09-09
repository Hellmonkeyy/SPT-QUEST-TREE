using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// Whether a quest is required for the Kappa secure container is NOT present anywhere in
    /// QuestTemplate or any other client-loaded data - confirmed by decompiling the quest classes.
    /// It is curated community knowledge that changes when BSG reshuffles quest chains between
    /// wipes, so it can't be derived and shouldn't be hardcoded as if it were game data.
    ///
    /// Instead this reads BepInEx/plugins/QuestTree/kappa-quests.json, a plain JSON array of quest
    /// names (matched case-insensitively against QuestTemplate.Name). The file ships empty - fill
    /// it in from a current wiki/community Kappa quest list and the badge lights up with no code
    /// changes. Matching by name rather than id because names are what a player can verify against
    /// a wiki list; ids are opaque Mongo hex strings.
    /// </summary>
    internal static class KappaQuests
    {
        /// <summary>Collector's quest id, the same constant the server half keys its Kappa list
        /// on. Used only as the fallback when the Kappa payload is unavailable - without the
        /// server half the Collector badge still has something to resolve.</summary>
        public const string CollectorQuestId = "5c51aac186f77432ea65c552";

        private static HashSet<string> _names;

        public static bool IsKappaRequired(string questName)
        {
            if (string.IsNullOrEmpty(questName)) return false;
            return Names.Contains(questName);
        }

        private static HashSet<string> Names => _names ??= Load();

        /// <summary>How many quest names the curated list holds. Zero means the file is still the
        /// empty stub it ships as, which the Kappa tab reports rather than showing a silent 0/0.</summary>
        public static int Count => Names.Count;

        /// <summary>Drops the cached list so the next read re-reads the file. Lets the Settings tab
        /// pick up an edited kappa-quests.json without restarting the game.</summary>
        public static void Reload() => _names = null;

        private static HashSet<string> Load()
        {
            try
            {
                var modPath = Path.GetDirectoryName(typeof(KappaQuests).Assembly.Location);
                if (string.IsNullOrEmpty(modPath))
                {
                    // A plugin loaded from memory has no location; said once, since the missing
                    // file case below says something and this case said nothing.
                    Plugin.LogSource?.LogInfo("QuestTree: the plugin has no file location, so kappa-quests.json cannot be found.");
                    return Empty();
                }

                var path = Path.Combine(modPath, "kappa-quests.json");
                if (!File.Exists(path))
                {
                    Plugin.LogSource?.LogInfo(
                        "QuestTree: kappa-quests.json not found - Kappa badges will not show. " +
                        "Add quest names to BepInEx/plugins/QuestTree/kappa-quests.json to enable them.");
                    return Empty();
                }

                var names = JsonConvert.DeserializeObject<string[]>(File.ReadAllText(path));
                return new HashSet<string>(names ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not read kappa-quests.json: {e.Message}");
                return Empty();
            }
        }

        private static HashSet<string> Empty() => new(StringComparer.OrdinalIgnoreCase);
    }
}
