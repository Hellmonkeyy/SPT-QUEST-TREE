using System;
using System.Collections.Generic;
using System.Linq;
using QuestTree.QuestGraph;
using UnityEngine;

namespace QuestTree.UI
{
    /// <summary>Everything a quest involves, drawn wherever it is asked for.
    ///
    /// This was the private middle of QuestDetailPanel, which meant the only way to see what a quest
    /// actually required was to open the side panel - and the side panel replaces whatever you were
    /// looking at. The Do next tab needed the same content inline, and the choice was to reproduce it
    /// there or to lift it here. Reproduced, the two would have drifted: one of them would have
    /// learned about a new reward type or a new gate and the other would not.
    ///
    /// So the panel and the tab now render the SAME sections from the SAME code, and cannot disagree
    /// about what a quest involves. The context struct is what the panel used to hold in fields -
    /// where to draw, which graph, and what to do when the reader clicks a quest or a map link.</summary>
    internal static class QuestBody
    {
        /// <summary>What the body needs that is not the quest itself. A struct rather than six
        /// parameters threaded through eight methods, which is what the instance fields were doing
        /// before they were fields.</summary>
        internal readonly struct Ctx
        {
            public Ctx(RectTransform parent, float x, QuestGraphBuilder graph, QuestNode node,
                Action<QuestNode> onQuestLink, Action<QuestNode> onShowOnMap)
            {
                Parent = parent;
                X = x;
                Graph = graph;
                Node = node;
                OnQuestLink = onQuestLink;
                OnShowOnMap = onShowOnMap;
            }

            public RectTransform Parent { get; }
            public float X { get; }
            public QuestGraphBuilder Graph { get; }

            /// <summary>The quest being described. Only used to decide whether a linked quest needs
            /// its trader named - a link to the same trader's chain does not.</summary>
            public QuestNode Node { get; }

            public Action<QuestNode> OnQuestLink { get; }
            public Action<QuestNode> OnShowOnMap { get; }
        }

        /// <summary>What the last preset save said, and which build it was about. Static because the
        /// views are rebuilt on every repaint, so a result would otherwise vanish before it was read -
        /// the same reason the goal dropdown keeps its open flag in a static.</summary>
        private static string _presetKey;
        private static string _presetSaid;

        /// <summary>Offer to write this build into the player's own saved weapon builds.
        ///
        /// The game's modding screen can then load the whole gun in one click, and - the part worth
        /// having - it offers to BUY the parts that are missing, through the game's own purchase flow
        /// rather than a server-side purchase that would desync the profile.</summary>
        private static void AddSavePreset(Ctx ctx, string key, float width, ref float y)
        {
            if (string.IsNullOrEmpty(key)) return;

            y += 2f;

            AuxLayout.AddClickableRow(ctx.Parent,
                $"<color=#{ColorUtility.ToHtmlStringRGB(GameStyle.AccentColor)}>⊞  Save as a weapon preset</color>",
                ctx.X, ref y, width, false, () =>
                {
                    var result = QuestDataClient.SavePreset(key);

                    _presetKey = key;
                    _presetSaid = result.Saved
                        ? $"Saved as \"{result.Name}\". It appears in the game's build list - you may need to " +
                          "go back to the profile select for it to show."
                        : result.Reason;

                    ModSettings.RequestRepaint();
                }, 22f);

            if (_presetKey == key && !string.IsNullOrEmpty(_presetSaid))
                AuxLayout.AddWrapped(ctx.Parent, $"<color=#FFFFFF80>{GameStyle.Safe(_presetSaid)}</color>",
                    ctx.X, ref y, width, 11);

            y += 6f;
        }

        /// <summary>Draw the quest's sections at the cursor, and advance it past them.</summary>
        internal static void Render(
            RectTransform parent, ref float y, float x, float width,
            QuestNode node, QuestGraphBuilder graph, ProfilePayloadDto profile,
            Action<QuestNode> onQuestLink, Action<QuestNode> onShowOnMap)
        {
            if (node == null) return;

            var ctx = new Ctx(parent, x, graph, node, onQuestLink, onShowOnMap);

            Sections(ctx, node, profile, width, ref y);
        }

        private static void Sections(Ctx ctx, QuestNode node, ProfilePayloadDto profile, float width, ref float y)
        {
            // Route is worked out first, because whether it is going to be drawn decides whether
            // Requires should be.
            var route = node.Status != ENodeStatus.Completed && ctx.Graph != null
                ? QuestRoute.Remaining(node, ctx.Graph)
                : null;

            var routeShown = route != null && route.Count >= 2;

            // Requires - named here rather than drawn as a line, since a prerequisite from another
            // trader has no node in a single-trader tab. Clicking one selects it in the graph.
            //
            // Skipped when Route is about to list the same quests. The blocking prerequisite was
            // being stated three times - in the amber banner, here, and as the last row of Route,
            // which orders by depth and so puts the immediate one at the bottom - and three mentions
            // of one fact read as three facts.
            //
            // Only skipped when Route actually renders, though. A completed quest has no route at
            // all, and for those this section is the only place its prerequisites appear.
            if (node.PrerequisiteIds.Count > 0 && !routeShown)
            {
                AuxLayout.AddSectionHeader(ctx.Parent, ref y, "Requires", ctx.X, width);

                foreach (var prereqId in node.PrerequisiteIds)
                {
                    if (ctx.Graph != null && ctx.Graph.NodesById.TryGetValue(prereqId, out var prereq))
                        AddQuestLink(ctx, prereq, width, ref y, QuestSummary.PrerequisiteNote(node, prereqId));
                    else
                        AuxLayout.AddLabelAt(ctx.Parent, prereqId, ctx.X, ref y, AuxLayout.RowHeight, 12, width);
                }

                y += 8f;
            }

            // Route - the chain still to walk to reach a locked quest.
            if (routeShown)
            {
                AuxLayout.AddSectionHeader(ctx.Parent, ref y, $"Route  ·  {route.Count} quests", ctx.X, width);

                foreach (var step in route.Take(QuestSummary.RouteSteps))
                {
                    // The note Requires used to carry - "started is enough", "3 h after" - follows
                    // the quest it belongs to rather than being lost with that section.
                    var note = node.PrerequisiteIds.Contains(step.Id)
                        ? QuestSummary.PrerequisiteNote(node, step.Id)
                        : null;

                    AddQuestLink(ctx, step, width, ref y, note);
                }

                if (route.Count > QuestSummary.RouteSteps)
                    AuxLayout.AddLabelAt(ctx.Parent, $"<color=#FFFFFF60>+{route.Count - QuestSummary.RouteSteps} more</color>", ctx.X, ref y, AuxLayout.RowHeight, 11, width);

                y += 8f;
            }

            // Build - what a Gunsmith quest wants assembled. Ahead of Objectives because for those
            // quests it IS the objective: "Hand over the modified weapon" tells you nothing on its
            // own, and the thresholds under it are the whole task.
            //
            // Drawn from the DTO rather than from a pre-formatted list of strings, because the parts
            // have template ids now and a row that knows its id can be opened.
            // Every build the quest asks for, each as its own block headed by its weapon.
            foreach (var build in node.WeaponBuilds)
                if (build != null) BuildWeaponSection(ctx, build, width, ref y);

            // Bring - what to have on you before the raid, and how much of it you already hold.
            var bringLines = QuestSummary.ItemsToBringLines(node, profile);

            if (bringLines.Count > 0)
            {
                AuxLayout.AddSectionHeader(ctx.Parent, ref y, "Take with you", ctx.X, width);

                foreach (var line in bringLines)
                    AuxLayout.AddWrapped(ctx.Parent, line, ctx.X, ref y, width);

                y += 8f;
            }

            // Objectives - with the live counter as a bar where the profile has one, and the way to
            // the map when the quest happens somewhere.
            var objectives = node.StatedObjectives.ToList();
            if (objectives.Count > 0)
            {
                AuxLayout.AddSectionHeader(ctx.Parent, ref y, "Objectives", ctx.X, width);

                foreach (var objective in objectives)
                {
                    var text = QuestSummary.FormatObjective(objective, profile);

                    // An objective that names an item opens that item. The template is already on
                    // the wire in TargetItems - the same field the "what do I need to bring" list is
                    // built from - so this needed nothing new, only a row that could be clicked.
                    //
                    // The FIRST target, where there are several. An objective asking for any of a
                    // set has no single item to show, and picking one arbitrarily is still better
                    // than picking none: it opens the handbook at the right kind of thing.
                    var template = objective.TargetItems?.FirstOrDefault(t => !string.IsNullOrEmpty(t));

                    if (!string.IsNullOrEmpty(template))
                    {
                        var captured = template;
                        AuxLayout.AddClickableWrapped(ctx.Parent, text, ctx.X, ref y, width,
                            () => GameStyle.InspectItem(captured));
                    }
                    else
                    {
                        AuxLayout.AddWrapped(ctx.Parent, text, ctx.X, ref y, width);
                    }

                    if (QuestSummary.TryProgress(objective, profile, out var current, out var target) && target > 0)
                        AuxLayout.AddProgressBar(ctx.Parent, (float)current / target, ctx.X, ref y, width, QuestNodeView.ColorFor(ENodeStatus.Completed));
                }

                y += 8f;
            }

            // Outside the objectives block, deliberately. It used to live inside it, so a quest whose
            // objectives were all filtered away lost the way to the map as well - the reader was told
            // neither what to do nor where to go, which is the pair of things this section exists for.
            //
            // MapKeys, not LocationKey: a quest declaring "any" whose objectives were placed on a real
            // map has somewhere to show, and testing the declaration alone hid the link on precisely
            // the quests this release taught the mod to place.
            if (node.MapKeys.Any(k => !string.IsNullOrEmpty(k)) && ctx.OnShowOnMap != null)
            {
                AuxLayout.AddClickableRow(ctx.Parent, $"<color=#{ColorUtility.ToHtmlStringRGB(GameStyle.AccentColor)}>◎  Show on the map</color>",
                    ctx.X, ref y, width, false, () => ctx.OnShowOnMap(node), 22f);

                y += 8f;
            }

            // The same marks the boxes wear, so a reward reads the same in both places.
            var rewards = node.Rewards
                .Select(r => new
                {
                    Mark = QuestNodeView.GlyphForReward(r.Type),
                    Body = QuestSummary.FormatReward(r, ctx.Graph),
                    r.Template
                })
                .Where(r => !string.IsNullOrEmpty(r.Body))
                .ToList();

            if (rewards.Count > 0)
            {
                AuxLayout.AddSectionHeader(ctx.Parent, ref y, "Rewards", ctx.X, width);

                foreach (var reward in rewards)
                {
                    var text = string.IsNullOrEmpty(reward.Mark)
                        ? reward.Body
                        : $"<color=#FFFFFF60>{reward.Mark}</color>  {reward.Body}";

                    // An item you are being given, or an offer being unlocked, is something you can
                    // look at - the game's own inspect window knows far more about it than a line
                    // of text ever will. Rewards that are a number have nothing to open.
                    if (!string.IsNullOrEmpty(reward.Template))
                    {
                        var captured = reward.Template;
                        AuxLayout.AddClickableRow(ctx.Parent, text, ctx.X, ref y, width, false,
                            () => GameStyle.InspectItem(captured));
                    }
                    else
                    {
                        AuxLayout.AddWrapped(ctx.Parent, text, ctx.X, ref y, width);
                    }
                }

                y += 8f;
            }

            if (node.Unlocks.Count > 0)
            {
                AuxLayout.AddSectionHeader(ctx.Parent, ref y, "Unlocks", ctx.X, width);

                foreach (var unlocked in node.Unlocks)
                    AddQuestLink(ctx, unlocked, width, ref y);
            }
        }

        /// <summary>What a Gunsmith quest asks for: the weapon, the numbers it is checked against,
        /// the parts it insists on - and what this mod's own stat model makes of those parts.
        ///
        /// The model block is a verification instrument rather than a feature. WeaponStatModel
        /// shipped a release before any build generator so it could be proven against the game
        /// first, and then nothing ever called it - so the proof never happened and the generator
        /// stayed unwritten. Fit the named parts, open the game's inspect screen, compare.</summary>
        private static void BuildWeaponSection(Ctx ctx, WeaponBuildDto build, float width, ref float y)
        {
            AuxLayout.AddSectionHeader(ctx.Parent, ref y, "Build", ctx.X, width);

            if (!string.IsNullOrEmpty(build.WeaponName))
            {
                var weapon = build.WeaponTemplate;

                if (!string.IsNullOrEmpty(weapon))
                    AuxLayout.AddClickableWrapped(ctx.Parent, $"<b>{GameStyle.Safe(build.WeaponName)}</b>",
                        ctx.X, ref y, width, () => GameStyle.InspectItem(weapon));
                else
                    AuxLayout.AddWrapped(ctx.Parent, $"<b>{GameStyle.Safe(build.WeaponName)}</b>", ctx.X, ref y, width);
            }

            foreach (var threshold in build.Thresholds ?? new List<WeaponBuildThresholdDto>())
            {
                if (threshold == null) continue;

                // Durability is repair state, not something you assemble - phrased as advice,
                // because handing in a correct build at 60% durability is a real way to fail these.
                var text = string.Equals(threshold.Field, "durability", StringComparison.OrdinalIgnoreCase)
                    ? $"<color=#FFFFFF80>hand in at {GameStyle.Safe(threshold.Compare)} {threshold.Value:0.##}% durability</color>"
                    : $"{GameStyle.Safe(threshold.Field)}  <color=#{ColorUtility.ToHtmlStringRGB(GameStyle.AccentColor)}>{GameStyle.Safe(threshold.Compare)} {threshold.Value:0.##}</color>";

                AuxLayout.AddWrapped(ctx.Parent, text, ctx.X, ref y, width);
            }

            // Parts the build must contain. The ids run parallel to the names, so a row can open the
            // item even though the list the server sends is two lists rather than one.
            var names = build.RequiredItemNames ?? new List<string>();
            var ids = build.RequiredItemIds ?? new List<string>();

            for (var i = 0; i < names.Count; i++)
            {
                var text = $"must include {GameStyle.Safe(names[i])}";

                if (i < ids.Count && !string.IsNullOrEmpty(ids[i]))
                {
                    var captured = ids[i];
                    AuxLayout.AddClickableWrapped(ctx.Parent, text, ctx.X, ref y, width,
                        () => GameStyle.InspectItem(captured));
                }
                else
                {
                    AuxLayout.AddWrapped(ctx.Parent, text, ctx.X, ref y, width);
                }
            }

            foreach (var category in build.RequiredCategoryNames ?? new List<string>())
                AuxLayout.AddWrapped(ctx.Parent, $"must include a {GameStyle.Safe(category)}", ctx.X, ref y, width);

            if (build.EmptyTacticalSlots > 0)
                AuxLayout.AddWrapped(ctx.Parent,
                    $"leave {build.EmptyTacticalSlots:0} tactical slot(s) empty", ctx.X, ref y, width);

            AddSolution(ctx, build, width, ref y);
            AddSavePreset(ctx, build.Key, width, ref y);
            AddModelCheck(ctx, build, width, ref y);

            y += 8f;
        }

        /// <summary>The build the mod worked out for this quest.
        ///
        /// The point of the whole exercise: the requirement above says what the game will check, and
        /// this says what to fit to pass it. Every row opens the part, because a name alone does not
        /// tell you what you are looking for in a trader's list.
        ///
        /// It states what it has NOT checked as plainly as what it has. Height and width are real
        /// constraints in five quests and nothing here can score them, so a build that meets every
        /// number available can still be refused for its assembled size - and a player who was not
        /// told that would blame the mod rather than measure the gun.</summary>
        private static void AddSolution(Ctx ctx, WeaponBuildDto build, float width, ref float y)
        {
            var solution = build.Solution;
            if (solution == null || solution.Parts.Count == 0) return;

            y += 6f;

            // YOUR build first, when the server has one: the shared build is solved over every part in
            // the game and can name one you cannot get, and a build you cannot assemble is worse than an
            // expensive one. The per-profile answer is either the shared build with every part confirmed
            // obtainable, one searched again within what you can get, or the plain reason there is none.
            var mine = QuestDataClient.GetBuilds();
            var own = mine?.BuildFor(build.Key);

            if (own != null && (own.Status == "ok" || own.Status == "repaired" || own.Status == "blocked"))
            {
                AddOwnBuild(ctx, own, solution, mine.Stale, width, ref y);
                return;
            }

            if (mine != null && mine.HasProfile && !mine.Ready)
                AuxLayout.AddWrapped(ctx.Parent,
                    "<color=#FFFFFF60>working out which of these you can get - reopen the quest in a moment</color>",
                    ctx.X, ref y, width, 11);

            // Three states, not two. A build that meets every threshold the server can score is
            // not the same claim as one that meets every threshold the quest sets, and the five
            // height/width quests are the second kind. Saying "Suggested build" in accent green on
            // those would be the mod asserting something it never checked - so they get their own
            // wording, and the reason is spelled out in the unchecked line further down.
            string headline;
            if (!solution.Satisfies)
                headline = $"<color=#{GameStyle.WarningHex}>Closest build found  ·  {solution.Parts.Count} parts</color>";
            else if (!solution.FullyChecked)
                headline = $"<color=#{GameStyle.WarningHex}>Build meets every checkable requirement  ·  " +
                           $"{solution.Parts.Count} parts</color>";
            else
                headline = $"<color=#{ColorUtility.ToHtmlStringRGB(GameStyle.AccentColor)}>Suggested build  ·  " +
                           $"{solution.Parts.Count} parts</color>";

            AuxLayout.AddWrapped(ctx.Parent, headline, ctx.X, ref y, width, 12);

            foreach (var part in solution.Parts)
            {
                if (part == null || string.IsNullOrEmpty(part.Name)) continue;

                var row = $"<color=#FFFFFF60>{GameStyle.Safe(SlotLabel(part.Slot))}</color>  {GameStyle.Safe(part.Name)}";

                if (!string.IsNullOrEmpty(part.Template))
                {
                    var captured = part.Template;
                    AuxLayout.AddClickableWrapped(ctx.Parent, row, ctx.X, ref y, width,
                        () => GameStyle.InspectItem(captured));
                }
                else
                {
                    AuxLayout.AddWrapped(ctx.Parent, row, ctx.X, ref y, width);
                }
            }

            if (solution.Scores.Count > 0)
                AuxLayout.AddWrapped(ctx.Parent,
                    $"<color=#FFFFFF80>{string.Join("   ", solution.Scores)}</color>", ctx.X, ref y, width, 11);

            foreach (var unmet in solution.Unmet)
                AuxLayout.AddWrapped(ctx.Parent,
                    $"<color=#{GameStyle.ErrorHex}>{GameStyle.Safe(unmet)}</color>", ctx.X, ref y, width, 11);

            // A budget exhausted is not a proof that nothing exists, and saying so is the difference
            // between "this quest is hard" and "the mod gave up".
            if (solution.HitBudget)
                AuxLayout.AddWrapped(ctx.Parent,
                    $"<color=#{GameStyle.WarningHex}>the search ran out of budget - a build may exist that this " +
                    "did not reach</color>", ctx.X, ref y, width, 11);

            if (solution.Unchecked.Count > 0)
                AuxLayout.AddWrapped(ctx.Parent,
                    $"<color=#{GameStyle.WarningHex}>not checked: {GameStyle.Safe(string.Join(", ", solution.Unchecked))} " +
                    "- eyeball these on the gun before handing in</color>", ctx.X, ref y, width, 11);
        }

        /// <summary>The build as this player can assemble it. Every row says what the part costs THEM:
        /// already on the gun, already in the stash, a price from a trader, a barter, a flea estimate -
        /// or, on a blocked build, what would unlock it.
        ///
        /// A part they hold that is bolted to a weapon is priced as a purchase and says where it is,
        /// because stripping a working gun is their call and never the mod's assumption.</summary>
        private static void AddOwnBuild(Ctx ctx, ProfileBuildDto own, SolvedBuildDto shared, bool stale, float width, ref float y)
        {
            var parts = own.Parts ?? new List<ProfilePartDto>();
            var accent = ColorUtility.ToHtmlStringRGB(GameStyle.AccentColor);

            string headline;

            if (own.Status == "blocked")
            {
                headline = $"<color=#{GameStyle.WarningHex}>No build from what you can get  ·  closest attempt below</color>";
            }
            else
            {
                var cost = own.Cash > 0 ? $"{own.Cash:N0} ₽" : "nothing to buy";
                if (own.Barters > 0) cost += $" + {own.Barters} barter{(own.Barters == 1 ? "" : "s")}";
                if (own.FleaEstimate > 0) cost += $" + about {own.FleaEstimate:N0} ₽ on the flea";

                var what = own.Status == "repaired" ? "Your build (within what you can get)" : "Your build";
                var colour = shared.FullyChecked ? accent : GameStyle.WarningHex;

                headline = $"<color=#{colour}>{what}  ·  {parts.Count} parts  ·  {cost}</color>";
            }

            AuxLayout.AddWrapped(ctx.Parent, headline, ctx.X, ref y, width, 12);

            if (stale)
                AuxLayout.AddWrapped(ctx.Parent,
                    "<color=#FFFFFF60>your traders or stash have changed since this was worked out - it is being redone</color>",
                    ctx.X, ref y, width, 11);

            foreach (var part in parts)
            {
                if (part == null || string.IsNullOrEmpty(part.Name)) continue;

                var row = $"<color=#FFFFFF60>{GameStyle.Safe(SlotLabel(part.Slot))}</color>  {GameStyle.Safe(part.Name)}" +
                          $"  <color=#FFFFFF80>{TierLabel(part)}</color>";

                if (!string.IsNullOrEmpty(part.Template))
                {
                    var captured = part.Template;
                    AuxLayout.AddClickableWrapped(ctx.Parent, row, ctx.X, ref y, width,
                        () => GameStyle.InspectItem(captured));
                }
                else
                {
                    AuxLayout.AddWrapped(ctx.Parent, row, ctx.X, ref y, width);
                }
            }

            // The shared scores describe the shared parts. They still apply when the build IS the shared
            // one, and say nothing about a repaired one, so they are shown only then.
            if (own.Status == "ok" && shared.Scores.Count > 0)
                AuxLayout.AddWrapped(ctx.Parent,
                    $"<color=#FFFFFF80>{string.Join("   ", shared.Scores)}</color>", ctx.X, ref y, width, 11);

            if (own.Status == "blocked")
            {
                foreach (var unmet in own.Unmet ?? new List<string>())
                    AuxLayout.AddWrapped(ctx.Parent,
                        $"<color=#{GameStyle.ErrorHex}>{GameStyle.Safe(unmet)}</color>", ctx.X, ref y, width, 11);

                // The remedy, which is the whole point: a trader to level, the flea to unlock, or the
                // plain fact that nobody sells it - three different problems a player must tell apart.
                if (!string.IsNullOrEmpty(own.Why))
                    AuxLayout.AddWrapped(ctx.Parent,
                        $"<color=#{GameStyle.WarningHex}>{GameStyle.Safe(WhyLabel(own.Why))}</color>", ctx.X, ref y, width, 11);
            }

            if (shared.Unchecked.Count > 0)
                AuxLayout.AddWrapped(ctx.Parent,
                    $"<color=#{GameStyle.WarningHex}>not checked: {GameStyle.Safe(string.Join(", ", shared.Unchecked))} " +
                    "- eyeball these on the gun before handing in</color>", ctx.X, ref y, width, 11);
        }

        /// <summary>What one part costs this player, in a few words beside its name.</summary>
        private static string TierLabel(ProfilePartDto part)
        {
            string label;

            switch (part.Tier)
            {
                case "fitted": label = "already on the gun"; break;
                case "inplace": label = "already on yours"; break;
                case "owned": label = "in your stash"; break;
                case "buyable": label = part.Price.HasValue ? $"{part.Price.Value:N0} ₽" : "from a trader"; break;
                case "barter": label = "barter"; break;
                case "flea": label = part.Price.HasValue ? $"about {part.Price.Value:N0} ₽ on the flea" : "on the flea"; break;
                case "absent": label = string.IsNullOrEmpty(part.Gate) ? "not sold" : $"needs {part.Gate}"; break;
                default: label = ""; break;
            }

            if (!string.IsNullOrEmpty(part.Where)) label += (label.Length > 0 ? "  ·  " : "") + part.Where;
            if (part.Named) label += (label.Length > 0 ? "  ·  " : "") + "the quest names this part";

            return GameStyle.Safe(label);
        }

        /// <summary>The server's "trader level - ..." / "flea market - ..." / "not sold - ..." as a
        /// sentence a player acts on.</summary>
        private static string WhyLabel(string why)
        {
            if (why.StartsWith("trader level - ", StringComparison.Ordinal))
                return "to build it you need: " + why.Substring("trader level - ".Length);
            if (why.StartsWith("flea market - ", StringComparison.Ordinal))
                return why.Substring("flea market - ".Length);
            if (why.StartsWith("not sold - ", StringComparison.Ordinal))
                return why.Substring("not sold - ".Length);

            return why;
        }

        /// <summary>"mod_muzzle" as "muzzle". The game's own slot names are readable once the prefix
        /// is gone, and inventing a lookup would only drift from what a modded slot calls itself.</summary>
        private static string SlotLabel(string slot)
        {
            if (string.IsNullOrEmpty(slot)) return "";

            var trimmed = slot.StartsWith("mod_", StringComparison.OrdinalIgnoreCase) ? slot.Substring(4) : slot;

            return trimmed.Replace('_', ' ');
        }

        /// <summary>The stat model's own numbers for the parts above, so they can be read against
        /// the game.
        ///
        /// Deliberately says what it is and what to do with it. A number with no instruction beside
        /// it is decoration, and this one exists to be acted on exactly once - after which, if it
        /// agrees with the game, a build generator becomes writable.</summary>
        private static void AddModelCheck(Ctx ctx, WeaponBuildDto build, float width, ref float y)
        {
            var check = build.ModelCheck;
            if (check == null) return;

            y += 4f;

            var parts = new List<string>
            {
                $"ergonomics {check.Ergonomics:0.##}",
                $"recoil {check.Recoil:0.##}",
                $"weight {check.Weight:0.###} kg"
            };

            if (check.MagazineCapacity != null) parts.Add($"magazine {check.MagazineCapacity}");
            if (check.EffectiveDistance != null) parts.Add($"distance {check.EffectiveDistance:0}");

            // Whether those parts actually make a gun decides what the numbers below mean, so it
            // is said first rather than as a footnote.
            var lead = check.UnfilledRequiredSlots > 0
                ? $"<color=#FFFFFF80>Model check - these {check.PartsNamed} part(s) leave " +
                  $"{check.UnfilledRequiredSlots} of {check.RequiredSlots} required slots empty, so this is a " +
                  "partial build and the scores below are a floor:</color>"
                : $"<color=#FFFFFF80>Model check - fit the {check.PartsNamed} part(s) above to this weapon and " +
                  "compare with the inspect screen:</color>";

            AuxLayout.AddWrapped(ctx.Parent, lead, ctx.X, ref y, width, 11);

            AuxLayout.AddWrapped(ctx.Parent,
                $"<color=#{ColorUtility.ToHtmlStringRGB(GameStyle.AccentColor)}>{string.Join("   ", parts)}</color>",
                ctx.X, ref y, width, 11);

            if (check.PartsScored < check.PartsNamed)
                AuxLayout.AddWrapped(ctx.Parent,
                    $"<color=#{GameStyle.WarningHex}>only {check.PartsScored} of {check.PartsNamed} parts could be " +
                    "scored - the rest are missing from the item table, so this comparison is incomplete.</color>",
                    ctx.X, ref y, width, 11);

            if (check.Clamped != null && check.Clamped.Count > 0)
                AuxLayout.AddWrapped(ctx.Parent,
                    $"<color=#{GameStyle.ErrorHex}>clamped: {GameStyle.Safe(string.Join("; ", check.Clamped))} - " +
                    "these numbers are not the game's and the comparison is void.</color>",
                    ctx.X, ref y, width, 11);
        }

        /// <summary>A quest named as a row you can click to go to it: glyph and name in the status
        /// colour, the trader beside it when it is a different one, and a dim note after that
        /// when the caller has one (a prerequisite's terms).</summary>
        private static void AddQuestLink(Ctx ctx, QuestNode target, float width, ref float y, string note = null)
        {
            var hex = QuestNodeView.HexFor(target.Status);
            var trader = ctx.Node != null && target.TraderId == ctx.Node.TraderId
                ? ""
                : $"  <color=#FFFFFF60>{GameStyle.Safe(target.TraderName)}</color>";
            var suffix = note == null ? "" : $"  <color=#FFFFFF60>{note}</color>";

            var text = $"<color=#{hex}>{QuestNodeView.GlyphFor(target.Status)}</color>  {GameStyle.Safe(target.Name)}{trader}{suffix}";
            var captured = target;

            AuxLayout.AddClickableRow(ctx.Parent, text, ctx.X, ref y, width, false,
                () => ctx.OnQuestLink?.Invoke(captured));
        }
    }
}
