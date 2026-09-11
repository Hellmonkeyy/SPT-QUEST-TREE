using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using QuestTree.QuestGraph;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QuestTree.UI

{
    /// <summary>
    /// The side panel that opens when a quest node is clicked: who gives it, what gates it, what it
    /// asks for and how far along that is, what it pays, and what it opens up - laid out as rows a
    /// player can act on rather than one block of text. Prerequisite, route and unlock rows select
    /// that quest in the graph; an objective with a place has a "show on map" row.
    ///
    /// A plain class, not a MonoBehaviour: it has no per-frame work and no Unity lifecycle of its
    /// own. It is opened by a node's click callback and closed by its own X button or by Escape,
    /// which the panel's Update forwards - see QuestTreePanel.Update.
    ///
    /// Rows are built on a y cursor into a scroll view, the way the map's sidebar is. Same
    /// primitives (AuxLayout), same section headers, so the two read as one design.
    /// </summary>
    internal sealed class QuestDetailPanel
    {
        /// <summary>Width when expanded, and the sliver left behind when collapsed - just enough
        /// to keep the chevron on screen so the panel can be brought back.</summary>
        public const float ExpandedWidth = 360f;
        private const float CollapsedWidth = 26f;

        private const float Inset = 12f;

        /// <summary>Room above the content for the close and collapse buttons.</summary>
        private const float TopChrome = 34f;


        private QuestGraphBuilder _graph;
        private Action<QuestNode> _focusNode;
        private Action<QuestNode> _showOnMap;
        private Func<IEftSession> _session;

        private RectTransform _detailPanel;
        private RectTransform _content;

        /// <summary>The inner margin the section builders draw at - see Show.</summary>
        private float _left;
        private QuestNode _detailNode;

        /// <summary>The children hidden when collapsed - everything except the chevron itself.</summary>
        private readonly List<GameObject> _collapsible = new();
        private TMP_Text _collapseGlyph;

        public bool IsOpen => _detailPanel != null && _detailPanel.gameObject.activeSelf;

        /// <summary>The quest being shown, kept across a view switch (see HideForTabSwitch).</summary>
        public QuestNode CurrentNode => _detailNode;

        /// <summary>Whether there is somewhere to go back to, and how - supplied by the panel,
        /// which keeps the history. The row is only drawn when the first says yes.</summary>
        public Func<bool> CanGoBack;
        public Action GoBack;

        /// <summary>How much of the viewport's right edge the panel covers when it is up: the
        /// full width, or nothing while it is collapsed to its sliver. The graph subtracts this
        /// when framing so a focused quest is not centred underneath the panel.</summary>
        public float CoveredWidth =>
            ModSettings.Ready && ModSettings.DetailPanelCollapsed.Value ? 0f : ExpandedWidth;

        /// <summary>Raised when the panel is dismissed outright (X, Escape) - not when a view
        /// switch merely takes it off screen. The tree unmarks its selected box from this.</summary>
        public Action OnHidden;

        /// <param name="focusNode">Selects and frames a quest in the graph - what a prerequisite,
        /// route or unlock row does when clicked.</param>
        /// <param name="showOnMap">Switches to the map with this quest selected.</param>
        /// <param name="session">The live session, for the trader's portrait. A function, since the
        /// session arrives with Show and the panel is built before it.</param>
        public void Build(
            RectTransform root, QuestGraphBuilder graph, Action<QuestNode> focusNode,
            Action<QuestNode> showOnMap, Func<IEftSession> session)
        {
            _graph = graph;
            _focusNode = focusNode;
            _showOnMap = showOnMap;
            _session = session;

            var panelGo = new GameObject("DetailPanel", typeof(RectTransform), typeof(Image));
            _detailPanel = (RectTransform)panelGo.transform;
            _detailPanel.SetParent(root, worldPositionStays: false);
            _detailPanel.anchorMin = new Vector2(1f, 0f);
            _detailPanel.anchorMax = new Vector2(1f, 1f);
            _detailPanel.pivot = new Vector2(1f, 0.5f);
            _detailPanel.sizeDelta = new Vector2(ExpandedWidth, -40f);
            _detailPanel.anchoredPosition = new Vector2(0f, -20f);

            var detailBackground = panelGo.GetComponent<Image>();
            detailBackground.color = GameStyle.ScreenColor;
            GameStyle.ApplyPanel(detailBackground);

            // The content scrolls: a locked quest deep in a chain has a route, prerequisites and
            // rewards that together run past any screen. Its Image is clear but still hit-testable,
            // which is what lets the wheel reach the ScrollRect.
            var scrollGo = new GameObject(
                "DetailScroll", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(ScrollRect));
            var scrollRect = (RectTransform)scrollGo.transform;
            scrollRect.SetParent(_detailPanel, worldPositionStays: false);
            scrollRect.anchorMin = Vector2.zero;
            scrollRect.anchorMax = Vector2.one;
            scrollRect.offsetMin = new Vector2(Inset, Inset);
            scrollRect.offsetMax = new Vector2(-Inset, -TopChrome);
            scrollGo.GetComponent<Image>().color = Color.clear;
            _collapsible.Add(scrollGo);

            var contentGo = new GameObject("DetailContent", typeof(RectTransform));
            _content = (RectTransform)contentGo.transform;
            _content.SetParent(scrollRect, worldPositionStays: false);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0f, 1f);
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = Vector2.zero;

            var scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.content = _content;
            scroll.viewport = scrollRect;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            BuildDetailCloseButton();
            BuildCollapseButton();

            // Restore whatever collapse state was left from last session before the panel is ever
            // shown, so it never appears expanded for a frame and then snaps shut.
            ApplyCollapsed(ModSettings.Ready && ModSettings.DetailPanelCollapsed.Value);

            _detailPanel.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ chrome

        /// <summary>Collapses the panel to a sliver instead of dismissing it. Distinct from the X on
        /// purpose: X clears the selected quest, this keeps it and just gives the graph its width
        /// back, so you can look at the tree and come straight back to the same quest.</summary>
        private void BuildCollapseButton()
        {
            const float size = 22f;

            var go = new GameObject("DetailCollapse", typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_detailPanel, worldPositionStays: false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(2f, -6f);
            rect.sizeDelta = new Vector2(size, size);

            var background = go.GetComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0.1f);
            GameStyle.ApplyPanel(background);

            _collapseGlyph = CreateCentredLabel(rect, "", 12);

            var button = go.GetComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(ToggleCollapsed);
        }

        private void ToggleCollapsed()
        {
            var collapsed = !(ModSettings.Ready && ModSettings.DetailPanelCollapsed.Value);
            if (ModSettings.Ready) ModSettings.DetailPanelCollapsed.Value = collapsed;
            ApplyCollapsed(collapsed);
        }

        private void ApplyCollapsed(bool collapsed)
        {
            if (_detailPanel == null) return;

            _detailPanel.sizeDelta = new Vector2(collapsed ? CollapsedWidth : ExpandedWidth, -40f);

            foreach (var child in _collapsible)
                if (child != null) child.SetActive(!collapsed);

            // Points the way the panel will move: collapsed shows "open me" (left edge), expanded
            // shows "put me away".
            if (_collapseGlyph != null) _collapseGlyph.text = collapsed ? "<" : ">";
        }

        private void BuildDetailCloseButton()
        {
            const float size = 22f;

            var closeGo = new GameObject("DetailClose", typeof(RectTransform), typeof(Image), typeof(Button));
            var closeRect = (RectTransform)closeGo.transform;
            closeRect.SetParent(_detailPanel, worldPositionStays: false);
            closeRect.anchorMin = closeRect.anchorMax = new Vector2(1f, 1f);
            closeRect.pivot = new Vector2(1f, 1f);
            closeRect.anchoredPosition = new Vector2(-6f, -6f);
            closeRect.sizeDelta = new Vector2(size, size);
            _collapsible.Add(closeGo);

            var background = closeGo.GetComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0.1f);
            GameStyle.ApplyPanel(background);

            CreateCentredLabel(closeRect, "X", 12);

            var button = closeGo.GetComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(Hide);
        }

        private static TMP_Text CreateCentredLabel(RectTransform parent, string text, int fontSize)
        {
            var labelGo = new GameObject("Text", typeof(RectTransform));
            var labelRect = (RectTransform)labelGo.transform;
            labelRect.SetParent(parent, worldPositionStays: false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.raycastTarget = false;
            GameStyle.Apply(label);
            return label;
        }

        // ------------------------------------------------------------------ content

        public void Show(QuestNode node)
        {
            if (node == null || _content == null) return;

            _detailNode = node;

            AuxLayout.ClearChildren(_content);

            var profile = QuestDataClient.GetProfile();

            // Rows start a few pixels in from the mask's edge: glyphs overhang their rect slightly
            // and the first stroke of every line was being shaved off.
            const float leftPad = 5f;
            var width = ExpandedWidth - Inset * 2f - leftPad;
            var y = 2f;

            try
            {
                BuildHeader(node, profile, width, leftPad, ref y);
                BuildSections(node, profile, width, leftPad, ref y);
            }
            catch (Exception ex)
            {
                // A malformed modded quest must not leave the panel empty and silent.
                Plugin.LogSource?.LogWarning($"QuestTree: could not lay out the detail for '{node.Name}': {ex.Message}");
                AuxLayout.AddWrapped(_content, $"<color=#{GameStyle.ErrorHex}>Could not show this quest - see the BepInEx log.</color>", 0f, ref y, width);
            }

            _content.sizeDelta = new Vector2(0f, y + Inset);
            _content.anchoredPosition = Vector2.zero;
            _detailPanel.gameObject.SetActive(true);
        }

        private void BuildHeader(QuestNode node, ProfilePayloadDto profile, float width, float left, ref float y)
        {
            const float avatar = 36f;
            var textX = left + avatar + 10f;

            AddAvatar(node, avatar, left, y);

            var nameY = y;
            AuxLayout.AddWrapped(_content, $"<b>{GameStyle.Safe(node.Name)}</b>", textX, ref nameY, width - textX, 16);
            AuxLayout.AddLabelAt(_content, $"<color=#FFFFFF80>{GameStyle.Safe(node.TraderName)}</color>", textX, ref nameY, 16f, 11, width - textX);
            y = Mathf.Max(nameY, y + avatar) + 6f;

            // Walking Requires and Unlocks links is how the panel is mostly used, and there was no
            // way back along them except finding the quest again by hand.
            if (CanGoBack?.Invoke() == true)
            {
                AuxLayout.AddClickableRow(_content, "<color=#FFFFFF80>←  Back</color>", left, ref y, width, false,
                    () => GoBack?.Invoke(), 20f);
                y += 4f;
            }

            // Chips: the facts that gate the quest, at a glance and in the palette the tree uses.
            var chipX = left;
            AuxLayout.AddChip(_content, QuestNodeView.NameFor(node.Status), QuestNodeView.ColorFor(node.Status), ref chipX, y);
            if (node.Level > 0) AuxLayout.AddChip(_content, $"Lv {node.Level}", GameStyle.TextColor, ref chipX, y);
            if (!string.IsNullOrEmpty(node.LocationId) && !node.LocationId.Equals("any", StringComparison.OrdinalIgnoreCase))
                AuxLayout.AddChip(_content, node.LocationId, GameStyle.TextColor, ref chipX, y);
            // Both chips regardless of the badge setting: that setting is about what the boxes
            // wear at a glance, and the detail is where the facts are stated in full.
            if (node.IsKappaRequired) AuxLayout.AddChip(_content, "Kappa", GameStyle.KappaGold, ref chipX, y);
            if (node.IsCollectorPrerequisite) AuxLayout.AddChip(_content, "Collector", GameStyle.CollectorBlue, ref chipX, y);
            y += 26f;

            // Faction- and edition-locked quests are shown rather than hidden, so this is what stops
            // one reading as a bug in the tree.
            if (node.UnobtainableReason != null)
                AuxLayout.AddWrapped(_content, $"<color=#{GameStyle.ErrorHex}>{node.UnobtainableReason}</color>", left, ref y, width);

            // The single gate actually stopping you, computed server-side against your level,
            // loyalty and standing.
            var lockReason = QuestSummary.FormatLockReason(node, _graph, profile);
            if (!string.IsNullOrEmpty(lockReason))
            {
                // Clickable when a QUEST is the gate, since then the banner names somewhere you can
                // go. A level or loyalty gate names a number, which there is nowhere to click to.
                var blocker = QuestSummary.BlockingQuest(node, _graph, profile);

                if (blocker != null)
                {
                    var captured = blocker;
                    AuxLayout.AddClickableRow(_content, lockReason, left, ref y, width, false,
                        () => _focusNode?.Invoke(captured));
                }
                else
                {
                    AuxLayout.AddWrapped(_content, lockReason, left, ref y, width);
                }
            }

            AuxLayout.AddClickableRow(_content, "<color=#FFFFFF80>Open the wiki page  ↗</color>", left, ref y, width, false, OpenWiki, 20f);
            y += 6f;
        }

        private void BuildSections(QuestNode node, ProfilePayloadDto profile, float width, float left, ref float y)
        {
            _left = left;

            // Route is worked out first, because whether it is going to be drawn decides whether
            // Requires should be.
            var route = node.Status != ENodeStatus.Completed && _graph != null
                ? QuestRoute.Remaining(node, _graph)
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
                AuxLayout.AddSectionHeader(_content, ref y, "Requires", _left, width);

                foreach (var prereqId in node.PrerequisiteIds)
                {
                    if (_graph != null && _graph.NodesById.TryGetValue(prereqId, out var prereq))
                        AddQuestLink(prereq, width, ref y, QuestSummary.PrerequisiteNote(node, prereqId));
                    else
                        AuxLayout.AddLabelAt(_content, prereqId, _left, ref y, AuxLayout.RowHeight, 12, width);
                }

                y += 8f;
            }

            // Route - the chain still to walk to reach a locked quest.
            if (routeShown)
            {
                AuxLayout.AddSectionHeader(_content, ref y, $"Route  ·  {route.Count} quests", _left, width);

                foreach (var step in route.Take(QuestSummary.RouteSteps))
                {
                    // The note Requires used to carry - "started is enough", "3 h after" - follows
                    // the quest it belongs to rather than being lost with that section.
                    var note = node.PrerequisiteIds.Contains(step.Id)
                        ? QuestSummary.PrerequisiteNote(node, step.Id)
                        : null;

                    AddQuestLink(step, width, ref y, note);
                }

                if (route.Count > QuestSummary.RouteSteps)
                    AuxLayout.AddLabelAt(_content, $"<color=#FFFFFF60>+{route.Count - QuestSummary.RouteSteps} more</color>", _left, ref y, AuxLayout.RowHeight, 11, width);

                y += 8f;
            }

            // Objectives - with the live counter as a bar where the profile has one, and the way to
            // the map when the quest happens somewhere.
            var objectives = node.NecessaryObjectives.ToList();
            if (objectives.Count > 0)
            {
                AuxLayout.AddSectionHeader(_content, ref y, "Objectives", _left, width);

                foreach (var objective in objectives)
                {
                    AuxLayout.AddWrapped(_content, QuestSummary.FormatObjective(objective, profile), _left, ref y, width);

                    if (QuestSummary.TryProgress(objective, profile, out var current, out var target) && target > 0)
                        AuxLayout.AddProgressBar(_content, (float)current / target, _left, ref y, width, QuestNodeView.ColorFor(ENodeStatus.Completed));
                }

                // MapKeys, not LocationKey: a quest declaring "any" whose objectives were placed on
                // a real map has somewhere to show, and testing the declaration alone hid the link
                // on precisely the quests this release taught the mod to place.
                var hasPlace = node.MapKeys.Any(k => !string.IsNullOrEmpty(k));
                if (hasPlace && _showOnMap != null)
                {
                    y += 2f;
                    AuxLayout.AddClickableRow(_content, $"<color=#{ColorUtility.ToHtmlStringRGB(GameStyle.AccentColor)}>◎  Show on the map</color>",
                        0f, ref y, width, false, () => _showOnMap(node), 22f);
                }

                y += 8f;
            }

            // The same marks the boxes wear, so a reward reads the same in both places.
            var rewards = node.Rewards
                .Select(r => new { Mark = QuestNodeView.GlyphForReward(r.Type), Body = QuestSummary.FormatReward(r, _graph) })
                .Where(r => !string.IsNullOrEmpty(r.Body))
                .Select(r => new { Text = string.IsNullOrEmpty(r.Mark) ? r.Body : $"<color=#FFFFFF60>{r.Mark}</color>  {r.Body}" })
                .ToList();

            if (rewards.Count > 0)
            {
                AuxLayout.AddSectionHeader(_content, ref y, "Rewards", _left, width);

                foreach (var reward in rewards)
                    AuxLayout.AddWrapped(_content, reward.Text, _left, ref y, width);

                y += 8f;
            }

            if (node.Unlocks.Count > 0)
            {
                AuxLayout.AddSectionHeader(_content, ref y, "Unlocks", _left, width);

                foreach (var unlocked in node.Unlocks)
                    AddQuestLink(unlocked, width, ref y);
            }
        }

        /// <summary>A quest named as a row you can click to go to it: glyph and name in the status
        /// colour, the trader beside it when it is a different one, and a dim note after that
        /// when the caller has one (a prerequisite's terms).</summary>
        private void AddQuestLink(QuestNode target, float width, ref float y, string note = null)
        {
            var hex = QuestNodeView.HexFor(target.Status);
            var trader = _detailNode != null && target.TraderId == _detailNode.TraderId
                ? ""
                : $"  <color=#FFFFFF60>{GameStyle.Safe(target.TraderName)}</color>";
            var suffix = note == null ? "" : $"  <color=#FFFFFF60>{note}</color>";

            var text = $"<color=#{hex}>{QuestNodeView.GlyphFor(target.Status)}</color>  {GameStyle.Safe(target.Name)}{trader}{suffix}";
            var captured = target;

            AuxLayout.AddClickableRow(_content, text, _left, ref y, width, false,
                () => _focusNode?.Invoke(captured));
        }

        /// <summary>The trader's portrait, from the game's own avatar loader - the same call the
        /// trader cards use, so there is no image fetching of our own. Silently absent when the
        /// session has no such trader (a modded quest with no trader, say).</summary>
        private void AddAvatar(QuestNode node, float size, float x, float y)
        {
            var session = _session?.Invoke();
            var trader = session?.Traders?.FirstOrDefault(t => t.Id == node.TraderId);
            if (trader == null) return;

            var iconGo = new GameObject("Avatar", typeof(RectTransform), typeof(Image));
            var iconRect = (RectTransform)iconGo.transform;
            iconRect.SetParent(_content, worldPositionStays: false);
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(0f, 1f);
            iconRect.pivot = new Vector2(0f, 1f);
            iconRect.anchoredPosition = new Vector2(x, -y);
            iconRect.sizeDelta = new Vector2(size, size);
            iconGo.GetComponent<Image>().raycastTarget = false;

            var cancel = iconGo.AddComponent<CancelOnDestroy>();

            try
            {
                trader.GetAndAssignAvatar(iconGo.GetComponent<Image>(), cancel.Token)
                    .ContinueWith(
                        t => Plugin.LogSource?.LogWarning($"QuestTree: trader avatar load failed for '{node.TraderId}': {t.Exception?.GetBaseException().Message}"),
                        TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: failed to load trader avatar for '{node.TraderId}': {ex.Message}");
            }
        }

        /// <summary>Opens the quest's page on the official EFT wiki. URL pattern confirmed against
        /// real wiki pages; a handful of names won't map exactly (disambiguation, punctuation).</summary>
        private void OpenWiki()
        {
            if (_detailNode == null) return;

            try
            {
                var slug = Uri.EscapeDataString(_detailNode.Name.Replace(' ', '_'));
                Application.OpenURL($"https://escapefromtarkov.fandom.com/wiki/{slug}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: failed to open wiki page: {ex.Message}");
            }
        }

        /// <summary>Rebuilds the open detail from current data - a hand-in moves the counters,
        /// the status chip and the lock reason, and the panel used to keep the old ones until it
        /// was closed and reopened. Nothing to do while closed; a panel hidden for a view switch
        /// is rebuilt when it is next shown anyway.</summary>
        public void Refresh()
        {
            if (IsOpen && _detailNode != null) Show(_detailNode);
        }

        public void Hide()
        {
            _detailNode = null;
            if (_detailPanel != null) _detailPanel.gameObject.SetActive(false);
            OnHidden?.Invoke();
        }

        /// <summary>Takes the panel off screen without forgetting which quest it was showing -
        /// what switching to a whole-screen view does. Kept distinct from <see cref="Hide"/>, which
        /// also clears the remembered node.</summary>
        public void HideForTabSwitch()
        {
            if (_detailPanel != null) _detailPanel.gameObject.SetActive(false);
        }
    }
}
