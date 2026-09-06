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
    /// A map picker and a floor picker along the top, the map itself filling most of the width, and
    /// that map's outstanding quests down the right. The pictures come from the DynamicMaps mod when
    /// it is installed (see <see cref="DynamicMapsLibrary"/>); without it the quest list is shown on
    /// its own and nothing else changes.
    ///
    /// The map is drawn in MAP SPACE: the container's local units are the map's own coordinates, the
    /// picture is a rect sized to the floor's ImageBounds, and a marker is placed at its raw
    /// coordinates with no arithmetic in between. That is how DynamicMaps does it, and it is the
    /// third attempt here - the two before both normalised positions into fractions of a rect, and
    /// both were subtly wrong in different ways. There is now no conversion left to get wrong.
    ///
    /// Markers are quest ITEM spawns, from the server's map-marker route, which reads them out of
    /// each map's forced loot spawns. Objectives themselves carry no coordinates at all, so nothing
    /// else can be pinned honestly.
    ///
    /// Quests that can be done anywhere are left out entirely: listing them under every map would
    /// bury the ones that actually change what you do with the raid you are about to load into.
    /// </summary>
    internal static class MapView
    {
        private const string AnyLocation = "any";
        private const float PickerWidth = 300f;
        private const float FloorPickerWidth = 190f;

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

        /// <summary>Marker dot size in screen pixels. Markers counter-scale against the map's zoom so
        /// this stays constant, which is what lets zooming separate spawns that overlap when zoomed
        /// out instead of magnifying the whole pile.</summary>
        private const float MarkerSize = 14f;

        /// <summary>Roughly how much screen space a marker's name takes. Used to keep names from
        /// stacking into an unreadable block where several spawns sit close together.</summary>
        private const float LabelWidth = 150f;
        private const float LabelHeight = 15f;

        /// <summary>Which map the view is showing. Static so it survives a re-render - switching map
        /// rebuilds the whole aux panel, and resetting to the first map each time would make the
        /// picker unusable.</summary>
        private static string _selectedLocationKey;

        /// <summary>Which floor, by its level number. Static for the same reason, and by level rather
        /// than by index so it carries across maps that have the same floor.</summary>
        private static int? _selectedLevel;

        private static bool _pickerOpen;
        private static bool _floorPickerOpen;

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
            var entry = DynamicMapsLibrary.FindByLocationKey(selected[0].LocationKey);
            var layer = ResolveLayer(entry);

            // The map and list are built first and the dropdowns last, even though the dropdowns sit
            // above them on screen. Unity UI draws siblings in order, so an open list can only cover
            // the map if it is created after it.
            var contentHeight = BuildSelectedMap(parent, selected, entry, layer, HeaderHeight);

            var labels = ordered.Select(LabelFor).ToList();
            var selectedIndex = ordered.FindIndex(m => m.Key == _selectedLocationKey);

            var popupBottom = AuxLayout.AddDropdown(
                parent, AuxLayout.Padding, labels, selectedIndex, _pickerOpen,
                toggleOpen: () =>
                {
                    _pickerOpen = !_pickerOpen;
                    _floorPickerOpen = false;
                    onRepaint();
                },
                onSelect: index =>
                {
                    _selectedLocationKey = ordered[index].Key;
                    _pickerOpen = false;
                    onRepaint();
                },
                width: PickerWidth);

            popupBottom = Mathf.Max(popupBottom, BuildFloorPicker(parent, entry, layer, onRepaint));

            // An open list is an overlay and so contributes no layout height of its own - but the
            // panel still has to be tall enough to scroll to the bottom of it.
            return Mathf.Max(contentHeight, popupBottom) + AuxLayout.Padding;
        }

        /// <summary>The floor dropdown, beside the map one. Absent for a map with a single floor,
        /// which is most of them - a picker with one entry is furniture.</summary>
        private static float BuildFloorPicker(
            RectTransform parent, DynamicMapsLibrary.MapEntry entry,
            DynamicMapsLibrary.MapLayer layer, Action onRepaint)
        {
            if (entry == null || entry.Layers.Count < 2) return 0f;

            var names = entry.Layers.Select(l => l.Name).ToList();
            var index = entry.Layers.IndexOf(layer);

            return AuxLayout.AddDropdown(
                parent, AuxLayout.Padding, names, index, _floorPickerOpen,
                toggleOpen: () =>
                {
                    _floorPickerOpen = !_floorPickerOpen;
                    _pickerOpen = false;
                    onRepaint();
                },
                onSelect: chosen =>
                {
                    _selectedLevel = entry.Layers[chosen].Level;
                    _floorPickerOpen = false;
                    onRepaint();
                },
                width: FloorPickerWidth,
                x: AuxLayout.Padding + PickerWidth + 10f);
        }

        /// <summary>The floor to show: the one last chosen if this map has it, else the map's own
        /// default. Falling back matters because floor levels are not shared between maps.</summary>
        private static DynamicMapsLibrary.MapLayer ResolveLayer(DynamicMapsLibrary.MapEntry entry)
        {
            if (entry == null || entry.Layers.Count == 0) return null;

            if (_selectedLevel.HasValue)
            {
                var match = entry.Layers.FirstOrDefault(l => l.Level == _selectedLevel.Value);
                if (match != null) return match;
            }

            return entry.DefaultLayer;
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

        /// <summary>Height of the dropdown row plus its breathing room - the map and list start below
        /// it, since the dropdowns are drawn separately and last.</summary>
        private const float HeaderHeight = AuxLayout.Padding + AuxLayout.DropdownHeight + 12f;

        private static float BuildSelectedMap(
            RectTransform parent, List<QuestNode> quests, DynamicMapsLibrary.MapEntry entry,
            DynamicMapsLibrary.MapLayer layer, float top)
        {
            var y = top;
            var left = AuxLayout.Padding;

            var mapName = quests[0].LocationId;
            var sprite = layer?.GetSprite();

            // Everything that is not the quest list is map. Measured off the panel rather than
            // fixed, so it fills an ultrawide the same way it fills 1080p.
            var available = parent.rect.width > 1f ? parent.rect.width : 1600f;
            var mapWidth = Mathf.Max(360f, available - QuestListWidth - AuxLayout.Padding * 3f);

            var listLeft = left;
            var mapBottom = y;

            if (sprite != null)
            {
                BuildMapViewport(parent, entry, layer, sprite, left, y, mapWidth);
                mapBottom = y + MapViewportHeight;
                AddCredit(parent, entry, left, mapBottom + 4f);
                mapBottom += 22f;
                listLeft = left + mapWidth + AuxLayout.Padding;
            }

            // A card behind the list, so the text reads as a column rather than as words floating
            // on the map's own background.
            if (sprite != null)
                AddCard(parent, listLeft - 10f, y - 8f, QuestListWidth + 20f, MapViewportHeight + 8f);

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
                var spawns = MarkerCountFor(entry);
                var floors = entry != null && entry.Layers.Count > 1
                    ? $"  ·  {entry.Layers.Count} floors"
                    : "";

                AddAt(parent,
                    $"<color=#FFFFFF60>Drag to pan, wheel to zoom{floors}</color>",
                    listLeft, ref listY, 18f, 11, QuestListWidth);

                if (spawns > 0)
                {
                    AddAt(parent,
                        $"<color=#FFFFFF60>{spawns} quest item spawn{(spawns == 1 ? "" : "s")} marked</color>",
                        listLeft, ref listY, 18f, 11, QuestListWidth);
                }
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
        /// Inside the viewport is a container whose local units ARE the map's coordinates. The
        /// picture is a rect sized to the floor's ImageBounds and centred on its midpoint, and
        /// everything drawn on top is placed at its raw map coordinates. That is lifted from how
        /// DynamicMaps builds its own layers, and it is deliberate: it removes the coordinate
        /// conversion that two previous attempts each got wrong in a different way. Fitting the map
        /// to the viewport is then one localScale on the container, which cannot desynchronise the
        /// picture from what is drawn on it because it moves both.
        ///
        /// Drawn with <see cref="SVGImage"/> rather than a plain <see cref="Image"/>: BuildSprite
        /// returns a sprite backed by tessellated geometry with no texture behind it, and Image
        /// draws a sprite by texturing a quad, so it drew nothing at all.
        ///
        /// The viewport carries <see cref="PanZoomHandler"/>, the same component the quest graph
        /// uses. Handling drag and scroll there is also what stops the surrounding aux ScrollRect
        /// stealing the gesture: Unity delivers to the first handler it finds walking up.
        /// </summary>
        private static void BuildMapViewport(
            RectTransform parent, DynamicMapsLibrary.MapEntry entry, DynamicMapsLibrary.MapLayer layer,
            Sprite sprite, float x, float y, float width)
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

            var bounds = layer.BoundsSize;

            // Map space: pivoted and anchored at the viewport's centre, so a child at map (0,0) is
            // at the middle and the picture's own offset from the origin is preserved.
            var spaceGo = new GameObject("MapSpace", typeof(RectTransform));
            var space = (RectTransform)spaceGo.transform;
            space.SetParent(viewport, worldPositionStays: false);
            space.anchorMin = space.anchorMax = new Vector2(0.5f, 0.5f);
            space.pivot = new Vector2(0.5f, 0.5f);
            space.sizeDelta = bounds;

            // Fit the floor into the viewport, then shift so the bounds' centre sits in the middle.
            var fit = Mathf.Min(width / bounds.x, MapViewportHeight / bounds.y);
            space.localScale = new Vector3(fit, fit, 1f);
            space.anchoredPosition = -layer.BoundsCentre * fit;

            var imageGo = new GameObject("MapImage", typeof(RectTransform), typeof(SVGImage));
            var image = (RectTransform)imageGo.transform;
            image.SetParent(space, worldPositionStays: false);
            image.anchorMin = image.anchorMax = new Vector2(0.5f, 0.5f);
            image.pivot = new Vector2(0.5f, 0.5f);
            image.sizeDelta = bounds;
            image.anchoredPosition = layer.BoundsCentre;

            var svg = imageGo.GetComponent<SVGImage>();
            svg.sprite = sprite;
            svg.preserveAspect = false;
            svg.raycastTarget = false;

            // The handler is created before the overlays because they register with it to be held
            // at a constant on-screen size. Zoom limits and step are scaled by the fit, since the
            // container already sits at that scale.
            var panZoom = viewportGo.AddComponent<PanZoomHandler>();
            panZoom.Init(space, MinZoom * fit, MaxZoom * fit, ZoomSpeed * fit);

            BuildPlaceLabels(space, entry, panZoom);
            BuildMarkers(space, entry, layer, panZoom);
        }

        /// <summary>
        /// The map's own place names, which the config carries and the SVGs do not - the pictures
        /// contain no text whatsoever, so without these the map is unlabelled.
        ///
        /// They also calibrate everything else. Names and quest markers go through the same
        /// placement, so if "Dorms" sits on the dorms then the markers are right too; if it does not,
        /// the error is visible and measurable rather than something to reason about.
        /// </summary>
        private static void BuildPlaceLabels(
            RectTransform space, DynamicMapsLibrary.MapEntry entry, PanZoomHandler panZoom)
        {
            if (entry == null) return;

            foreach (var label in entry.Labels)
            {
                var go = new GameObject("PlaceLabel", typeof(RectTransform));
                var rect = (RectTransform)go.transform;
                rect.SetParent(space, worldPositionStays: false);
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = label.Position;
                rect.sizeDelta = new Vector2(160f, 18f);
                panZoom.KeepConstantScale(rect);

                var text = go.AddComponent<TextMeshProUGUI>();
                text.text = label.Text;
                text.fontSize = 12;
                text.color = new Color(1f, 1f, 1f, 0.65f);
                text.alignment = TextAlignmentOptions.Center;
                text.enableWordWrapping = false;
                text.raycastTarget = false;
                GameStyle.ApplyOutlined(text);
            }
        }

        /// <summary>
        /// Pins where the items your quests want actually spawn.
        ///
        /// Positions come from the server's map-marker route, which reads them out of each map's
        /// forced loot spawns - see MapMarkerPayloadBuilder for why that is the only honest source.
        ///
        /// A marker on another floor is dimmed rather than hidden, and keeps its name, so an item
        /// never silently vanishes just because you are looking at the wrong level - you can see it
        /// is there and which floor to switch to.
        /// </summary>
        private static void BuildMarkers(
            RectTransform space, DynamicMapsLibrary.MapEntry entry,
            DynamicMapsLibrary.MapLayer layer, PanZoomHandler panZoom)
        {
            if (entry == null) return;

            var payload = QuestDataClient.GetMapMarkers();
            if (payload?.Maps == null) return;

            var set = payload.Maps.FirstOrDefault(m =>
                m?.LocationKey != null &&
                entry.InternalNames.Any(n => string.Equals(n, m.LocationKey, StringComparison.OrdinalIgnoreCase)));

            if (set?.Markers == null) return;

            // Markers on the floor being shown are placed first, so that when two names would
            // collide it is the one you can actually walk to that keeps its label.
            var ordered = set.Markers
                .Where(m => m != null)
                .Select(m => (Marker: m, Owner: entry.LayerFor(m.X, m.Z, m.Y)))
                .OrderBy(m => m.Owner == null || m.Owner == layer ? 0 : 1)
                .ToList();

            // Label footprints already claimed, in map units. Names are held at a constant screen
            // size, so their size in map units is the screen size divided by the current fit.
            var claimed = new List<Rect>();
            var scale = space.localScale.x;
            var labelSpan = new Vector2(LabelWidth, LabelHeight) / Mathf.Max(0.0001f, scale);

            foreach (var (marker, owner) in ordered)
            {
                // A marker whose height matches no floor at all is treated as belonging to the one
                // being shown rather than dropped: the bands do not tile the world exhaustively, and
                // a real spawn is worth more than a tidy rule.
                var onThisFloor = owner == null || owner == layer;

                var colour = onThisFloor
                    ? GameStyle.AccentColor
                    : new Color(GameStyle.AccentColor.r, GameStyle.AccentColor.g, GameStyle.AccentColor.b, 0.4f);

                // Map space is (game.x, game.z) with game.y as the height - see DynamicMapsLibrary.
                var position = new Vector2(marker.X, marker.Z);

                var go = new GameObject("QuestMarker", typeof(RectTransform));
                var rect = (RectTransform)go.transform;
                rect.SetParent(space, worldPositionStays: false);
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = position;

                // Held at a constant on-screen size however far the map is zoomed - otherwise
                // zooming in magnifies the pile instead of separating it.
                rect.sizeDelta = new Vector2(MarkerSize, MarkerSize);
                panZoom.KeepConstantScale(rect);

                // A glyph rather than an Image: it is round, and it takes the same outline the rest
                // of the map text uses, which is what makes it readable over both the pale buildings
                // and the dark ground.
                var dot = go.AddComponent<TextMeshProUGUI>();
                dot.text = onThisFloor ? "\u25CF" : "\u25CB";
                dot.fontSize = MarkerSize;
                dot.color = colour;
                dot.alignment = TextAlignmentOptions.Center;
                dot.enableWordWrapping = false;
                dot.raycastTarget = false;
                GameStyle.ApplyOutlined(dot);

                // The name is dropped where it would land on one already placed. The dot always
                // stays, so nothing is hidden - a cluster reads as several spawns with one name
                // rather than as a block of overlapping text, which is what it did before.
                var footprint = new Rect(
                    position.x + labelSpan.x * 0.1f, position.y - labelSpan.y * 0.5f,
                    labelSpan.x, labelSpan.y);

                if (claimed.Any(other => other.Overlaps(footprint))) continue;
                claimed.Add(footprint);

                var labelGo = new GameObject("Label", typeof(RectTransform));
                var labelRect = (RectTransform)labelGo.transform;
                labelRect.SetParent(rect, worldPositionStays: false);
                labelRect.anchorMin = labelRect.anchorMax = new Vector2(0.5f, 0.5f);
                labelRect.pivot = new Vector2(0f, 0.5f);
                labelRect.anchoredPosition = new Vector2(MarkerSize * 0.6f, 0f);
                labelRect.sizeDelta = new Vector2(LabelWidth, LabelHeight);

                var label = labelGo.AddComponent<TextMeshProUGUI>();
                label.text = onThisFloor || owner == null
                    ? marker.ItemName
                    : $"{marker.ItemName}  <color=#FFFFFF50>({owner.Name})</color>";
                label.fontSize = 10;
                label.color = colour;
                label.alignment = TextAlignmentOptions.Left;
                label.enableWordWrapping = false;
                label.overflowMode = TextOverflowModes.Ellipsis;
                label.raycastTarget = false;
                GameStyle.ApplyOutlined(label);
            }
        }

        /// <summary>How many item spawns this map has markers for, for the line under the heading.</summary>
        private static int MarkerCountFor(DynamicMapsLibrary.MapEntry entry)
        {
            if (entry == null) return 0;

            var payload = QuestDataClient.GetMapMarkers();
            if (payload?.Maps == null) return 0;

            var set = payload.Maps.FirstOrDefault(m =>
                m?.LocationKey != null &&
                entry.InternalNames.Any(n => string.Equals(n, m.LocationKey, StringComparison.OrdinalIgnoreCase)));

            return set?.Markers?.Count ?? 0;
        }

        /// <summary>A panel behind a column, so its text reads as a block rather than as words lying
        /// loose on whatever is behind them.</summary>
        private static void AddCard(RectTransform parent, float x, float y, float width, float height)
        {
            var go = new GameObject("Card", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);

            var image = go.GetComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.04f);
            image.raycastTarget = false;
            GameStyle.ApplyPanel(image);
        }

        /// <summary>The map's own author credit, shown because the images are someone else's work
        /// (tarkov.dev, via DynamicMaps) and their licence is only satisfied with attribution.</summary>
        private static void AddCredit(RectTransform parent, DynamicMapsLibrary.MapEntry entry, float x, float y)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Attribution)) return;

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
