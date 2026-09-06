using System;
using System.Collections.Generic;
using System.Linq;
using QuestTree.QuestGraph;
using TMPro;
using Unity.VectorGraphics;
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
        private const float PickerWidth = 300f;

        /// <summary>The quest list's column on the right. Everything left of it is map - the map is
        /// the thing you are here to read, and the list is the caption.</summary>
        private const float QuestListWidth = 400f;

        /// <summary>How tall the map viewport is. Fixed rather than filling the panel, because the
        /// aux surface is a scroll view whose height is not reliable at build time.</summary>
        private const float MapViewportHeight = 700f;

        private const int MaxQuestRows = 40;

        private const float MinZoom = 0.5f;
        private const float MaxZoom = 8f;
        private const float ZoomSpeed = 0.25f;

        /// <summary>Marker dot size in pixels, unscaled by zoom - so zooming in separates
        /// spawns that sit on top of each other when zoomed out.</summary>
        private const float MarkerSize = 9f;

        /// <summary>Which map the view is showing. Static so it survives a re-render - switching map
        /// rebuilds the whole aux panel, and resetting to the first map each time would make the
        /// picker unusable.</summary>
        private static string _selectedLocationKey;

        /// <summary>Whether the map dropdown is open. Static for the same reason as the selection:
        /// clicking the header rebuilds the view, so a local would close it again immediately.</summary>
        private static bool _pickerOpen;

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

            var selected = byMap[_selectedLocationKey];

            // The map and list are built first and the dropdown last, even though the dropdown sits
            // above them on screen. Unity UI draws siblings in order, so the open list can only
            // cover the map if it is created after it.
            var contentHeight = BuildSelectedMap(parent, selected, HeaderHeight);

            var labels = ordered.Select(LabelFor).ToList();
            var selectedIndex = ordered.FindIndex(m => m.Key == _selectedLocationKey);

            var popupBottom = AuxLayout.AddDropdown(
                parent, AuxLayout.Padding, labels, selectedIndex, _pickerOpen,
                toggleOpen: () =>
                {
                    _pickerOpen = !_pickerOpen;
                    onRepaint();
                },
                onSelect: index =>
                {
                    _selectedLocationKey = ordered[index].Key;
                    _pickerOpen = false;
                    onRepaint();
                },
                width: PickerWidth);

            // The open list is an overlay and so contributes no layout height of its own - but the
            // panel still has to be tall enough to scroll to the bottom of it.
            return Mathf.Max(contentHeight, popupBottom) + AuxLayout.Padding;
        }

        /// <summary>Map name plus what is outstanding on it, so the dropdown still carries the
        /// counts the old button column showed.</summary>
        private static string LabelFor(KeyValuePair<string, List<QuestNode>> map)
        {
            var quests = map.Value;
            var actionable = quests.Count(q => q.Status == ENodeStatus.Active || q.Status == ENodeStatus.Available);

            return $"{quests[0].LocationId}   {actionable}/{quests.Count}";
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

        /// <summary>Height of the dropdown header plus its breathing room - the map and list start
        /// below it, since the header is drawn separately and last.</summary>
        private const float HeaderHeight = AuxLayout.Padding + AuxLayout.DropdownHeight + 12f;

        private static float BuildSelectedMap(RectTransform parent, List<QuestNode> quests, float top)
        {
            var y = top;
            var left = AuxLayout.Padding;

            var mapName = quests[0].LocationId;
            var entry = DynamicMapsLibrary.FindByLocationKey(quests[0].LocationKey);
            var sprite = entry?.GetSprite();

            // Everything that is not the quest list is map. Measured off the panel rather than
            // fixed, so it fills an ultrawide the same way it fills 1080p.
            var available = parent.rect.width > 1f ? parent.rect.width : 1600f;
            var mapWidth = Mathf.Max(360f, available - QuestListWidth - AuxLayout.Padding * 3f);

            var listLeft = left;
            var mapBottom = y;

            if (sprite != null)
            {
                BuildMapViewport(parent, entry, sprite, left, y, mapWidth);
                mapBottom = y + MapViewportHeight;
                AddCredit(parent, entry, left, mapBottom + 4f);
                mapBottom += 22f;
                listLeft = left + mapWidth + AuxLayout.Padding;
            }

            var listY = y;
            AddAt(parent, $"<b>{mapName}</b>", listLeft, ref listY, 26f, 15, QuestListWidth);

            if (sprite == null && DynamicMapsLibrary.Available)
            {
                AddAt(parent, "<color=#FFFFFF60>No map image for this location.</color>",
                    listLeft, ref listY, 20f, 11, QuestListWidth);
            }
            else if (!DynamicMapsLibrary.Available)
            {
                AddAt(parent,
                    "<color=#FFFFFF60>Install the DynamicMaps mod to see map images here.</color>",
                    listLeft, ref listY, 20f, 11, QuestListWidth);
            }
            else
            {
                AddAt(parent, "<color=#FFFFFF60>Drag to pan, wheel to zoom</color>",
                    listLeft, ref listY, 20f, 11, QuestListWidth);
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
                    listLeft, ref listY, AuxLayout.RowHeight, 12, QuestListWidth);
            }

            if (quests.Count > MaxQuestRows)
            {
                AddAt(parent, $"<color=#FFFFFF60>+{quests.Count - MaxQuestRows} more</color>",
                    listLeft, ref listY, AuxLayout.RowHeight, 11, QuestListWidth);
            }

            return Mathf.Max(listY, mapBottom);
        }

        /// <summary>
        /// The map, in a viewport you can drag and zoom.
        ///
        /// Drawn with <see cref="SVGImage"/> rather than a plain <see cref="Image"/>, which is the
        /// difference between a map and a blank rectangle: VectorUtils.BuildSprite returns a sprite
        /// backed by tessellated geometry with no texture behind it, and Image draws a sprite by
        /// texturing a quad, so it drew nothing at all - while the attribution line beside it still
        /// appeared, making it look as though the image had merely failed to position. SVGImage is
        /// the renderer that package ships for these sprites, and being a Graphic it takes its
        /// material from the canvas rather than needing a vector shader found by name at runtime.
        ///
        /// The viewport carries <see cref="PanZoomHandler"/>, the same component the quest graph
        /// uses. Handling drag and scroll there is also what stops the surrounding aux ScrollRect
        /// stealing the gesture: Unity delivers to the first handler it finds walking up, so
        /// scrolling over the map zooms it instead of scrolling the panel.
        /// </summary>
        private static void BuildMapViewport(
            RectTransform parent, DynamicMapsLibrary.MapEntry entry, Sprite sprite,
            float x, float y, float width)
        {
            var viewportGo = new GameObject(
                "MapViewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            var viewport = (RectTransform)viewportGo.transform;
            viewport.SetParent(parent, worldPositionStays: false);
            viewport.anchorMin = viewport.anchorMax = new Vector2(0f, 1f);
            viewport.pivot = new Vector2(0f, 1f);
            viewport.anchoredPosition = new Vector2(x, -y);
            viewport.sizeDelta = new Vector2(width, MapViewportHeight);

            // A visible backing plate, which also gives the viewport a raycast target - without a
            // Graphic here the drag and scroll handlers would never receive anything.
            var backing = viewportGo.GetComponent<Image>();
            backing.color = new Color(0f, 0f, 0f, 0.25f);
            GameStyle.ApplyPanel(backing);

            // The content rect is built to the map's own aspect rather than the viewport's, and
            // preserveAspect is then off. That is what makes markers land in the right place: with
            // letterboxing the drawn rectangle is smaller than the rect by bars of unknown size,
            // and every marker would be off by them. Sized to the aspect, picture and rect coincide.
            var aspect = entry != null && entry.HasBounds ? entry.AspectRatio : 1f;

            var contentWidth = width;
            var contentHeight = width / Mathf.Max(0.01f, aspect);

            if (contentHeight > MapViewportHeight)
            {
                contentHeight = MapViewportHeight;
                contentWidth = contentHeight * aspect;
            }

            var contentGo = new GameObject("MapContent", typeof(RectTransform), typeof(SVGImage));
            var content = (RectTransform)contentGo.transform;
            content.SetParent(viewport, worldPositionStays: false);
            content.anchorMin = content.anchorMax = new Vector2(0f, 1f);
            content.pivot = new Vector2(0f, 1f);
            content.anchoredPosition = new Vector2(
                (width - contentWidth) * 0.5f, -(MapViewportHeight - contentHeight) * 0.5f);
            content.sizeDelta = new Vector2(contentWidth, contentHeight);

            var image = contentGo.GetComponent<SVGImage>();
            image.sprite = sprite;
            image.preserveAspect = false;
            image.raycastTarget = false;

            BuildMarkers(content, entry, contentWidth, contentHeight);

            viewportGo.AddComponent<PanZoomHandler>().Init(content, MinZoom, MaxZoom, ZoomSpeed);
        }

        /// <summary>
        /// Pins where the items your quests want actually spawn.
        ///
        /// The positions come from the server's map-marker route, which reads them out of each
        /// map's forced loot spawns - see MapMarkerPayloadBuilder for why that is the only honest
        /// source. They are parented to the map content, so they pan and zoom with it and stay put
        /// relative to the ground underneath.
        ///
        /// Markers are drawn at a fixed pixel size and are NOT counter-scaled as you zoom, so
        /// zooming in genuinely separates two spawns that overlap when zoomed out.
        /// </summary>
        private static void BuildMarkers(
            RectTransform content, DynamicMapsLibrary.MapEntry entry, float width, float height)
        {
            if (entry == null || !entry.HasBounds) return;

            var payload = QuestDataClient.GetMapMarkers();
            if (payload?.Maps == null) return;

            var set = payload.Maps.FirstOrDefault(m =>
                m != null && string.Equals(m.LocationKey, MatchedKey(entry), StringComparison.OrdinalIgnoreCase));

            if (set?.Markers == null) return;

            foreach (var marker in set.Markers)
            {
                if (marker == null) continue;

                var normalized = entry.Normalize(marker.X, marker.Z);

                // Off the edge of the picture means the marker is not describable on this image;
                // clamping it to the border would be a confident lie about where the item is.
                if (normalized.x < 0f || normalized.x > 1f || normalized.y < 0f || normalized.y > 1f)
                    continue;

                var go = new GameObject("QuestMarker", typeof(RectTransform), typeof(Image));
                var rect = (RectTransform)go.transform;
                rect.SetParent(content, worldPositionStays: false);
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 0f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(MarkerSize, MarkerSize);
                rect.anchoredPosition = new Vector2(normalized.x * width, normalized.y * height);

                var dot = go.GetComponent<Image>();
                dot.color = GameStyle.AccentColor;
                dot.raycastTarget = false;

                // The item and the quests that want it, as a label beside the dot. No hover: the
                // aux panel has no tooltip layer, and a marker you have to hover to identify is no
                // better than no marker when you are deciding which one to walk to.
                var labelGo = new GameObject("Label", typeof(RectTransform));
                var labelRect = (RectTransform)labelGo.transform;
                labelRect.SetParent(rect, worldPositionStays: false);
                labelRect.anchorMin = labelRect.anchorMax = new Vector2(0.5f, 0.5f);
                labelRect.pivot = new Vector2(0f, 0.5f);
                labelRect.anchoredPosition = new Vector2(MarkerSize, 0f);
                labelRect.sizeDelta = new Vector2(190f, 16f);

                var label = labelGo.AddComponent<TextMeshProUGUI>();
                label.text = marker.ItemName;
                label.fontSize = 10;
                label.color = GameStyle.AccentColor;
                label.alignment = TextAlignmentOptions.Left;
                label.enableWordWrapping = false;
                label.overflowMode = TextOverflowModes.Ellipsis;
                label.raycastTarget = false;
                GameStyle.ApplyOutlined(label);
            }
        }

        /// <summary>The internal name the server keyed this map's markers under. A DynamicMaps entry
        /// can cover several (Factory is day and night), and the marker set is per game location, so
        /// the first of its names that the payload actually has is the right one.</summary>
        private static string MatchedKey(DynamicMapsLibrary.MapEntry entry)
        {
            var payload = QuestDataClient.GetMapMarkers();
            if (payload?.Maps == null) return entry.InternalNames.FirstOrDefault() ?? "";

            foreach (var name in entry.InternalNames)
            {
                if (payload.Maps.Any(m =>
                        m != null && string.Equals(m.LocationKey, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return name;
                }
            }

            return entry.InternalNames.FirstOrDefault() ?? "";
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
            RectTransform parent, string text, float x, ref float y, float height, int fontSize,
            float fixedWidth = 0f)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.pivot = new Vector2(0f, 1f);

            if (fixedWidth > 0f)
            {
                // Pinned to the top-left corner at a set width, so a long quest name is ellipsised
                // inside the list column instead of stretching to the panel edge.
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                rect.sizeDelta = new Vector2(fixedWidth, height);
            }
            else
            {
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.sizeDelta = new Vector2(-(x + AuxLayout.Padding), height);
            }

            rect.anchoredPosition = new Vector2(x, -y);

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
