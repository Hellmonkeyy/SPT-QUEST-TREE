using System;
using System.Collections.Generic;
using System.Linq;
using QuestTree.QuestGraph;
using UnityEngine;

namespace QuestTree.UI
{
    /// <summary>
    /// Everything worth saying about one quest, as rich-text lines.
    ///
    /// This was private to <see cref="QuestDetailPanel"/> until the Maps tab needed the same facts.
    /// It is a list of strings rather than a widget tree deliberately: the two callers lay their
    /// text out differently - the detail panel pours the whole list into a single TMP, the map list
    /// renders it row by row through <see cref="AuxLayout"/> - and only the CONTENT is shared. That
    /// is also why nothing here touches a RectTransform.
    /// </summary>
    internal static class QuestSummary
    {
        /// <summary>How many route steps a summary or the detail panel lists before "+N more". A
        /// route on a late Kappa quest can run to dozens - past this it stops being a plan you
        /// can read.</summary>
        internal const int RouteSteps = 12;

        /// <summary>The fallback found-in-raid test for a server that predates the objective's
        /// own flag: the English objective sentence. Shared by the Do next ranking and the Items
        /// watchlist, which each had a copy.</summary>
        internal static bool MentionsFoundInRaid(string objectiveText) =>
            !string.IsNullOrEmpty(objectiveText) &&
            objectiveText.IndexOf("found in raid", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// The quest's full story, top to bottom: identity, why it is blocked, what it requires, the
        /// route to it, objectives, rewards, and what it opens up.
        ///
        /// Nulls are left in the list rather than skipped, so a caller that joins the lines can drop
        /// them in one pass and a caller that renders them individually can too - see the
        /// <c>Where(l =&gt; l != null)</c> at both call sites.
        /// </summary>
        public static List<string> Lines(
            QuestNode node, QuestGraphBuilder graph, ProfilePayloadDto profile, bool includeHeader = true)
        {
            if (node == null) return new List<string>();

            // The header is for a surface that shows nothing else about the quest; a row that
            // was just clicked already says its name and trader.
            var lines = new List<string>
            {
                includeHeader ? $"<b>{GameStyle.Safe(node.Name)}</b>" : null,
                includeHeader ? node.TraderName : null,
                node.Level > 0 ? $"Level {node.Level}" : null,
                node.IsKappaRequired ? $"<color=#{GameStyle.WarningHex}>Kappa required</color>" : null,
                node.IsCollectorPrerequisite
                    ? $"<color=#{ColorUtility.ToHtmlStringRGB(GameStyle.CollectorBlue)}>Needed to unlock Collector</color>"
                    : null,
                // Faction- and edition-locked quests are shown rather than hidden, so this is what
                // stops one reading as a bug in the tree.
                node.UnobtainableReason != null ? $"<color=#{GameStyle.ErrorHex}>{node.UnobtainableReason}</color>" : null,
                // The single gate actually stopping you, computed server-side against your level,
                // loyalty and standing. Until this existed a locked quest was a grey box with no
                // explanation of what to go and do about it.
                FormatLockReason(node, graph, profile),
                ""
            };

            if (node.PrerequisiteIds.Count > 0)
            {
                // Named here rather than drawn as a line, since a prerequisite from another trader
                // won't have a node in whatever tab is currently rendered (QuestGraphView.Render
                // only draws edges within the current tab's node set) - this is the one place that
                // relationship still surfaces when it crosses tabs.
                lines.Add("<b>Requires</b>");
                foreach (var prereqId in node.PrerequisiteIds)
                {
                    var note = PrerequisiteNote(node, prereqId);
                    var suffix = note == null ? "" : $"  <color=#FFFFFF60>{note}</color>";
                    lines.Add(graph != null && graph.NodesById.TryGetValue(prereqId, out var prereq)
                        ? $"{GameStyle.Safe(prereq.Name)} ({GameStyle.Safe(prereq.TraderName)}){suffix}"
                        : prereqId + suffix);
                }
                lines.Add("");
            }

            AddRoute(lines, node, graph);
            AddItemsToBring(lines, node, profile);

            // Available for a locked quest too, not just an accepted one: the objective text
            // arrives with the companion mod's payload rather than being read off a live Quest
            // instance the game only creates once the quest is unlocked.
            var objectives = node.NecessaryObjectives.Select(o => FormatObjective(o, profile)).ToList();
            if (objectives.Count > 0)
            {
                lines.Add("<b>Objectives</b>");
                lines.AddRange(objectives);
                lines.Add("");
            }

            var rewards = node.Rewards
                .Select(r => FormatReward(r, graph))
                .Where(r => !string.IsNullOrEmpty(r))
                .ToList();

            if (rewards.Count > 0)
            {
                lines.Add("<b>Rewards</b>");
                lines.AddRange(rewards);
                lines.Add("");
            }

            if (node.Unlocks.Count > 0)
            {
                lines.Add("<b>Unlocks</b>");
                lines.AddRange(node.Unlocks.Select(
                    u => u.TraderId == node.TraderId ? u.Name : $"{u.Name} ({u.TraderName})"));
            }

            return lines;
        }

        /// <summary>
        /// What you have to be carrying, and how much of it you already hold.
        ///
        /// The objective sentences say this in prose - "Mark the first trading post with an MS2000
        /// Marker on Shoreline" - which is exactly the sort of thing a player reads on the map
        /// screen, walks into a raid, and discovers they left in the stash. Pulled out as its own
        /// short list above the objectives, with the held count beside each item, it is answerable
        /// at a glance.
        ///
        /// Grouped by item rather than listed per objective: two objectives wanting the same marker
        /// need two of it, not two lines about it.
        /// </summary>
        private static void AddItemsToBring(List<string> lines, QuestNode node, ProfilePayloadDto profile)
        {
            var objectives = node.NecessaryObjectives;
            if (objectives == null) return;

            var order = new List<string>();
            var wanted = new Dictionary<string,
                (List<string> Templates, string Name, int Need, bool FoundInRaid, string Verb)>();

            foreach (var objective in objectives)
            {
                if (objective?.TargetItems == null || objective.TargetItems.Count == 0) continue;

                // Keyed on every template the condition accepts, not on the first of them, and
                // held counts sum across all of them - the same rule the map sidebar's raid check
                // folds by. They render one above the other in the same column, and the old rule
                // (name the first, count only that one) made them disagree: a player carrying seven
                // grenades of five kinds would read "7 on you" in one section and "0" in the other,
                // because Confidential Info's first template is a V40 they own none of.
                var templates = objective.TargetItems.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                if (templates.Count == 0) continue;

                var key = string.Join("|", templates.ToArray());
                var need = Mathf.Max(1, objective.Count);

                if (wanted.TryGetValue(key, out var existing))
                {
                    wanted[key] = (existing.Templates, existing.Name, existing.Need + need,
                        existing.FoundInRaid || NeedsFoundInRaid(objective), existing.Verb);
                    continue;
                }

                order.Add(key);
                wanted[key] = (templates, ItemName(objective, 0), need, NeedsFoundInRaid(objective), Verb(objective));
            }

            if (order.Count == 0) return;

            lines.Add("<b>Bring</b>");

            // Whether the server can say where things are. Below schema v2, or with the roots
            // unread, on-person counts are UNKNOWN rather than zero - and zero reads exactly like
            // "carrying nothing" on a full rig.
            var placesKnown = profile != null &&
                              profile.SchemaVersion >= ProfilePayloadDto.SupportedSchemaVersion &&
                              profile.InventoryLocationsKnown;

            foreach (var key in order)
            {
                var item = wanted[key];
                var held = HeldCount(profile, item.Templates, item.FoundInRaid, placesKnown);

                var enough = held.OnYou >= item.Need;
                var colour = enough ? "#" + QuestNodeView.HexFor(ENodeStatus.Completed) : "#" + GameStyle.WarningHex;
                var fir = item.FoundInRaid ? " found in raid" : "";
                var count = item.Need > 1 ? $" x{item.Need}" : "";

                // "Held" was the stash-inclusive total, which told you that you hold a marker
                // sitting at home - the same falsehood the pre-raid cue exists to prevent, one
                // screen further in. It counts what is ON YOU now, and says where the rest is.
                var where =
                    !placesKnown ? "held" :
                    enough ? "on you" :
                    held.Elsewhere > 0 && held.InStash == 0 ? $"on you, {held.Elsewhere} elsewhere" :
                    held.InStash > 0 ? $"on you, {held.InStash} in stash" :
                    "on you";

                lines.Add(
                    $"{GameStyle.Safe(item.Name)}{count}  <color={colour}>{held.OnYou} of {item.Need} {where}{fir}</color>" +
                    $"  <color=#FFFFFF60>{item.Verb}</color>");
            }

            lines.Add("");
        }

        /// <summary>What the profile holds of a condition's templates, summed across all of
        /// them, counting only found-in-raid copies when the objective insists on them - a stack the
        /// quest will refuse is not stock.
        ///
        /// OnYou falls back to the stash-inclusive total when the server cannot say where things
        /// are, which is the honest reading of an older payload: it knew how many, not where.</summary>
        private static (int OnYou, int InStash, int Elsewhere) HeldCount(
            ProfilePayloadDto profile, List<string> templates, bool foundInRaid, bool placesKnown)
        {
            if (profile?.ItemsOwned == null || templates == null) return (0, 0, 0);

            var onYou = 0;
            var inStash = 0;
            var elsewhere = 0;

            foreach (var template in templates)
            {
                if (string.IsNullOrWhiteSpace(template)) continue;
                if (!profile.ItemsOwned.TryGetValue(template, out var held) || held == null) continue;

                if (!placesKnown)
                {
                    onYou += foundInRaid ? held.FoundInRaid : held.Total;
                    continue;
                }

                onYou += foundInRaid ? held.OnPersonFoundInRaid : held.OnPerson;
                inStash += held.InStash;
                elsewhere += held.Elsewhere;
            }

            return (onYou, inStash, elsewhere);
        }

        /// <summary>The condition's own flag when the server sends one (schema v2); the English
        /// sentence as the fallback for an older server.</summary>
        private static bool NeedsFoundInRaid(ObjectiveDto objective) =>
            objective.FoundInRaid || MentionsFoundInRaid(objective.Text);

        /// <summary>What is done with the item, from the condition type - the difference between
        /// carrying a marker in and handing a graphics card over, which is the whole reason a
        /// player cares about this list before a raid rather than after one.</summary>
        private static string Verb(ObjectiveDto objective) => objective.ConditionType switch
        {
            "LeaveItemAtLocation" => "leave it in place",
            "PlaceBeacon" => "plant it",
            "HandoverItem" => "hand it in",
            "FindItem" => "find it",
            _ => ""
        };

        /// <summary>
        /// The display name of one of an objective's target items.
        ///
        /// The server resolves these from the locale table (schema v3). The fallback below is what
        /// every reader used to do on its own: take whatever follows the last colon in the objective
        /// sentence. That works for "Find in raid and hand over: Bitcoin" and fails completely for a
        /// sentence with no colon, where it returns the entire sentence as the item's name.
        /// </summary>
        internal static string ItemName(ObjectiveDto objective, int index)
        {
            if (objective == null) return "";

            var names = objective.TargetItemNames;
            if (names != null && index < names.Count && !string.IsNullOrWhiteSpace(names[index]))
                return names[index];

            var template = objective.TargetItems != null && index < objective.TargetItems.Count
                ? objective.TargetItems[index]
                : "";

            var text = objective.Text;
            if (string.IsNullOrEmpty(text)) return template;

            var colon = text.LastIndexOf(':');
            if (colon < 0 || colon >= text.Length - 1) return string.IsNullOrEmpty(template) ? text : template;

            return text.Substring(colon + 1).Trim();
        }

        /// <summary>
        /// The full route to a locked quest: everything it transitively requires that is still
        /// outstanding, in the order it can be done.
        ///
        /// This is the question "Requires" above cannot answer. That line names only the quest
        /// immediately before this one, which on a deep chain is nearly useless - the real answer
        /// is the twelve quests behind that one. Ordered by depth, so it reads top to bottom as a
        /// plan rather than a set.
        ///
        /// Gated on the route itself being non-empty rather than on the quest reading as Locked.
        /// Those are not the same test: a node is only Locked when the client has no live instance
        /// for it, so on a profile where everything is unlocked at once nothing would ever qualify
        /// and this section would silently never appear. Asking whether anything is outstanding
        /// works on any profile - a normally-available quest has its prerequisites done, so the
        /// route comes back empty and the section hides itself.
        /// </summary>
        private static void AddRoute(List<string> lines, QuestNode node, QuestGraphBuilder graph)
        {
            // A quest already handed in is not somewhere you are trying to get to.
            if (node.Status == ENodeStatus.Completed || graph == null) return;

            var route = QuestRoute.Remaining(node, graph);

            // A single step is already spelled out by "Requires" directly above; repeating it as a
            // one-item route would be noise.
            if (route.Count < 2) return;

            lines.Add($"<b>Route</b>  <color=#FFFFFF60>{route.Count} quests</color>");

            foreach (var step in route.Take(RouteSteps))
            {
                var hex = ColorUtility.ToHtmlStringRGB(QuestNodeView.ColorFor(step.Status));

                lines.Add($"<color=#{hex}>{QuestNodeView.GlyphFor(step.Status)}</color>  {step.Name}" +
                          $"  <color=#FFFFFF60>{step.TraderName}</color>");
            }

            if (route.Count > RouteSteps)
                lines.Add($"<color=#FFFFFF60>+{route.Count - RouteSteps} more</color>");

            lines.Add("");
        }

        /// <summary>
        /// The one gate blocking this quest, phrased as something to act on. Trader-scoped gates are
        /// named from the client's own trader list rather than the server guessing a display name,
        /// and a prerequisite names the actual quest, since the client has the graph to resolve it.
        /// </summary>
        internal static string FormatLockReason(
            QuestNode node, QuestGraphBuilder graph, ProfilePayloadDto profile)
        {
            var detail = LockReasonDetail(node, graph, profile);
            return detail == null ? null : $"<color=#{GameStyle.WarningHex}>Locked - {detail}</color>";
        }

        /// <summary>The gate as plain text, for a row that has its own colour and prefix - the
        /// Do next reason line used to show the server's raw detail here, without the trader's
        /// name or the "(you are N)" the detail panel adds.</summary>
        internal static string LockReasonDetail(
            QuestNode node, QuestGraphBuilder graph, ProfilePayloadDto profile)
        {
            if (profile?.LockReasons == null) return null;
            if (!profile.LockReasons.TryGetValue(node.Id, out var reason) || reason == null) return null;

            var detail = reason.Detail;

            if (!string.IsNullOrEmpty(reason.TraderId) && graph != null &&
                graph.TraderNames.TryGetValue(reason.TraderId, out var traderName))
            {
                detail = $"{traderName}: {detail}";
            }

            if (reason.Kind == "Level" && reason.CurrentValue > 0)
                detail = $"{detail} (you are {reason.CurrentValue})";

            // The profile carries every trader's loyalty level; "requires loyalty level 3" was
            // shown without the "you are LL2" that says how far off that is.
            if (reason.Kind == "Loyalty" && !string.IsNullOrEmpty(reason.TraderId) && profile.Traders != null)
            {
                var trader = profile.Traders.FirstOrDefault(t => t != null && t.Id == reason.TraderId);
                if (trader != null && trader.LoyaltyLevel > 0)
                    detail = $"{detail} (you are LL{trader.LoyaltyLevel})";
            }

            if (reason.Kind == "Prerequisite" && reason.BlockingQuestIds != null && graph != null)
            {
                var names = reason.BlockingQuestIds
                    .Select(id => graph.NodesById.TryGetValue(id, out var n) ? n.Name : null)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();

                if (names.Count > 0) detail = $"Requires: {string.Join(", ", names)}";
            }

            return detail;
        }

        /// <summary>What a prerequisite actually asks for, when it is not the usual "complete
        /// it": "(started is enough)" for a chain that opens on accepting the earlier quest, and
        /// "(N h after)" for a timed gate. Both were in the payload from the start and never
        /// shown, so a time-gated quest read as flatly locked. The gate is in seconds - the SPT
        /// wiki's quest sheet says so, whatever the server DTO's old comment said.</summary>
        internal static string PrerequisiteNote(QuestNode node, string prerequisiteId)
        {
            var dto = node?.Dto?.Prerequisites?.FirstOrDefault(p => p != null && p.Target == prerequisiteId);
            if (dto == null) return null;

            var notes = new List<string>();

            if (dto.Status != null && dto.Status.Count > 0 &&
                !dto.Status.Any(s => string.Equals(s, "Success", StringComparison.OrdinalIgnoreCase)))
            {
                notes.Add("started is enough");
            }

            if (dto.AvailableAfter > 0) notes.Add($"{Duration(dto.AvailableAfter)} after");

            return notes.Count == 0 ? null : $"({string.Join(", ", notes)})";
        }

        private static string Duration(int seconds) =>
            seconds >= 3600 ? $"{seconds / 3600} h" : $"{Math.Max(1, seconds / 60)} min";

        /// <summary>A quest in progress in one line: objectives done of total, and the live count
        /// of the one counter still moving when there is exactly one - "1/3 objectives · 7/15".
        /// An objective the game keeps no counter for reads as not done, which is what it is.</summary>
        internal static string ObjectiveProgress(QuestNode node, ProfilePayloadDto profile)
        {
            var objectives = node?.Dto?.Objectives;
            if (objectives == null || objectives.Count == 0) return "in progress";

            var total = 0;
            var done = 0;
            var moving = 0;
            string counter = null;

            foreach (var objective in objectives)
            {
                if (objective == null) continue;
                total++;

                if (!TryProgress(objective, profile, out var current, out var target)) continue;
                if (current >= target)
                {
                    done++;
                    continue;
                }

                moving++;
                counter = $"{current}/{target}";
            }

            var text = $"{done}/{total} objectives";
            return moving == 1 ? $"{text}  ·  {counter}" : text;
        }

        /// <summary>An objective with its live counter where the game is tracking one. Counters only
        /// exist for quests actually in progress, so most objectives render unchanged.</summary>
        /// <summary>The live counter behind an objective, when the profile payload has one.</summary>
        internal static bool TryProgress(ObjectiveDto objective, ProfilePayloadDto profile, out int current, out int target)
        {
            current = 0;
            target = 0;

            if (objective == null || profile?.ConditionProgress == null || string.IsNullOrEmpty(objective.Id)) return false;
            if (!profile.ConditionProgress.TryGetValue(objective.Id, out var done)) return false;

            target = Mathf.Max(1, objective.Count);
            current = Mathf.Clamp((int)done, 0, target);
            return true;
        }

        internal static string FormatObjective(ObjectiveDto objective, ProfilePayloadDto profile)
        {
            if (objective == null) return "";
            if (profile?.ConditionProgress == null || string.IsNullOrEmpty(objective.Id)) return objective.Text;
            if (!profile.ConditionProgress.TryGetValue(objective.Id, out var done)) return objective.Text;

            var target = Mathf.Max(1, objective.Count);
            var current = Mathf.Clamp((int)done, 0, target);

            var color = current >= target ? "#" + QuestNodeView.HexFor(ENodeStatus.Completed) : "#FFFFFF80";
            return $"{objective.Text}  <color={color}>{current}/{target}</color>";
        }

        /// <summary>Turns one payload reward into a display line. Trader-scoped rewards are named
        /// from the live session's trader list rather than from the payload, so a modded trader
        /// reads correctly without the server mod having to know about it.</summary>
        internal static string FormatReward(RewardDto reward, QuestGraphBuilder graph)
        {
            if (reward == null) return null;

            // TraderNames is sanitised where the trader list is built; the raw TraderId fallback
            // is not, and it is printed whenever a modded reward names a trader the graph has never
            // heard of - a third unsanitised sink in this one function.
            var trader = !string.IsNullOrEmpty(reward.TraderId) && graph != null &&
                         graph.TraderNames.TryGetValue(reward.TraderId, out var traderName)
                ? traderName
                : GameStyle.Safe(reward.TraderId);

            switch (reward.Type)
            {
                case "Experience":
                    return $"+{reward.Value:N0} XP";

                case "TraderStanding":
                    return string.IsNullOrEmpty(trader)
                        ? $"Reputation {reward.Value:+0.00;-0.00}"
                        : $"{trader} Rep {reward.Value:+0.00;-0.00}";

                case "TraderUnlock":
                    return string.IsNullOrEmpty(trader) ? "Unlocks a trader" : $"Unlocks {trader}";

                case "Item":
                    if (string.IsNullOrEmpty(reward.Name)) return null;
                    return reward.Value >= 2 ? $"{reward.Value:N0}x {reward.Name}" : reward.Name;

                case "Skill":
                    return string.IsNullOrEmpty(reward.Name) ? null : $"{reward.Name} +{reward.Value:N0}";

                case "AssortmentUnlock":
                    return string.IsNullOrEmpty(trader) ? "Unlocks a new trader offer" : $"Unlocks a new {trader} offer";

                default:
                    // Unknown/rare reward types (StashRows, Achievement, ...) still say something
                    // rather than silently vanishing, but only when there is a name worth showing.
                    //
                    // Type is wrapped HERE rather than at ingest: the switch above compares it
                    // against string literals, so wrapping it earlier would send every modded type
                    // into this branch. This is the only place it reaches the screen.
                    return string.IsNullOrEmpty(reward.Name)
                        ? null
                        : $"{GameStyle.Safe(reward.Type)}: {reward.Name}";
            }
        }
    }
}
