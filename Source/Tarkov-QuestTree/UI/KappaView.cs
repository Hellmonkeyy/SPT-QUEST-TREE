using System;
using System.Collections.Generic;
using System.Linq;
using QuestTree.QuestGraph;
using TMPro;
using UnityEngine;

namespace QuestTree.UI
{
    /// <summary>
    /// The Kappa tab: how close this profile is to the Kappa secure container.
    ///
    /// Everything here is derived from real data rather than a hardcoded or community list:
    ///
    /// 1. The Collector hand-in checklist, from the quest's own conditions plus the profile stash.
    /// 2. The Kappa quest list, from Collector's AvailableForStart conditions in SPT's shipped
    ///    database. An earlier version of this file claimed the Kappa requirement "is not in game
    ///    data" - that was simply wrong; it is 136 quests across the 7 main traders, and the
    ///    companion server mod reads it straight out of quests.json.
    /// 3. The quests gating Collector on THIS install, shown only when a mod has changed them so
    ///    that they differ from the canonical list above.
    /// </summary>
    internal static class KappaView
    {
        /// <summary>Builds the whole tab into <paramref name="parent"/> and returns its height.
        /// Reads the cached fetch result rather than requesting - see QuestDataClient.GetKappa for
        /// why this must not hit the server on every render.</summary>
        public static float Build(RectTransform parent, QuestGraphBuilder graph, Action onRefresh)
        {
            var y = AuxLayout.Padding;

            // The checklist reflects your stash, and the mod deliberately does not poll for that -
            // so there is an explicit way to re-read it after a raid without reopening the panel.
            AuxLayout.AddButton(parent, ref y, "Refresh from server", onRefresh);
            AuxLayout.AddSpacer(ref y, 8f);

            var result = QuestDataClient.GetKappa();

            if (!result.IsOk)
            {
                BuildUnavailableSection(parent, ref y, result);
                return y + AuxLayout.Padding;
            }

            var payload = result.Payload;

            BuildItemSection(parent, ref y, payload);
            AuxLayout.AddSpacer(ref y, 18f);

            BuildKappaQuestSection(parent, ref y, graph, payload);
            AuxLayout.AddSpacer(ref y, 18f);

            // Only worth showing when it differs from the Kappa list above - on an unmodified
            // install the two are the same thing and printing both would just be noise.
            if (LiveRequirementDiffers(payload))
            {
                BuildCollectorUnlockSection(parent, ref y, graph, payload);
                AuxLayout.AddSpacer(ref y, 18f);
            }

            return y + AuxLayout.Padding;
        }

        /// <summary>
        /// Explains a failed fetch in terms of what the player has to DO about it. The old message
        /// was "the server did not answer", which is true but unactionable - and the case that
        /// actually happened in the wild (a Fika client talking to a server without the server half)
        /// looks identical to every other failure unless it is named.
        /// </summary>
        private static void BuildUnavailableSection(RectTransform parent, ref float y, KappaFetchResult result)
        {
            AuxLayout.AddHeading(parent, ref y, "Kappa progress unavailable");
            AuxLayout.AddText(parent, ref y,
                $"<color=#C86464>{Explain(result)}</color>", 72f, 12);
            AuxLayout.AddSpacer(ref y, 8f);
            AuxLayout.AddText(parent, ref y,
                "<color=#FFFFFF80>The quest tree itself still works - only the Kappa checklist and " +
                "quest list need the server half.</color>", 32f, 11);
        }

        /// <summary>The sentence for one failure. Each names the fix rather than the symptom, which
        /// is the whole point of keeping the three statuses apart in the first place.</summary>
        private static string Explain(KappaFetchResult result) => result.Status switch
        {
            EKappaFetchStatus.ServerHalfMissing =>
                "The server half of this mod is missing, or is older than the client half. " +
                "Copy <b>SPT_Runtime\\user\\mods\\QuestTree</b> from the same download as your " +
                "BepInEx plugin, then restart the server.",

            EKappaFetchStatus.VersionMismatch =>
                $"Both halves are installed but from different versions - server {result.ServerVersion}, " +
                $"client {ModInfo.Version}. Reinstall both from the same download.",

            EKappaFetchStatus.Unreachable =>
                "Could not reach the SPT server. If you are playing on someone else's Fika server, " +
                "they need the server half installed too.",

            _ => ""
        };

        private static bool LiveRequirementDiffers(KappaPayloadDto payload) =>
            payload?.KappaQuestIds != null &&
            payload.LiveCollectorPrerequisiteCount != payload.KappaQuestIds.Count;

        // ------------------------------------------------------------------ items

        private static void BuildItemSection(RectTransform parent, ref float y, KappaPayloadDto payload)
        {
            if (payload == null)
            {
                AuxLayout.AddHeading(parent, ref y, "Collector items");
                AuxLayout.AddText(parent, ref y,
                    "<color=#C86464>The server half of the mod did not answer.</color> " +
                    "The item checklist is read from your profile by QuestTreeServer " +
                    "(SPT_Runtime/user/mods/QuestTree).", 40f);
                return;
            }

            if (!payload.CollectorFound || payload.Items == null || payload.Items.Count == 0)
            {
                AuxLayout.AddHeading(parent, ref y, "Collector items");
                AuxLayout.AddText(parent, ref y,
                    "No Collector quest found in this install, so there is no item list to track.", 40f);
                return;
            }

            var items = payload.Items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var done = items.Count(i => i.IsSatisfied);

            AuxLayout.AddHeading(parent, ref y, $"Collector items      {done} / {items.Count}");

            var status = payload.CollectorStatus;
            if (!string.Equals(status, "Started", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase))
            {
                AuxLayout.AddText(parent, ref y,
                    "<color=#FFFFFF80>Collector is not accepted yet, so nothing counts as handed in - " +
                    "items below are what is sitting in your stash.</color>", 20f, 11);
            }

            foreach (var item in items)
                AuxLayout.AddText(parent, ref y, FormatItem(item), AuxLayout.RowHeight, 12, indent: 6f);
        }

        /// <summary>
        /// One checklist line. "In stash but not found-in-raid" is called out as its own state
        /// rather than counted as progress: Collector only accepts found-in-raid items, so treating
        /// a non-FiR copy as done would tell the player they are finished when they are not.
        /// </summary>
        private static string FormatItem(KappaItemDto item)
        {
            var name = string.IsNullOrEmpty(item.Name) ? item.Template : item.Name;
            var need = item.Required > 1 ? $" x{item.Required}" : "";

            if (item.HandedIn)
                return $"<color=#6FBF6F>[done]</color>  {name}{need}  <color=#FFFFFF60>handed in</color>";

            if (item.OwnedFoundInRaid >= item.Required)
                return $"<color=#6FBF6F>[ready]</color>  {name}{need}  " +
                       $"<color=#FFFFFF60>{item.OwnedFoundInRaid} found in raid</color>";

            if (item.OwnedTotal > 0)
                return $"<color=#D9A61A>[not FiR]</color>  {name}{need}  " +
                       $"<color=#FFFFFF60>{item.OwnedTotal} held, none found in raid</color>";

            return $"<color=#FFFFFF40>[     ]</color>  <color=#FFFFFFB0>{name}{need}</color>";
        }

        // ------------------------------------------------------------------ unlock chain

        /// <summary>
        /// The transitive prerequisite closure of Collector - what you must finish before you can
        /// even accept it. Computed here from the graph rather than asked of the server, so it
        /// reflects live quest status with no extra round trip.
        /// </summary>
        private static void BuildCollectorUnlockSection(
            RectTransform parent, ref float y, QuestGraphBuilder graph, KappaPayloadDto payload)
        {
            var collectorId = payload?.CollectorQuestId;

            if (string.IsNullOrEmpty(collectorId) || !graph.NodesById.TryGetValue(collectorId, out var collector))
            {
                AuxLayout.AddHeading(parent, ref y, "To unlock Collector");
                AuxLayout.AddText(parent, ref y, "Collector is not in the loaded quest set.", 20f, 12);
                return;
            }

            var required = QuestRoute.Prerequisites(collector, graph);
            var complete = required.Count(n => n.Status == ENodeStatus.Completed);

            AuxLayout.AddHeading(parent, ref y, $"To unlock Collector      {complete} / {required.Count}");
            AuxLayout.AddText(parent, ref y,
                "<color=#FFFFFF80>What Collector actually requires on this install right now, after " +
                "mods. Shown because it differs from the canonical Kappa list above.</color>", 32f, 11);

            foreach (var node in required
                         .OrderBy(n => n.TraderName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            {
                // Taken from the shared status palette rather than a hardcoded green: this is a
                // quest's completion state, so it has to say "completed" in the same colour the
                // tree, the legend and every other list say it in.
                var doneHex = ColorUtility.ToHtmlStringRGB(
                    QuestNodeView.ColorFor(ENodeStatus.Completed));

                var mark = node.Status == ENodeStatus.Completed
                    ? $"<color=#{doneHex}>[done]</color>"
                    : "<color=#FFFFFF40>[     ]</color>";

                AuxLayout.AddText(parent, ref y,
                    $"{mark}  {node.Name}  <color=#FFFFFF60>{node.TraderName}</color>",
                    AuxLayout.RowHeight, 12, indent: 6f);
            }
        }

        // ------------------------------------------------------------------ curated list

        /// <summary>
        /// The Kappa quest list proper: every quest Collector requires, served by the companion mod
        /// from Collector's own start conditions in SPT's shipped database.
        ///
        /// The database is read rather than the live quest table on purpose - a quest mod can
        /// rewrite Collector in memory, and on the install this was built against it does exactly
        /// that, cutting 136 prerequisites down to 4. Completion status still comes from the live
        /// graph, so it stays accurate either way.
        ///
        /// kappa-quests.json remains a manual override for anyone tracking a different list; it
        /// ships empty, so the derived list is what you get by default.
        /// </summary>
        private static void BuildKappaQuestSection(
            RectTransform parent, ref float y, QuestGraphBuilder graph, KappaPayloadDto payload)
        {
            if (KappaQuests.Count > 0)
            {
                BuildOverrideSection(parent, ref y, graph);
                return;
            }

            var ids = payload?.KappaQuestIds;
            if (ids == null || ids.Count == 0)
            {
                AuxLayout.AddHeading(parent, ref y, "Kappa quests");
                AuxLayout.AddText(parent, ref y,
                    "<color=#C86464>The server half of the mod did not supply the Kappa quest list.</color> " +
                    "It is read from Collector's start conditions by QuestTreeServer.", 40f, 12);
                return;
            }

            var nodes = ids
                .Select(id => graph.NodesById.TryGetValue(id, out var node) ? node : null)
                .ToList();

            var complete = nodes.Count(n => n != null && n.Status == ENodeStatus.Completed);

            AuxLayout.AddHeading(parent, ref y, $"Kappa quests      {complete} / {ids.Count}");

            // The whole reason for reading the database rather than the live table: if the two
            // disagree, a mod has changed what Kappa costs on this install, and the player should
            // know rather than quietly tracking the wrong thing.
            if (LiveRequirementDiffers(payload))
            {
                AuxLayout.AddText(parent, ref y,
                    $"<color=#D9A61A>A mod has changed Collector on this install: it currently requires " +
                    $"{payload.LiveCollectorPrerequisiteCount} prerequisite quest(s), not {ids.Count}. " +
                    "The canonical list is shown below.</color>", 44f, 11);
            }

            for (var index = 0; index < ids.Count; index++)
            {
                if (nodes[index] != null) continue;

                // Listed by id rather than dropped silently - a Kappa quest missing from the
                // loaded set is itself worth seeing.
                AuxLayout.AddText(parent, ref y,
                    $"<color=#FFFFFF40>[     ]</color>  <color=#C86464>{ids[index]} (not in the loaded quest set)</color>",
                    AuxLayout.RowHeight, 12, indent: 6f);
            }

            // Ordered by depth rather than by the order Collector happens to list its conditions,
            // which is arbitrary. Depth is one past a quest's deepest prerequisite, so this reads
            // top-to-bottom as the order the remaining quests can actually be done in - the
            // difference between a checklist and a plan. Same rule as QuestRoute.Remaining.
            var outstanding = nodes
                .Where(node => node != null && node.Status != ENodeStatus.Completed)
                .OrderBy(node => node.Depth)
                .ThenBy(node => node.TraderName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var startable = outstanding.Count(
                node => node.Status == ENodeStatus.Active || node.Status == ENodeStatus.Available);

            if (outstanding.Count > 0)
            {
                AuxLayout.AddText(parent, ref y,
                    startable > 0
                        ? $"<color=#FFFFFF80>{startable} of the {outstanding.Count} left can be worked on now, " +
                          "listed first.</color>"
                        : $"<color=#FFFFFF80>{outstanding.Count} left, in the order they unlock.</color>",
                    26f, 11);
            }

            foreach (var node in outstanding)
            {
                // The status glyph replaces a uniform empty checkbox, which said only "not done" -
                // true of every row and so worth nothing. This distinguishes what is in progress
                // and startable from what is still gated.
                var hex = ColorUtility.ToHtmlStringRGB(QuestNodeView.ColorFor(node.Status));

                AuxLayout.AddText(parent, ref y,
                    $"<color=#{hex}>{QuestNodeView.GlyphFor(node.Status)}</color>  {node.Name}" +
                    $"  <color=#FFFFFF60>{node.TraderName}</color>",
                    AuxLayout.RowHeight, 12, indent: 6f);
            }
        }

        /// <summary>The manual kappa-quests.json path, used only when that file has been filled in.</summary>
        private static void BuildOverrideSection(RectTransform parent, ref float y, QuestGraphBuilder graph)
        {
            var kappaNodes = graph.Nodes.Where(n => n.IsKappaRequired).ToList();
            var complete = kappaNodes.Count(n => n.Status == ENodeStatus.Completed);

            AuxLayout.AddHeading(parent, ref y, $"Kappa quests      {complete} / {kappaNodes.Count}");
            AuxLayout.AddText(parent, ref y,
                $"<color=#FFFFFF80>Using your own list from kappa-quests.json ({KappaQuests.Count} names). " +
                "Empty that file to go back to the list derived from Collector.</color>", 32f, 11);

            foreach (var node in kappaNodes
                         .Where(n => n.Status != ENodeStatus.Completed)
                         .OrderBy(n => n.TraderName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            {
                AuxLayout.AddText(parent, ref y,
                    $"<color=#FFFFFF40>[     ]</color>  {node.Name}  <color=#FFFFFF60>{node.TraderName}</color>",
                    AuxLayout.RowHeight, 12, indent: 6f);
            }
        }
    }
}
