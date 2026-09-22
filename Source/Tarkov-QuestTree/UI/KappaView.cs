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
        /// <summary>Layout for the current build: the x every section draws at, and its width.
        /// Static so the section helpers keep their signatures; the page is built in one call.</summary>
        private static float _x;
        private static float _width;

        /// <summary>The row callback for the build in progress, and null between builds. It is a
        /// closure over the panel, which owns the graph - left set after Build returned, this
        /// static kept a destroyed panel and its thousands of nodes alive for the life of the
        /// process, one copy per menu visit.</summary>
        private static Action<QuestNode> _onQuestSelected;

        /// <summary>Which of the page's sections the tab row is showing, by key rather than by
        /// index: the Collector section is only offered on an install where it says something
        /// different from the Kappa list, and an index would select the wrong section on the
        /// installs where it is absent.
        ///
        /// Static because the aux panel is destroyed and rebuilt on every repaint - the same reason
        /// MapView keeps its map and floor here. A selection held in an instance would live for one
        /// frame.</summary>
        private static string _section = SectionItems;

        private const string SectionItems = "items";
        private const string SectionKappa = "kappa";
        private const string SectionCollector = "collector";

        /// <summary>The most rows any one section here will draw, with a "+N more" tail when there
        /// were more. Every list on this tab is unbounded in principle - Collector's item list, the
        /// prerequisite closure and the outstanding Kappa quests all grow with what mods add - and
        /// each row is a GameObject with a TextMeshPro on it. The same number ItemWatchlistView
        /// uses; MapView's quest list and DoNextView are capped for the same reason.</summary>
        private const int MaxRows = 150;

        /// <summary>Builds the whole tab into <paramref name="parent"/> and returns its height.
        /// Reads the cached fetch result rather than requesting - see QuestDataClient.GetKappa for
        /// why this must not hit the server on every render.</summary>
        public static float Build(
            RectTransform parent, QuestGraphBuilder graph, Vector2 panelSize, Action<QuestNode> onQuestSelected,
            Action onRefresh, Action onRepaint)
        {
            _x = AuxLayout.Padding;
            _width = Mathf.Min(AuxLayout.MaxContentWidth, panelSize.x - AuxLayout.Padding * 2f);
            _onQuestSelected = onQuestSelected;

            try
            {
                return BuildPage(parent, graph, onRefresh, onRepaint);
            }
            finally
            {
                _onQuestSelected = null;
            }
        }

        private static float BuildPage(
            RectTransform parent, QuestGraphBuilder graph, Action onRefresh, Action onRepaint)
        {
            var y = AuxLayout.Padding;

            var result = QuestDataClient.GetKappa();
            if (!result.IsOk)
            {
                BuildUnavailableSection(parent, ref y, result);
                DoNextView.RefreshLink(parent, AuxLayout.Padding + 32f, _x, _width, onRefresh);
                return y + AuxLayout.Padding;
            }

            var payload = result.Payload;

            // The three sections used to be stacked, which put a hundred and thirty-six quest rows
            // between the item checklist and anything below it. Only the sections this install has
            // something to say in get a tab: on an unmodified profile the live Collector
            // requirement and the canonical Kappa list are the same set, and printing both is noise.
            var sections = new List<(string Key, string Label)>
            {
                (SectionItems, "Collector items"),
                (SectionKappa, "Kappa quests")
            };

            if (LiveRequirementDiffers(payload)) sections.Add((SectionCollector, "To unlock Collector"));

            var selected = sections.FindIndex(section => section.Key == _section);
            if (selected < 0)
            {
                // The remembered section is not on offer here - a mod stopped changing Collector,
                // or this is the first build of the session.
                selected = 0;
                _section = sections[0].Key;
            }

            var tabTop = y;

            // Short of the refresh link, which sits on this same line at the right-hand end.
            AuxLayout.AddTabRow(
                parent, ref y, _x, Mathf.Max(200f, _width - RefreshLinkWidth),
                sections.Select(section => section.Label).ToList(), selected,
                index =>
                {
                    _section = sections[index].Key;
                    onRepaint?.Invoke();
                });

            // The checklist reflects your stash, and the mod deliberately does not poll for that -
            // so there is an explicit way to re-read it after a raid without reopening the panel.
            // On every tab, because every section here is read from the same fetch.
            DoNextView.RefreshLink(parent, tabTop + 33f, _x, _width, onRefresh);

            switch (_section)
            {
                case SectionKappa:
                    BuildKappaQuestSection(parent, ref y, graph, payload);
                    break;

                case SectionCollector:
                    BuildCollectorUnlockSection(parent, ref y, graph, payload);
                    break;

                default:
                    BuildItemSection(parent, ref y, payload);
                    break;
            }

            return y + AuxLayout.Padding;
        }

        /// <summary>Width kept clear for the refresh link on the tab row's line. DoNextView.
        /// RefreshLink right-aligns itself in 150px; the rest is a gap so the last tab does not
        /// touch it.</summary>
        private const float RefreshLinkWidth = 165f;

        /// <summary>
        /// Explains a failed fetch in terms of what the player has to DO about it. The old message
        /// was "the server did not answer", which is true but unactionable - and the case that
        /// actually happened in the wild (a Fika client talking to a server without the server half)
        /// looks identical to every other failure unless it is named.
        /// </summary>
        private static void BuildUnavailableSection(RectTransform parent, ref float y, KappaFetchResult result)
        {
            Section(parent, ref y, "Kappa progress unavailable");
            AuxLayout.AddText(parent, ref y,
                $"<color=#{GameStyle.ErrorHex}>{Explain(result)}</color>", 72f, 12);
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
                Section(parent, ref y, "Collector items");
                AuxLayout.AddText(parent, ref y,
                    $"<color=#{GameStyle.ErrorHex}>The server half of the mod did not answer.</color> " +
                    "The item checklist is read from your profile by QuestTreeServer " +
                    "(SPT_Runtime/user/mods/QuestTree).", 40f);
                return;
            }

            if (!payload.CollectorFound || payload.Items == null || payload.Items.Count == 0)
            {
                Section(parent, ref y, "Collector items");
                AuxLayout.AddText(parent, ref y,
                    "No Collector quest found in this install, so there is no item list to track.", 40f);
                return;
            }

            var items = payload.Items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var done = items.Count(i => i.IsSatisfied);

            Section(parent, ref y, $"Collector items      {done} / {items.Count}");

            var status = payload.CollectorStatus;
            if (!string.Equals(status, "Started", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase))
            {
                AuxLayout.AddText(parent, ref y,
                    "<color=#FFFFFF80>Collector is not accepted yet, so nothing counts as handed in - " +
                    "items below are what is sitting in your stash.</color>", 20f, 11);
            }

            foreach (var item in items.Take(MaxRows))
                ItemRow(parent, ref y, item.Template, FormatItem(item));

            MoreRow(parent, ref y, items.Count);
        }

        /// <summary>A checklist line that opens the game's own inspect window on the item. The rows
        /// were plain text, which is a strange thing for a list of items to be in a game where every
        /// other list of items can be inspected.</summary>
        private static void ItemRow(RectTransform parent, ref float y, string template, string text)
        {
            var captured = template;
            AuxLayout.AddClickableRow(parent, text, _x + 6f, ref y, _width - 6f, false,
                () => GameStyle.InspectItem(captured));
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

            // The shared "completed" green, as the quest list below it uses - two greens one above
            // the other read as two different states.
            var done = QuestNodeView.HexFor(ENodeStatus.Completed);

            if (item.HandedIn)
                return $"<color=#{done}>[done]</color>  {name}{need}  <color=#FFFFFF60>handed in</color>";

            if (item.OwnedFoundInRaid >= item.Required)
                return $"<color=#{done}>[ready]</color>  {name}{need}  " +
                       $"<color=#FFFFFF60>{item.OwnedFoundInRaid} found in raid</color>";

            if (item.OwnedTotal > 0)
                return $"<color=#{GameStyle.WarningHex}>[not FiR]</color>  {name}{need}  " +
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
            // The same fallback the badge uses, so a payload without an id cannot leave the boxes
            // wearing C while this section says Collector is unknown.
            var collectorId = string.IsNullOrEmpty(payload?.CollectorQuestId)
                ? KappaQuests.CollectorQuestId
                : payload.CollectorQuestId;

            if (!graph.NodesById.TryGetValue(collectorId, out var collector))
            {
                Section(parent, ref y, "To unlock Collector");
                AuxLayout.AddText(parent, ref y, "Collector is not in the loaded quest set.", 20f, 12);
                return;
            }

            var required = QuestRoute.Prerequisites(collector, graph);
            var complete = required.Count(n => n.Status == ENodeStatus.Completed);

            Section(parent, ref y, $"To unlock Collector      {complete} / {required.Count}");
            AuxLayout.AddText(parent, ref y,
                "<color=#FFFFFF80>What Collector actually requires on this install right now, after " +
                "mods. Shown because it differs from the canonical Kappa list above.</color>", 32f, 11);

            var ordered = required
                .OrderBy(n => n.TraderName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var node in ordered.Take(MaxRows))
            {
                // Taken from the shared status palette rather than a hardcoded green: this is a
                // quest's completion state, so it has to say "completed" in the same colour the
                // tree, the legend and every other list say it in.
                var doneHex = ColorUtility.ToHtmlStringRGB(
                    QuestNodeView.ColorFor(ENodeStatus.Completed));

                var mark = node.Status == ENodeStatus.Completed
                    ? $"<color=#{doneHex}>[done]</color>"
                    : "<color=#FFFFFF40>[     ]</color>";

                QuestRow(parent, ref y, node, $"{mark}  {GameStyle.Safe(node.Name)}  <color=#FFFFFF60>{GameStyle.Safe(node.TraderName)}</color>");
            }

            MoreRow(parent, ref y, ordered.Count);
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
                Section(parent, ref y, "Kappa quests");
                AuxLayout.AddText(parent, ref y,
                    $"<color=#{GameStyle.ErrorHex}>The server half of the mod did not supply the Kappa quest list.</color> " +
                    "It is read from Collector's start conditions by QuestTreeServer.", 40f, 12);
                return;
            }

            var nodes = ids
                .Select(id => graph.NodesById.TryGetValue(id, out var node) ? node : null)
                .ToList();

            var complete = nodes.Count(n => n != null && n.Status == ENodeStatus.Completed);

            Section(parent, ref y, $"Kappa quests      {complete} / {ids.Count}");

            // The whole reason for reading the database rather than the live table: if the two
            // disagree, a mod has changed what Kappa costs on this install, and the player should
            // know rather than quietly tracking the wrong thing.
            if (LiveRequirementDiffers(payload))
            {
                AuxLayout.AddText(parent, ref y,
                    $"<color=#{GameStyle.WarningHex}>A mod has changed Collector on this install: it currently requires " +
                    $"{payload.LiveCollectorPrerequisiteCount} prerequisite quest(s), not {ids.Count}. " +
                    "The canonical list is shown below.</color>", 44f, 11);
            }

            for (var index = 0; index < ids.Count; index++)
            {
                if (nodes[index] != null) continue;

                // Listed by id rather than dropped silently - a Kappa quest missing from the
                // loaded set is itself worth seeing.
                // Safe: this is the only place in the mod that prints a raw server-supplied ID, and
                // the Kappa payload's id list is not sanitised at ingest the way its item names are.
                // A quest mod may use any string it likes as an id, and one with a "<" in it would
                // be markup by the time it reached this label.
                AuxLayout.AddText(parent, ref y,
                    $"<color=#FFFFFF40>[     ]</color>  <color=#{GameStyle.ErrorHex}>{GameStyle.Safe(ids[index])} (not in the loaded quest set)</color>",
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

            foreach (var node in outstanding.Take(MaxRows))
            {
                // The status glyph replaces a uniform empty checkbox, which said only "not done" -
                // true of every row and so worth nothing. This distinguishes what is in progress
                // and startable from what is still gated.
                var hex = ColorUtility.ToHtmlStringRGB(QuestNodeView.ColorFor(node.Status));

                QuestRow(parent, ref y, node,
                    $"<color=#{hex}>{QuestNodeView.GlyphFor(node.Status)}</color>  {GameStyle.Safe(node.Name)}" +
                    $"  <color=#FFFFFF60>{GameStyle.Safe(node.TraderName)}</color>");
            }

            MoreRow(parent, ref y, outstanding.Count);
        }

        /// <summary>The manual kappa-quests.json path, used only when that file has been filled in.</summary>
        private static void BuildOverrideSection(RectTransform parent, ref float y, QuestGraphBuilder graph)
        {
            var kappaNodes = graph.Nodes.Where(n => n.IsKappaRequired).ToList();
            var complete = kappaNodes.Count(n => n.Status == ENodeStatus.Completed);

            Section(parent, ref y, $"Kappa quests      {complete} / {kappaNodes.Count}");
            AuxLayout.AddText(parent, ref y,
                $"<color=#FFFFFF80>Using your own list from kappa-quests.json ({KappaQuests.Count} names). " +
                "Empty that file to go back to the list derived from Collector.</color>", 32f, 11);

            // Capped like every other list on this tab. This one was left out when the caps went in,
            // and it is the least bounded of them: the file it reads is the player's own, so its
            // length is whatever they typed, and each row here is a GameObject with a TMP label on it.
            var outstanding = kappaNodes
                .Where(n => n.Status != ENodeStatus.Completed)
                .OrderBy(n => n.TraderName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var node in outstanding.Take(MaxRows))
            {
                QuestRow(parent, ref y, node,
                    $"<color=#FFFFFF40>[     ]</color>  {GameStyle.Safe(node.Name)}  <color=#FFFFFF60>{GameStyle.Safe(node.TraderName)}</color>");
            }

            MoreRow(parent, ref y, outstanding.Count);
        }
        // ------------------------------------------------------------------ rows

        private static void Section(RectTransform parent, ref float y, string title) =>
            AuxLayout.AddSectionHeader(parent, ref y, title, _x, _width);

        /// <summary>A quest line that opens the quest - its detail, and the tree framed on it.</summary>
        private static void QuestRow(RectTransform parent, ref float y, QuestNode node, string text)
        {
            // Captured per row: the static is cleared when the build returns.
            var captured = node;
            var onSelected = _onQuestSelected;
            AuxLayout.AddClickableRow(parent, text, _x, ref y, _width, false,
                () => onSelected?.Invoke(captured));
        }

        /// <summary>The tail under a capped list, drawn only when the list was longer than the cap.
        /// Takes the full count rather than the remainder so the caller cannot get the subtraction
        /// wrong, and reads the same as the tree map's tail.</summary>
        private static void MoreRow(RectTransform parent, ref float y, int total)
        {
            if (total <= MaxRows) return;

            AuxLayout.AddText(parent, ref y,
                $"<color=#FFFFFF60>+{total - MaxRows} more</color>", AuxLayout.RowHeight, 11, indent: 6f);
        }
    }
}
