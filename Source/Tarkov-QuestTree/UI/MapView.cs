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
    /// "I'm going to Customs - what can I do there?"
    ///
    /// A map picker down the left, the map image in the middle, and that map's outstanding quests on
    /// the right. The image comes from the DynamicMaps mod when it is installed (see
    /// <see cref="DynamicMapsLibrary"/>); without it, the quest list is shown on its own and nothing
    /// else changes.
    ///
    /// There are deliberately no quest pins. The game's quest data carries no coordinates - of
    /// roughly 10,600 objectives only 246 name a zone at all, and the rest say nothing beyond which
    /// map - so any pin would be invented. The map is here as orientation; the list is the content.
    ///
    /// Quests that can be done anywhere are left out entirely: listing them under every map would
    /// bury the ones that actually change what you do with the raid you are about to load into.
    /// </summary>
    internal static class MapView
    {
        private const string AnyLocation = "any";
        private const float PickerWidth = 150f;
        private const float MapSize = 420f;
        private const int MaxQuestRows = 40;

        /// <summary>Which map the view is showing. Static so it survives a re-render - switching map
        /// rebuilds the whole aux panel, and resetting to the first map each time would make the
        /// picker unusable.</summary>
        private static string _selectedLocationKey;

        public static float Build(RectTransform parent, QuestGraphBuilder graph, Action onRepaint)
        {
            var byMap = GroupByMap(graph);

            if (byMap.Count == 0)
            {
                var empty = AuxLayout.Padding;
                AuxLayout.AddHeading(parent, ref empty, "By map");
                AuxLayout.AddText(parent, ref empty,
                    "<color=#FFFFFF80>No unfinished quests are tied to a specific map.</color>", 24f, 12);
                return empty + AuxLayout.Padding;
            }

            // Ordered by how much is outstanding - the busiest map is usually the one worth loading.
            var ordered = byMap.OrderByDescending(m => m.Value.Count).ToList();

            if (_selectedLocationKey == null || !byMap.ContainsKey(_selectedLocationKey))
                _selectedLocationKey = ordered[0].Key;

            var pickerHeight = BuildPicker(parent, ordered, onRepaint);
            var selected = byMap[_selectedLocationKey];
            var contentHeight = BuildSelectedMap(parent, selected);

            return Mathf.Max(pickerHeight, contentHeight) + AuxLayout.Padding;
        }

        /// <summary>Quests grouped by map, keyed on the raw location id so it can be matched against
        /// DynamicMaps' internal names; the display name rides along on the nodes.</summary>
        private static Dictionary<string, List<QuestNode>> GroupByMap(QuestGraphBuilder graph)
        {
            var byMap = new Dictionary<string, List<QuestNode>>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in graph.Nodes)
            {
                if (node.Status == ENodeStatus.Completed) continue;

                var key = node.LocationKey;
                var display = node.LocationId;

                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(display)) continue;
                if (display.Equals(AnyLocation, StringComparison.OrdinalIgnoreCase)) continue;

                if (!byMap.TryGetValue(key, out var list))
                {
                    list = new List<QuestNode>();
                    byMap[key] = list;
                }

                list.Add(node);
            }

            return byMap;
        }

        private static float BuildPicker(
            RectTransform parent, List<KeyValuePair<string, List<QuestNode>>> maps, Action onRepaint)
        {
            var y = AuxLayout.Padding;

            foreach (var (key, quests) in maps)
            {
                var name = quests[0].LocationId;
                var actionable = quests.Count(q => q.Status == ENodeStatus.Active || q.Status == ENodeStatus.Available);
                var selected = key == _selectedLocationKey;

                var button = GameStyle.CreateButton(parent, $"{name}  {actionable}/{quests.Count}", () =>
                {
                    _selectedLocationKey = key;
                    onRepaint();
                });

                button.anchorMin = button.anchorMax = new Vector2(0f, 1f);
                button.pivot = new Vector2(0f, 1f);
                button.anchoredPosition = new Vector2(AuxLayout.Padding, -y);
                button.sizeDelta = new Vector2(PickerWidth, 26f);

                // Only the hand-built button has a background of ours to tint for the selected state.
                var background = button.GetComponent<Image>();
                if (background != null && selected) background.color = GameStyle.AccentColor;

                y += 30f;
            }

            return y;
        }

        private static float BuildSelectedMap(RectTransform parent, List<QuestNode> quests)
        {
            var y = AuxLayout.Padding;
            var left = AuxLayout.Padding + PickerWidth + 12f;

            var mapName = quests[0].LocationId;
            var entry = DynamicMapsLibrary.FindByLocationKey(quests[0].LocationKey);
            var sprite = entry?.GetSprite();

            var listLeft = left;

            if (sprite != null)
            {
                BuildMapImage(parent, sprite, left, y);
                AddCredit(parent, entry, left, y + MapSize + 4f);
                listLeft = left + MapSize + 16f;
            }

            var listY = y;
            AddAt(parent, $"<b>{mapName}</b>", listLeft, ref listY, 26f, 15);

            if (sprite == null && DynamicMapsLibrary.Available)
            {
                AddAt(parent, "<color=#FFFFFF60>No map image for this location.</color>",
                    listLeft, ref listY, 20f, 11);
            }
            else if (!DynamicMapsLibrary.Available)
            {
                AddAt(parent,
                    "<color=#FFFFFF60>Install the DynamicMaps mod to see map images here.</color>",
                    listLeft, ref listY, 20f, 11);
            }

            listY += 6f;

            // In progress first, then startable, then locked: the order you would act on them.
            foreach (var node in quests
                         .OrderBy(q => StatusRank(q.Status))
                         .ThenBy(q => q.TraderName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
                         .Take(MaxQuestRows))
            {
                var hex = ColorUtility.ToHtmlStringRGB(QuestNodeView.ColorFor(node.Status));

                AddAt(parent,
                    $"<color=#{hex}>{QuestNodeView.GlyphFor(node.Status)}</color>  {node.Name}" +
                    $"  <color=#FFFFFF60>{node.TraderName}</color>",
                    listLeft, ref listY, AuxLayout.RowHeight, 12);
            }

            if (quests.Count > MaxQuestRows)
            {
                AddAt(parent, $"<color=#FFFFFF60>+{quests.Count - MaxQuestRows} more</color>",
                    listLeft, ref listY, AuxLayout.RowHeight, 11);
            }

            return Mathf.Max(listY, sprite != null ? y + MapSize + 28f : listY);
        }

        private static void BuildMapImage(RectTransform parent, Sprite sprite, float x, float y)
        {
            var go = new GameObject("MapImage", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(MapSize, MapSize);

            var image = go.GetComponent<Image>();
            image.sprite = sprite;
            // Maps are not square; preserveAspect letterboxes rather than stretching the geometry.
            image.preserveAspect = true;
            image.raycastTarget = false;
        }

        /// <summary>The map's own author credit, shown because the images are someone else's work
        /// (tarkov.dev, via DynamicMaps) and their licence is only satisfied with attribution.</summary>
        private static void AddCredit(RectTransform parent, DynamicMapsLibrary.MapEntry entry, float x, float y)
        {
            if (string.IsNullOrEmpty(entry.Attribution)) return;

            var cursor = y;
            AddAt(parent, $"<color=#FFFFFF60>Map: {entry.Attribution}, via DynamicMaps</color>",
                x, ref cursor, 18f, 10);
        }

        /// <summary>Places a line at an explicit x, which AuxLayout's full-width rows cannot do -
        /// this view is the only one with side-by-side columns.</summary>
        private static void AddAt(
            RectTransform parent, string text, float x, ref float y, float height, int fontSize)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(-(x + AuxLayout.Padding), height);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Left;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            GameStyle.Apply(label);

            y += height;
        }

        private static int StatusRank(ENodeStatus status) => status switch
        {
            ENodeStatus.Active => 0,
            ENodeStatus.Available => 1,
            _ => 2
        };
    }
}
