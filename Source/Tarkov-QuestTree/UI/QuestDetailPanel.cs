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
        private QuestGraphBuilder _graph;
        private RectTransform _detailPanel;
        private TMP_Text _detailText;
        private QuestNode _detailNode;

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
            _detailPanel.sizeDelta = new Vector2(320f, -40f);
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

            BuildDetailCloseButton();
            BuildWikiButton(wikiButtonHeight);

            _detailPanel.gameObject.SetActive(false);
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

            var lines = new List<string>
            {
                $"<b>{node.Name}</b>",
                node.TraderName,
                node.Level > 0 ? $"Level {node.Level}" : null,
                node.IsKappaRequired ? "<color=#D9A61A>Kappa required</color>" : null,
                // Faction- and edition-locked quests are shown rather than hidden, so this is what
                // stops one reading as a bug in the tree.
                node.UnobtainableReason != null ? $"<color=#C86464>{node.UnobtainableReason}</color>" : null,
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
                    lines.Add(_graph.NodesById.TryGetValue(prereqId, out var prereq)
                        ? $"{prereq.Name} ({prereq.TraderName})"
                        : prereqId);
                }
                lines.Add("");
            }

            // Available for a locked quest too, not just an accepted one: the objective text
            // arrives with the companion mod's payload rather than being read off a live Quest
            // instance the game only creates once the quest is unlocked.
            var objectives = node.NecessaryObjectives.Select(o => o.Text).ToList();
            if (objectives.Count > 0)
            {
                lines.Add("<b>Objectives</b>");
                lines.AddRange(objectives);
                lines.Add("");
            }

            var rewards = node.Rewards.Select(FormatReward).Where(r => !string.IsNullOrEmpty(r)).ToList();
            if (rewards.Count > 0)
            {
                lines.Add("<b>Rewards</b>");
                lines.AddRange(rewards);
                lines.Add("");
            }

            if (node.Unlocks.Count > 0)
            {
                lines.Add("<b>Unlocks</b>");
                lines.AddRange(node.Unlocks.Select(u => u.TraderId == node.TraderId ? u.Name : $"{u.Name} ({u.TraderName})"));
            }

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

        /// <summary>Turns one payload reward into a display line. Trader-scoped rewards are named
        /// from the live session's trader list rather than from the payload, so a modded trader
        /// reads correctly without the server mod having to know about it.</summary>
        private string FormatReward(RewardDto reward)
        {
            if (reward == null) return null;

            var trader = !string.IsNullOrEmpty(reward.TraderId) &&
                         _graph.TraderNames.TryGetValue(reward.TraderId, out var traderName)
                ? traderName
                : reward.TraderId;

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
                    return string.IsNullOrEmpty(reward.Name) ? null : $"{reward.Type}: {reward.Name}";
            }
        }
    }
}
