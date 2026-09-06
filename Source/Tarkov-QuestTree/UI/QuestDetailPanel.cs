using System;
using System.Collections.Generic;
using System.Linq;
using QuestTree.QuestGraph;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// The side panel that opens when a quest node is clicked: prerequisites, objectives, rewards,
    /// what the quest unlocks, and a link to its wiki page.
    ///
    /// A plain class, not a MonoBehaviour: it has no per-frame work and no Unity lifecycle of its
    /// own. It is opened by a node's click callback and closed by its own X button or by Escape,
    /// which the panel's Update forwards - see QuestTreePanel.Update.
    /// </summary>
    internal sealed class QuestDetailPanel
    {
        /// <summary>Width when expanded, and the sliver left behind when collapsed - just enough
        /// to keep the chevron on screen so the panel can be brought back.</summary>
        private const float ExpandedWidth = 320f;
        private const float CollapsedWidth = 26f;

        private QuestGraphBuilder _graph;
        private RectTransform _detailPanel;
        private TMP_Text _detailText;
        private QuestNode _detailNode;

        /// <summary>Everything except the collapse chevron, so collapsing hides the content without
        /// having to reach for each piece individually.</summary>
        /// <summary>The children hidden when collapsed - everything except the chevron itself.
        /// Tracked as they are built rather than reparented under a container, because moving the
        /// existing children would change sibling/draw order for no benefit.</summary>
        private readonly System.Collections.Generic.List<GameObject> _collapsible = new();

        private TMP_Text _collapseGlyph;

        public bool IsOpen => _detailPanel != null && _detailPanel.gameObject.activeSelf;

        public void Build(RectTransform root, QuestGraphBuilder graph)
        {
            _graph = graph;

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

            const float wikiButtonHeight = 32f;

            var textGo = new GameObject("Text", typeof(RectTransform));
            var textRect = (RectTransform)textGo.transform;
            textRect.SetParent(_detailPanel, worldPositionStays: false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(12f, 12f + wikiButtonHeight + 8f); // leaves room for the wiki button below
            textRect.offsetMax = new Vector2(-12f, -12f);

            _detailText = textGo.AddComponent<TextMeshProUGUI>();
            _detailText.fontSize = 13;
            _detailText.color = Color.white;
            _detailText.enableWordWrapping = true;
            GameStyle.Apply(_detailText);
            _collapsible.Add(textGo);

            BuildDetailCloseButton();
            BuildWikiButton(wikiButtonHeight);
            BuildCollapseButton();

            // Restore whatever collapse state was left from last session before the panel is ever
            // shown, so it never appears expanded for a frame and then snaps shut.
            ApplyCollapsed(ModSettings.Ready && ModSettings.DetailPanelCollapsed.Value);

            _detailPanel.gameObject.SetActive(false);
        }

        /// <summary>
        /// Collapses the panel to a sliver instead of dismissing it. Distinct from the X on purpose:
        /// X clears the selected quest, this keeps it and just gives the graph its 320px back, so
        /// you can look at the tree and come straight back to the same quest.
        /// </summary>
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

            var labelGo = new GameObject("Text", typeof(RectTransform));
            var labelRect = (RectTransform)labelGo.transform;
            labelRect.SetParent(rect, worldPositionStays: false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            _collapseGlyph = labelGo.AddComponent<TextMeshProUGUI>();
            _collapseGlyph.fontSize = 12;
            _collapseGlyph.alignment = TextAlignmentOptions.Center;
            _collapseGlyph.color = Color.white;
            GameStyle.Apply(_collapseGlyph);

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

        /// <summary>
        /// Dismisses the detail panel. Until this existed the panel could only ever be opened -
        /// clicking one quest cost 320px of graph for the rest of the session, which is the single
        /// most obviously unfinished thing about the UI.
        /// </summary>
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

            var labelGo = new GameObject("Text", typeof(RectTransform));
            var labelRect = (RectTransform)labelGo.transform;
            labelRect.SetParent(closeRect, worldPositionStays: false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = "X";
            label.fontSize = 12;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            GameStyle.Apply(label);

            var button = closeGo.GetComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(Hide);
        }

        /// <summary>Opens the quest's page on the official EFT wiki - the same Application.OpenURL
        /// call Raid Review itself uses for its own external link. URL pattern
        /// (https://escapefromtarkov.fandom.com/wiki/&lt;Name_With_Underscores&gt;) confirmed against
        /// real wiki pages, not guessed; a handful of quest names won't map exactly (disambiguation
        /// pages, punctuation) without a full id-to-slug table, which isn't worth building for this.</summary>
        private void BuildWikiButton(float height)
        {
            var buttonGo = new GameObject("WikiButton", typeof(RectTransform), typeof(Image), typeof(Button));
            var buttonRect = (RectTransform)buttonGo.transform;
            buttonRect.SetParent(_detailPanel, worldPositionStays: false);
            buttonRect.anchorMin = new Vector2(0f, 0f);
            buttonRect.anchorMax = new Vector2(1f, 0f);
            buttonRect.pivot = new Vector2(0.5f, 0f);
            buttonRect.anchoredPosition = new Vector2(0f, 12f);
            buttonRect.sizeDelta = new Vector2(-24f, height);
            _collapsible.Add(buttonGo);

            var background = buttonGo.GetComponent<Image>();
            background.color = new Color(0.2f, 0.35f, 0.45f, 0.9f);
            GameStyle.ApplyPanel(background);

            var labelGo = new GameObject("Text", typeof(RectTransform));
            var labelRect = (RectTransform)labelGo.transform;
            labelRect.SetParent(buttonRect, worldPositionStays: false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = "Open Wiki Page";
            label.fontSize = 12;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            GameStyle.Apply(label);

            var button = buttonGo.GetComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(() =>
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
            });
        }

        public void Show(QuestNode node)
        {
            _detailNode = node;

            // The lines themselves live in QuestSummary, shared with the Maps tab's quest list -
            // the two screens say the same things about a quest and only lay them out differently.
            var lines = QuestSummary.Lines(node, _graph, QuestDataClient.GetProfile());

            _detailText.text = string.Join("\n", lines.Where(l => l != null));
            _detailPanel.gameObject.SetActive(true);
        }

        public void Hide()
        {
            _detailNode = null;
            if (_detailPanel != null) _detailPanel.gameObject.SetActive(false);
        }

        /// <summary>Takes the panel off screen without forgetting which quest it was showing -
        /// exactly what switching to a Kappa/Settings tab did inline before this class existed.
        /// Kept distinct from <see cref="Hide"/>, which also clears the remembered node, so the
        /// split changes no behaviour.</summary>
        public void HideForTabSwitch()
        {
            if (_detailPanel != null) _detailPanel.gameObject.SetActive(false);
        }
    }
}
