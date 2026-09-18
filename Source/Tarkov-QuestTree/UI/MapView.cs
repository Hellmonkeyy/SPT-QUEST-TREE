using System;
using System.Collections.Generic;
using System.Linq;
using EFT.UI;
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
        // The map reads settings on every build, so the Ready guard the other views carry lives
        // here once instead of at each read. Each fallback is the entry's own default.
        private static bool StartedOnly => ModSettings.Ready && ModSettings.MarkStartedOnly.Value;
        private static bool ShowGuides => ModSettings.Ready && ModSettings.ShowMapGuides.Value;
        private static bool MirrorArtwork => ModSettings.Ready && ModSettings.MirrorMapArtwork.Value;
        private static int ArtworkRotation => ModSettings.Ready ? ModSettings.MapArtworkRotation.Value : 0;

        private const string AnyLocation = "any";
        private const float PickerWidth = 300f;
        private const float FloorPickerWidth = 190f;

        /// <summary>The quest list's column on the right. Everything left of it is map - the map is
        /// the thing you are here to read, and the list is the caption.</summary>

        /// <summary>The column beside the map: its own scroll view, since the map is fixed and the
        /// list is not. Everything that is not the sidebar is map. Width from Settings.</summary>
        private static float SidebarWidth => ModSettings.Ready ? ModSettings.SidebarWidth.Value : 440f;
        private const float SidebarInset = 12f;

        /// <summary>The map's own control row - pickers, the accepted-only toggle, coverage -
        /// directly under the toolbar.</summary>
        private const float ControlRowHeight = AuxLayout.Padding + AuxLayout.DropdownHeight + 10f;

        /// <summary>How many "do next" rows the sidebar shows before the full list takes over.</summary>
        private static int MaxDoNextRows => ModSettings.Ready ? ModSettings.DoNextRows.Value : 8;

        private const string ItemKind = "item";


        private const int MaxQuestRows = 40;

        private const float MinZoom = 0.5f;
        private const float MaxZoom = 8f;
        private const float ZoomSpeed = 0.25f;

        /// <summary>Marker dot size in screen pixels. Markers counter-scale against the map's zoom so
        /// this stays constant, which is what lets zooming separate spawns that overlap when zoomed
        /// out instead of magnifying the whole pile.</summary>
        private const float MarkerSize = 14f;

        /// <summary>Height of a quest pin in screen pixels. Taller than a dot because it is read as
        /// a shape rather than a point.</summary>
        private const float PinSize = 26f;

        /// <summary>Roughly how much screen space a marker's name takes. Used to keep names from
        /// stacking into an unreadable block where several spawns sit close together.</summary>
        private const float LabelWidth = 260f;
        private const float LabelHeight = 18f;

        /// <summary>Marker kinds as the server tags them.</summary>
        private const string ObjectiveKind = "objective";

        /// <summary>A marker whose quests the graph does not hold, so no status can honestly be
        /// claimed for it. Neutral grey rather than a guessed status colour. Status colours
        /// themselves come from <see cref="QuestNodeView.ColorFor"/>, shared with the tree.
        /// Fully opaque like them: the "not started" dimming is applied to every pin alike where
        /// they are drawn, and baking a second helping in here made these near invisible.</summary>
        private static readonly Color UnknownMarkerColor = new(0.62f, 0.62f, 0.60f, 1f);

        /// <summary>Pan and zoom, kept across the rebuild that every dropdown click causes. Switching
        /// floor used to throw them away, which is useless when the whole point of switching floors
        /// is to look at the same place one storey up. Reset when the MAP changes, since a different
        /// map is a different coordinate space and the old view means nothing in it.</summary>
        private static string _viewStateKey;
        private static float _savedScale;
        private static Vector2 _savedPan;

        /// <summary>Which map the view is showing. Static so it survives a re-render - switching map
        /// rebuilds the whole aux panel, and resetting to the first map each time would make the
        /// picker unusable.</summary>
        private static string _selectedLocationKey;

        /// <summary>Which floor, by its level number. Static for the same reason, and by level rather
        /// than by index so it carries across maps that have the same floor.</summary>
        private static int? _selectedLevel;

        private static bool _pickerOpen;
        private static bool _floorPickerOpen;

        /// <summary>The quest whose row is expanded in the list, or null. Static for the same reason
        /// as the pickers above: clicking a row repaints the whole aux panel, so anything that has
        /// to outlive the click cannot be a local.</summary>
        private static string _selectedQuestId;

        /// <summary>Set alongside <see cref="_selectedQuestId"/> when the click should also move the
        /// map, and cleared by the render that acts on it. Separate from the selection because
        /// re-rendering for any other reason - a floor change, a settings change - must not yank the
        /// view back to the pin the user has since panned away from.</summary>
        private static string _pendingFocusQuestId;

        /// <summary>How far in to zoom when flying to a quest's pin, as a multiple of the zoom that
        /// fits the whole floor. Close enough to read the surroundings, still short of
        /// <see cref="MaxZoom"/> so there is room to zoom further by hand.</summary>
        private const float FocusZoom = 2f;

        /// <summary>A quest whose row should be scrolled into view once the panel has been laid out,
        /// and the y the row was actually placed at. Needed because the aux panel rewinds itself to
        /// the top on every repaint, so clicking a pin for a quest far down the list would otherwise
        /// expand it off screen.</summary>
        private static string _pendingScrollQuestId;
        private static float? _pendingScrollY;

        /// <summary>One sentence for the next sidebar about something the map could not do - a
        /// quest with no pin to fly to, a pin whose quest has no row. Drawn once and forgotten,
        /// so a later repaint for any other reason does not keep repeating it.</summary>
        private static string _notice;

        /// <summary>Maps that load the same scene, in either direction. Mirrors ZoneStore.Aliases
        /// on the server, and is only consulted when DynamicMaps is not installed to answer the
        /// same question from its own MapInternalNames.</summary>
        private static readonly (string A, string B)[] SceneAliases =
        {
            ("factory4_night", "factory4_day"),
            ("Sandbox_high", "Sandbox")
        };

        /// <summary>The map key the matchmaker's pick last resolved to, or null when it resolved to
        /// nothing (no map picked, or a map with nothing to do on it). Static like the selection:
        /// the sidebar says "your next raid" from it on every rebuild.</summary>
        private static string _raidLocationKey;

        /// <summary>True while the map on screen is the one the player is about to load into.</summary>
        public static bool IsShowingRaidMap =>
            _raidLocationKey != null &&
            string.Equals(_selectedLocationKey, _raidLocationKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>Points the view at the map the matchmaker has picked, by the location's internal
        /// name. Returns false, and leaves the selection alone, when no map with outstanding quests
        /// answers to that name - the caller decides whether that is worth trying again later. The
        /// selected quest is dropped with the map, as it is on a manual map change.</summary>
        public static bool PreselectLocation(string internalName, QuestGraphBuilder graph)
        {
            if (string.IsNullOrEmpty(internalName) || graph == null) return false;

            var key = ResolveMapKey(internalName, GroupByMap(graph).Keys);
            _raidLocationKey = key;
            if (key == null) return false;

            _selectedLocationKey = key;
            _selectedQuestId = null;
            _pendingFocusQuestId = null;
            _pendingScrollQuestId = null;
            _pickerOpen = false;
            _floorPickerOpen = false;
            return true;
        }

        /// <summary>One key per scene, folding the aliased pairs together.
        ///
        /// Sandbox and Sandbox_high are both "Ground Zero" and factory4_day and factory4_night are
        /// both "Factory" - separate location ids, one place as far as a player is concerned. The
        /// map picker listing each twice, with the quests divided between them, is not a view of
        /// anything real.</summary>
        private static string CanonicalMapKey(string key)
        {
            foreach (var alias in SceneAliases)
                if (string.Equals(key, alias.A, StringComparison.OrdinalIgnoreCase)) return alias.B;

            return key;
        }

        /// <summary>The map key for a location's internal name: the name itself when the quest data
        /// keys a map by it, else a key that DynamicMaps lists as the same map (Factory has a day
        /// and a night id, Ground Zero a low- and a high-level one), else the same pairs from
        /// <see cref="SceneAliases"/> for an install without DynamicMaps.</summary>
        private static string ResolveMapKey(string internalName, IEnumerable<string> keys)
        {
            var known = keys.ToList();

            string Find(string name) =>
                known.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

            var exact = Find(internalName);
            if (exact != null) return exact;

            var entry = DynamicMapsLibrary.FindByLocationKey(internalName);
            if (entry != null)
            {
                foreach (var name in entry.InternalNames)
                {
                    var shared = Find(name);
                    if (shared != null) return shared;
                }
            }

            foreach (var (a, b) in SceneAliases)
            {
                var other = string.Equals(a, internalName, StringComparison.OrdinalIgnoreCase) ? b
                    : string.Equals(b, internalName, StringComparison.OrdinalIgnoreCase) ? a
                    : null;
                if (other == null) continue;

                var aliased = Find(other);
                if (aliased != null) return aliased;
            }

            return null;
        }

        /// <summary>Forgets everything this view remembers - map, floor, selection, pan and zoom,
        /// notices, the raid cue - for a different profile. The statics exist to survive the aux
        /// rebuild, not a change of character; a new profile used to inherit the last one's
        /// selected quest and saved view until it clicked past them.</summary>
        public static void ResetSession()
        {
            _viewStateKey = null;
            _savedScale = 0f;
            _savedPan = Vector2.zero;
            _selectedLocationKey = null;
            _selectedLevel = null;
            _pickerOpen = false;
            _floorPickerOpen = false;
            _selectedQuestId = null;
            _pendingFocusQuestId = null;
            _pendingScrollQuestId = null;
            _pendingScrollY = null;
            _notice = null;
            _raidLocationKey = null;
            _setPayload = null;
            _setEntry = null;
            _set = null;
        }

        /// <summary>Forgets the saved pan and zoom, so the next build fits the floor the way it
        /// was first shown - the F key on the map, matching F on the tree. A pending fly-to is
        /// dropped with it: fitting and then flying would undo the fit.</summary>
        public static void ResetView()
        {
            _viewStateKey = null;
            _pendingFocusQuestId = null;
        }

        /// <summary>Moves one floor up (+1) or down (-1) from the one showing. False at either end
        /// and on a map with one floor, so the caller knows whether there is anything to redraw.</summary>
        public static bool StepFloor(int direction)
        {
            var entry = DynamicMapsLibrary.FindByLocationKey(_selectedLocationKey);
            if (entry == null || entry.Layers.Count < 2) return false;

            // By level, not config order: "up" has to mean the storey above.
            var floors = entry.Layers.OrderBy(l => l.Level).ToList();
            var index = floors.IndexOf(ResolveLayer(entry));
            if (index < 0) return false;

            var next = index + direction;
            if (next < 0 || next >= floors.Count) return false;

            _selectedLevel = floors[next].Level;
            _pickerOpen = false;
            _floorPickerOpen = false;
            return true;
        }

        /// <summary>The map "show this quest" should open, or null when the quest is on none.
        ///
        /// A derived quest declares "any", so its own LocationKey names no map - the answer is the
        /// first map its objectives were actually placed on.</summary>
        private static string MapKeyOf(QuestNode node)
        {
            if (node == null) return null;

            foreach (var key in node.MapKeys)
                if (!string.IsNullOrEmpty(key)) return key;

            return null;
        }

        /// <summary>Prepares the view to open on a quest: its map, its floor, its row expanded and
        /// its pin flown to. The caller switches to the Maps view afterwards; the next Build does
        /// the rest. If the accepted-only filter would hide the quest, the filter is lifted - it
        /// makes no sense to be sent to a pin that is then not drawn.</summary>
        public static void ShowQuest(QuestNode node)
        {
            // A derived quest's own LocationKey is "any", which is no map at all - so the first of
            // the maps its objectives actually sit on is what "show this on the map" means for it.
            // Reading LocationKey blindly sent the player to a map that does not exist.
            var key = MapKeyOf(node);
            if (key == null) return;

            _selectedLocationKey = key;
            _selectedQuestId = node.Id;
            _pendingFocusQuestId = node.Id;
            _pendingScrollQuestId = node.Id;
            _pickerOpen = false;
            _floorPickerOpen = false;

            SelectFloorFor(node.Id, DynamicMapsLibrary.FindByLocationKey(key));

            if (StartedOnly && node.Status != ENodeStatus.Active && ModSettings.Ready)
                ModSettings.MarkStartedOnly.Value = false;
        }

        /// <summary>
        /// The map view, filling <paramref name="panelSize"/>: the control row along the top, the
        /// map below it on the left, the sidebar on the right. The map is the point of the whole
        /// panel, so it takes every pixel the sidebar does not.
        /// </summary>
        public static float Build(
            RectTransform parent, QuestGraphBuilder graph, Action onRepaint, Action onRefresh, Vector2 panelSize)
        {
            var byMap = GroupByMap(graph);
            PanelOpenTimer.Mark("map: group");

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

            // The GROUP key, never selected[0].LocationKey. A derived quest's own LocationKey is
            // "any" or blank, so reading the map identity off the first node returns null here -
            // and the map then loses its image, its floors and every pin. Whether it broke at all
            // came down to node ordering, which reads as an intermittent bug.
            var entry = DynamicMapsLibrary.FindByLocationKey(_selectedLocationKey);
            var layer = ResolveLayer(entry);

            // The map and list are built first and the dropdowns last, even though the dropdowns sit
            // above them on screen. Unity UI draws siblings in order, so an open list can only cover
            // the map if it is created after it.
            var contentHeight = BuildSelectedMap(parent, selected, entry, layer, ControlRowHeight, graph, onRepaint, onRefresh, panelSize);

            // Beside the two pickers, and built before them for the same draw-order reason. Its x is
            // fixed rather than measured from the floor picker, which is absent on single-floor maps
            // - a control that moves between maps is harder to find than one that is always here.
            //
            // Setting the value is enough to repaint: ModSettings raises Changed for this entry and
            // QuestTreePanel re-renders from it. Calling onRepaint as well would build the map twice.
            // Capped so the toggle stays on screen in a narrow window, where it used to run off
            // the right edge; the fixed position holds whenever there is room for it.
            var toggleX = Mathf.Min(AuxLayout.Padding + PickerWidth + 10f + FloorPickerWidth + 10f, panelSize.x - 230f);
            AuxLayout.AddToggleAt(
                parent,
                toggleX,
                AuxLayout.Padding,
                "Accepted quests only",
                StartedOnly,
                value => { if (ModSettings.Ready) ModSettings.MarkStartedOnly.Value = value; });

            // How much of this map is located, beside the toggle - said out loud because the
            // alternative is pins silently missing, and the fix (one raid here) is not guessable.
            // Dropped rather than clipped when the row is too narrow for it: half a sentence
            // about zones says less than nothing.
            var coverageY = AuxLayout.Padding + 5f;
            if (toggleX + 210f + 180f <= panelSize.x)
            {
                AddAt(parent, $"<color=#FFFFFF60>{CoverageLine(MarkerSetFor(entry))}</color>",
                    toggleX + 210f, ref coverageY, 18f, 11);
            }

            var labels = ordered.Select(LabelFor).ToList();
            var selectedIndex = ordered.FindIndex(m => m.Key == _selectedLocationKey);

            // The floor picker is absent on a single-floor map, which is most of them - a picker
            // with one entry is furniture. A floor list left open across a map change would have
            // nothing to draw, so the flag is cleared with the picker it belongs to.
            var floorNames = entry != null && entry.Layers.Count >= 2
                ? entry.Layers.Select(l => l.Name).ToList()
                : null;
            var floorIndex = floorNames != null ? entry.Layers.IndexOf(layer) : -1;
            if (floorNames == null) _floorPickerOpen = false;

            // Both headers, then the one open list after them. Sibling order is draw order, so a
            // list built before the second header would be covered by it - see
            // AuxLayout.AddDropdownHeader for the whole story.
            AuxLayout.AddDropdownHeader(
                parent, AuxLayout.Padding, labels, selectedIndex, _pickerOpen,
                toggleOpen: () =>
                {
                    _pickerOpen = !_pickerOpen;
                    _floorPickerOpen = false;
                    onRepaint();
                },
                width: PickerWidth);

            if (floorNames != null)
            {
                AuxLayout.AddDropdownHeader(
                    parent, AuxLayout.Padding, floorNames, floorIndex, _floorPickerOpen,
                    toggleOpen: () =>
                    {
                        _floorPickerOpen = !_floorPickerOpen;
                        _pickerOpen = false;
                        onRepaint();
                    },
                    width: FloorPickerWidth,
                    x: AuxLayout.Padding + PickerWidth + 10f);
            }

            var popupBottom = 0f;

            if (_pickerOpen)
            {
                popupBottom = AuxLayout.AddDropdownList(
                    parent, AuxLayout.Padding, labels, selectedIndex,
                    onSelect: index =>
                    {
                        _selectedLocationKey = ordered[index].Key;
                        _pickerOpen = false;

                        // A quest belongs to the map it is done on, so a selection never survives a
                        // map change - carrying it would leave a highlight with no row to explain it.
                        _selectedQuestId = null;
                        _pendingFocusQuestId = null;

                        onRepaint();
                    },
                    width: PickerWidth);
            }
            else if (_floorPickerOpen && floorNames != null)
            {
                popupBottom = AuxLayout.AddDropdownList(
                    parent, AuxLayout.Padding, floorNames, floorIndex,
                    onSelect: chosen =>
                    {
                        _selectedLevel = entry.Layers[chosen].Level;
                        _floorPickerOpen = false;
                        onRepaint();
                    },
                    width: FloorPickerWidth,
                    x: AuxLayout.Padding + PickerWidth + 10f);
            }

            // An open list is an overlay and so contributes no layout height of its own - but the
            // panel still has to be tall enough to scroll to the bottom of it.
            return Mathf.Max(contentHeight, popupBottom);
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

        /// <summary>What the pins mean, in the colours they are drawn in.</summary>
        private static string Legend()
        {
            var active = QuestNodeView.HexFor(ENodeStatus.Active);
            var grey = ColorUtility.ToHtmlStringRGB(UnknownMarkerColor);
            var accent = ColorUtility.ToHtmlStringRGB(GameStyle.AccentColor);

            var statuses = string.Join("  ", new[] { ENodeStatus.Active, ENodeStatus.Available, ENodeStatus.Completed, ENodeStatus.Gated, ENodeStatus.Locked, ENodeStatus.Failed }
                .Select(s => $"<color=#{QuestNodeView.HexFor(s)}>\u25a0</color> {QuestNodeView.NameFor(s).ToLowerInvariant()}"));

            return "<color=#FFFFFF60>" +
                   $"<color=#{active}>\u25c6</color> objective   <color=#{active}>\u25cf</color> item   \u25c7 \u25cb other floor\n" +
                   $"{statuses}  <color=#{grey}>\u25a0</color> not in your tree  <color=#{accent}>\u25a0</color> selected" +
                   "</color>";
        }

        /// <summary>Map name plus what is outstanding on it, so the dropdown still carries the
        /// counts the old button column showed.</summary>
        private static string LabelFor(KeyValuePair<string, List<QuestNode>> map)
        {
            var quests = map.Value;
            var actionable = quests.Count(q => q.Status == ENodeStatus.Active || q.Status == ENodeStatus.Available);

            var raid = _raidLocationKey != null &&
                       string.Equals(map.Key, _raidLocationKey, StringComparison.OrdinalIgnoreCase)
                ? "  ·  next raid"
                : "";

            return $"{DisplayNameFor(map.Key, quests)}   {actionable}/{quests.Count}{raid}";
        }

        /// <summary>The folded raid check for a map, or null when there is nothing to say.
        ///
        /// Null is the NEUTRAL case - the server half missing, no profile, an unreadable inventory,
        /// or a map the server does not recognise. It is never drawn as "you are ready".
        ///
        /// A requirement that could not be placed is NOT in that list any more. It comes back as a
        /// Caveat on a verdict that otherwise stands, because voiding the map over it threw away every
        /// other requirement on it.</summary>
        private static RaidCheckView.Verdict RaidCheckFor(string locationKey, QuestGraphBuilder graph)
        {
            if (string.IsNullOrEmpty(locationKey)) return null;

            var payload = QuestDataClient.GetRaidCheck();
            if (payload == null) return null;

            var countUnaccepted = !ModSettings.Ready || ModSettings.CountUnacceptedQuests.Value;
            var verdict = RaidCheckView.Fold(payload, locationKey, graph, countUnaccepted);

            // One Info line a session naming the cause, because five different causes all render as
            // the same silence and telling them apart otherwise means another play session.
            if (verdict.State == null && !_raidCheckReasonLogged)
            {
                _raidCheckReasonLogged = true;
                Plugin.LogSource?.LogInfo($"QuestTree: the raid check is neutral - {verdict.Reason}.");
            }

            // The caveat gets its own line, and needs one. It used to travel as a neutral Reason and so
            // was logged by the branch above - which is the only reason anyone found out that an
            // unplaceable requirement was blanking a whole map. Now that it keeps its rows, nothing
            // else would say so: the caveat shows on screen only where a green light was withheld, so
            // on a red or amber map it would otherwise be entirely silent.
            if (verdict.Caveat != null && !_raidCheckCaveatLogged)
            {
                _raidCheckCaveatLogged = true;
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: the raid check cannot confirm this map - {verdict.Caveat}.");
            }

            return verdict;
        }

        private static bool _raidCheckReasonLogged;
        private static bool _raidCheckCaveatLogged;

        /// <summary>The one-line answer, in the same three states as the ready-up button.</summary>
        private static string SummaryLine(RaidCheckView.Verdict verdict)
        {
            if (verdict.Empty) return "<color=#FFFFFF80>Nothing to bring for this map.</color>";

            if (verdict.State == RaidCheckView.Have.OnYou)
            {
                // "or in your task items" only when one actually is, rather than always: a player who
                // has packed everything themselves should not be left wondering which of it the game is
                // carrying for them.
                var tail = verdict.Lines.Any(l => l.InTaskItems >= l.Needed)
                    ? " or in your task items"
                    : "";

                // Amber and stated when the green was not earned. Only this branch consults the
                // caveat: red and amber are already not-green, and the server calls over-listing the
                // safe direction, so a requirement placed by assumption cannot make either of them
                // wrong in the dangerous direction.
                if (!verdict.Confirmed)
                {
                    return $"<color=#{GameStyle.WarningHex}>Everything listed is on you{tail} - but " +
                           $"{GameStyle.Safe(verdict.Caveat)}</color>";
                }

                return $"<color=#{GameStyle.SuccessHex}>Ready - everything this map needs is on you{tail}</color>";
            }

            if (verdict.State == RaidCheckView.Have.Missing)
            {
                var missing = verdict.Lines.First(l => l.State == RaidCheckView.Have.Missing);
                var tail = verdict.ToPackCount > 0 ? $", {verdict.ToPackCount} to pack" : "";

                return $"<color=#{GameStyle.ErrorHex}>Missing {verdict.MissingCount} of " +
                       $"{verdict.Lines.Count}: {missing.Name}{tail}</color>";
            }

            // Amber, and deliberately not "in your stash": 325 of the reference profile's items are
            // chiefly in hideout stashes, so the summary must not claim the stash when the rows
            // themselves say "elsewhere".
            //
            // "none of it is on you" is conditional now, and has to be. This branch fires on
            // ToPackCount > 0 and says nothing about the OTHER lines, which before task items counted
            // as held was a distinction without a difference - every off-person line was ToPack. Now a
            // map with one task item and one stash item printed "none of it is on you" directly above a
            // green row reading "1 of 1 - task item", and a mixed map is the common case rather than an
            // edge one.
            var held = verdict.Lines.Count - verdict.ToPackCount;

            var nothing = held == 0
                ? " - you own it all, none of it is on you"
                : $" - you own it all, and have {held} of {verdict.Lines.Count} already";

            return $"<color=#{GameStyle.WarningHex}>{verdict.ToPackCount} to pack{nothing}</color>";
        }

        /// <summary>One row: state colour, name, how many of how many, and where it is.</summary>
        private static string TakeWithYouRow(RaidCheckView.Line line)
        {
            var hex =
                line.State == RaidCheckView.Have.OnYou ? GameStyle.SuccessHex :
                line.State == RaidCheckView.Have.ToPack ? GameStyle.WarningHex :
                GameStyle.ErrorHex;

            // A task item is reported as what it is. It cannot be put in a rig, so "on you" would send
            // the player looking for it somewhere it can never be - which is the softer half of the bug
            // that had this row saying "elsewhere" and the cue asking for it to be packed.
            //
            // Not on a found-in-raid line, because InTaskItems has no found-in-raid split. Green there
            // is decided by OnPersonFoundInRaid, so a player carrying the one qualifying copy in their
            // rig while a non-found-in-raid copy sat in the task container would be told the game was
            // carrying it - about the only copy the quest will refuse. That split is real on the
            // reference profile, where one of the five task-container items is found-in-raid and four
            // are not. "on you" is the weaker word and the true one.
            var where =
                line.State == RaidCheckView.Have.OnYou &&
                    !line.NeedsFoundInRaid && line.InTaskItems >= line.Needed ? "task item" :
                line.State == RaidCheckView.Have.OnYou ? "on you" :
                line.State == RaidCheckView.Have.Missing ? "not owned" :
                line.InStash >= line.Needed ? "in stash" :
                line.Elsewhere > 0 ? "elsewhere" :
                "in stash";

            var quests = line.Quests.Count > 0 ? string.Join(", ", line.Quests.ToArray()) : "";
            var fir = line.NeedsFoundInRaid ? " (found in raid)" : "";

            return $"<color=#{hex}>{line.Name}</color>  {line.OnPerson} of {line.Needed}  " +
                   $"<color=#FFFFFF80>{where}{fir}</color>  <color=#FFFFFF60>{quests}</color>";
        }

        /// <summary>The display name for a map, found on whichever node actually knows it.
        ///
        /// quests[0].LocationId used to be read directly, which since 1.9.0 renders "Quests on any"
        /// whenever the first node in the group is a derived one - its own LocationId is "any", and
        /// the real name lives in its DerivedLocations entry for this map.</summary>
        private static string DisplayNameFor(string key, List<QuestNode> quests)
        {
            foreach (var quest in quests)
            {
                if (quest == null) continue;

                if (!string.IsNullOrEmpty(quest.LocationKey) &&
                    quest.LocationKey.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(quest.LocationId) &&
                    !quest.LocationId.Equals(QuestNode.AnyLocation, StringComparison.OrdinalIgnoreCase))
                {
                    return quest.LocationId;
                }

                foreach (var derived in quest.DerivedLocations)
                    if (derived.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(derived.Name))
                    {
                        return derived.Name;
                    }
            }

            return key;
        }

        /// <summary>Quests grouped by map, keyed on the raw location id so it can be matched against
        /// DynamicMaps' internal names; the display name rides along on the nodes.</summary>
        private static Dictionary<string, List<QuestNode>> GroupByMap(QuestGraphBuilder graph)
        {
            var byMap = new Dictionary<string, List<QuestNode>>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in graph.Nodes)
            {
                if (node.Status == ENodeStatus.Completed) continue;

                // Every map this quest belongs to, not just its declared one: a quest that says
                // "any" and then names five Shoreline zones belongs on Shoreline, and one that
                // names zones on two maps belongs on both.
                foreach (var rawKey in node.MapKeys)
                {
                    if (string.IsNullOrEmpty(rawKey)) continue;
                    if (rawKey.Equals(AnyLocation, StringComparison.OrdinalIgnoreCase)) continue;

                    // Folded to one key per SCENE, so the picker lists one "Ground Zero" and one
                    // "Factory" rather than two of each. Sandbox/Sandbox_high and
                    // factory4_day/factory4_night are separate location ids sharing a display name,
                    // and once derived quests are alias-expanded onto both, the picker showed the
                    // same name twice with the quests split between them. ResolveMapKey already
                    // treats these pairs as one map; this makes the grouping agree with it.
                    var key = CanonicalMapKey(rawKey);

                    if (!byMap.TryGetValue(key, out var list))
                    {
                        list = new List<QuestNode>();
                        byMap[key] = list;
                    }

                    list.Add(node);
                }
            }

            return byMap;
        }


        private static float BuildSelectedMap(
            RectTransform parent, List<QuestNode> quests, DynamicMapsLibrary.MapEntry entry,
            DynamicMapsLibrary.MapLayer layer, float top, QuestGraphBuilder graph, Action onRepaint,
            Action onRefresh, Vector2 panelSize)
        {
            var left = AuxLayout.Padding;
            var sprite = layer?.GetSprite();
            // The first open of a session pays the SVG tessellation here; later opens hit the
            // layer's cached sprite. The phase line tells the two apart.
            PanelOpenTimer.Mark("map: sprite");

            // The map takes everything the sidebar does not, in both directions. The sidebar
            // yields first: on a narrow panel the setting's width is cut back so the map keeps
            // 360px, where the two used to overlap.
            var sidebarWidth = Mathf.Clamp(SidebarWidth, 260f, Mathf.Max(260f, panelSize.x - 360f - AuxLayout.Padding * 3f));
            var mapWidth = Mathf.Max(360f, panelSize.x - sidebarWidth - AuxLayout.Padding * 3f);
            var height = Mathf.Max(300f, panelSize.y - top - AuxLayout.Padding);

            // The same filter the markers use, so the list and the map agree about what is on
            // screen. Without this, turning the toggle on emptied the map but left a list of quests
            // whose pins had just been hidden.
            //
            // In progress first, then startable, then locked: the order you would act on them.
            //
            // Computed before the map is built because a marker click has to land on a quest the
            // list is actually showing - see ClickTargetFor.
            var visible = quests
                .Where(q => !StartedOnly || q.Status == ENodeStatus.Active)
                .OrderBy(q => StatusRank(q.Status))
                .ThenBy(q => q.TraderName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var shownIds = new HashSet<string>(
                visible.Take(MaxQuestRows).Select(q => q.Id), StringComparer.Ordinal);

            // A selection the list no longer shows is dropped here, before either column is built.
            // The "accepted only" toggle, a quest completing, or the row cap can all remove the
            // selected quest's row - and its pin would otherwise stay painted as selected with
            // nothing to explain it, and unclickable, since a pin only takes clicks for quests the
            // list shows. There was no way to clear it short of selecting something else.
            if (_selectedQuestId != null && !shownIds.Contains(_selectedQuestId))
                _selectedQuestId = null;

            if (sprite != null)
            {
                BuildMapViewport(parent, entry, layer, sprite, left, top, mapWidth, height, graph, shownIds, onRepaint);
            }
            else
            {
                // The focus request is consumed inside the viewport build, so with no viewport it
                // would survive to fire on whichever map next renders - flying that map to a quest
                // nobody asked about.
                _pendingFocusQuestId = null;
            }

            PanelOpenTimer.Mark("map: viewport+pins");

            var sidebarX = sprite != null ? left + mapWidth + AuxLayout.Padding : left;
            var sidebarSpan = sprite != null ? sidebarWidth : Mathf.Max(sidebarWidth, panelSize.x - AuxLayout.Padding * 2f);

            BuildSidebar(parent, sidebarX, top, sidebarSpan, height, quests, visible, entry, layer, graph, onRepaint, onRefresh);
            PanelOpenTimer.Mark("map: sidebar rows");

            return top + height + AuxLayout.Padding;
        }

        /// <summary>Row-click semantics shared by every clickable quest row in the sidebar: the open
        /// quest closes again, anything else opens, flies the map to its pin and switches to its
        /// floor.</summary>
        private static void SelectQuest(QuestNode node, DynamicMapsLibrary.MapEntry entry, Action onRepaint)
        {
            if (node.Id == _selectedQuestId)
            {
                _selectedQuestId = null;
            }
            else
            {
                _selectedQuestId = node.Id;
                _pendingFocusQuestId = node.Id;
                _pendingScrollQuestId = node.Id;
                SelectFloorFor(node.Id, entry);
            }

            onRepaint();
        }

        /// <summary>
        /// The column beside the map. Its own scroll view, because the map is fixed and this is
        /// not: a quest expanded near the bottom of a long list has to be reachable without the
        /// map scrolling away with it. Three sections - what to do next here, every quest on the
        /// map, the items to look for - and the credits.
        ///
        /// Built directly into the content with the same y-cursor helpers the old list used; the
        /// content's height is set once everything is placed, and the pending scroll (a quest
        /// opened from its pin) is applied right after, against the real height.
        /// </summary>
        private static void BuildSidebar(
            RectTransform parent, float x, float top, float width, float height,
            List<QuestNode> quests, List<QuestNode> visible, DynamicMapsLibrary.MapEntry entry,
            DynamicMapsLibrary.MapLayer layer, QuestGraphBuilder graph, Action onRepaint, Action onRefresh)
        {
            var mapName = DisplayNameFor(_selectedLocationKey, quests);
            var set = MarkerSetFor(entry);
            var sprite = layer?.GetSprite();

            var sidebarGo = new GameObject(
                "Sidebar", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(ScrollRect));
            var sidebar = (RectTransform)sidebarGo.transform;
            sidebar.SetParent(parent, worldPositionStays: false);
            sidebar.anchorMin = sidebar.anchorMax = new Vector2(0f, 1f);
            sidebar.pivot = new Vector2(0f, 1f);
            sidebar.anchoredPosition = new Vector2(x, -top);
            sidebar.sizeDelta = new Vector2(width, height);

            var card = sidebarGo.GetComponent<Image>();
            card.color = new Color(1f, 1f, 1f, 0.04f);
            GameStyle.ApplyPanel(card);

            var contentGo = new GameObject("SidebarContent", typeof(RectTransform));
            var content = (RectTransform)contentGo.transform;
            content.SetParent(sidebar, worldPositionStays: false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;

            var scroll = sidebarGo.GetComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = sidebar;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            var inner = width - SidebarInset * 2f;
            var listX = SidebarInset;
            var y = SidebarInset;

            // ---- header
            AddAt(content, $"<b>{mapName}</b>", listX, ref y, 26f, 16, inner);

            // The other list views carry this; the map's "items to find here" reads the same
            // stash and had no way to re-read it short of leaving the view. On the header line,
            // placed by hand: DoNextView.RefreshLink assumes a section header's geometry.
            var linkY = y - 26f + 3f;
            AuxLayout.AddClickableRow(content, "<color=#FFFFFF80>Refresh  ⟳</color>",
                listX + inner - 90f, ref linkY, 90f, false, onRefresh, 20f);

            // The verdict, in one line, before anything that has to be read.
            //
            // AuxLayout.AddWrapped rather than AddAt: AddAt ellipsises at the column edge and this
            // is a sentence, not a label - a distinction this file learned the hard way.
            var verdict = RaidCheckFor(_selectedLocationKey, graph);
            if (verdict != null && verdict.State != null)
            {
                AuxLayout.AddWrapped(content, SummaryLine(verdict), listX, ref y, inner, 11);
            }

            // Said once, here, rather than by retitling the header: the map name is what the rest
            // of the sidebar refers back to ("Quests on Customs").
            if (IsShowingRaidMap)
            {
                AddAt(content, $"<color=#{ColorUtility.ToHtmlStringRGB(GameStyle.AccentColor)}>Your next raid</color>",
                    listX, ref y, 18f, 11, inner);
            }

            if (_notice != null)
            {
                AuxLayout.AddWrapped(content, $"<color=#{GameStyle.WarningHex}>{_notice}</color>", listX, ref y, inner, 11);
                _notice = null;
            }

            if (sprite == null && DynamicMapsLibrary.Available)
            {
                AddAt(content, "<color=#FFFFFF60>No map image for this location.</color>", listX, ref y, 18f, 11, inner);
            }
            else if (!DynamicMapsLibrary.Available)
            {
                AddAt(content, "<color=#FFFFFF60>Install the DynamicMaps mod to see map images here.</color>",
                    listX, ref y, 18f, 11, inner);
            }
            else
            {
                var facts = new List<string> { "Drag to pan, wheel to zoom" };
                if (entry != null && entry.Layers.Count > 1) facts.Add($"{entry.Layers.Count} floors");
                var spawns = MarkerCountFor(entry);
                if (spawns > 0) facts.Add($"{spawns} pins");
                AddAt(content, $"<color=#FFFFFF60>{string.Join("  ·  ", facts)}</color>", listX, ref y, 18f, 11, inner);

                // The pins encode two more things than the tree's legend covers - what kind of
                // place, and whether it is on this floor - and the tree's legend is hidden here
                // anyway. Text glyphs stand in for the pin sprite; the colours are the real ones.
                if (spawns > 0) AuxLayout.AddWrapped(content, Legend(), listX, ref y, inner, 11);
            }

            y += 6f;

            // ---- do next here: the global ranking, cut to this map
            var profile = QuestDataClient.GetProfile();
            var here = new HashSet<string>(quests.Select(q => q.Id), StringComparer.Ordinal);
            var ranked = DoNextView.Rank(graph, profile)
                .Where(r => here.Contains(r.Node.Id))
                .Where(r => !StartedOnly || r.Node.Status == ENodeStatus.Active)
                .Take(MaxDoNextRows)
                .ToList();
            PanelOpenTimer.Mark("map: sidebar head+rank");

            if (ranked.Count > 0)
            {
                AuxLayout.AddSectionHeader(content, ref y, "Do next here", listX, inner);

                foreach (var ranking in ranked)
                {
                    var node = ranking.Node;
                    AddQuestRow(content, node, listX, ref y, inner, node.Id == _selectedQuestId,
                        () => SelectQuest(node, entry, onRepaint));

                    var reason = DoNextView.Reason(ranking, profile, graph);
                    if (!string.IsNullOrEmpty(reason))
                        AddAt(content, $"<color=#FFFFFF60>{reason}</color>", listX + 22f, ref y, 16f, 11, inner - 22f);
                }

                y += 10f;
            }

            // ---- every quest on this map
            AuxLayout.AddSectionHeader(content, ref y, $"Quests on {mapName}  ({visible.Count})", listX, inner);

            if (visible.Count == 0)
            {
                // AuxLayout.AddWrapped rather than AddAt: this is a sentence, not a label, and AddAt
                // ellipsises at the column edge - which cut off the part saying how to get the
                // list back.
                AuxLayout.AddWrapped(content,
                    "<color=#FFFFFF60>No accepted quests on this map. Turn off \u201cAccepted quests " +
                    "only\u201d to see the rest.</color>",
                    listX, ref y, inner);
                y += 6f;
            }

            foreach (var node in visible.Take(MaxQuestRows))
            {
                var isSelected = node.Id == _selectedQuestId;

                // Rows are laid out on a plain y cursor in the content's own units, so the cursor
                // IS the scroll offset that brings this row to the top - no rect maths.
                if (node.Id == _pendingScrollQuestId)
                {
                    _pendingScrollY = Mathf.Max(0f, y - SidebarInset);
                    _pendingScrollQuestId = null;
                }

                AddQuestRow(content, node, listX, ref y, inner, isSelected, () => SelectQuest(node, entry, onRepaint));

                if (!isSelected) continue;

                // The same lines the tree view's detail panel shows, indented under the row that
                // opened them. The profile is only fetched here: at most one row is ever open.
                foreach (var line in QuestSummary.Lines(node, graph, QuestDataClient.GetProfile(), includeHeader: false))
                    AuxLayout.AddWrapped(content, line, listX + 12f, ref y, inner - 12f);

                y += 6f;
            }

            // Whether or not the row turned up - a quest can be filtered out or past the row cap -
            // the request is spent. Leaving it set would scroll on some unrelated later render.
            _pendingScrollQuestId = null;

            if (visible.Count > MaxQuestRows)
            {
                AddAt(content, $"<color=#FFFFFF60>+{visible.Count - MaxQuestRows} more</color>",
                    listX, ref y, AuxLayout.RowHeight, 11, inner);
            }

            // ---- take with you
            //
            // Distinct from "Items to find here" directly below, which is spawn-based: that section
            // is what the map GIVES you, this is what you must bring through the door. They sit
            // together because they are both about items, and in this order because you pack before
            // you land.
            if ((!ModSettings.Ready || ModSettings.ShowTakeWithYou.Value) &&
                verdict != null && verdict.State != null && verdict.Lines.Count > 0)
            {
                y += 10f;
                AuxLayout.AddSectionHeader(content, ref y, "Take with you", listX, inner);

                foreach (var line in verdict.Lines)
                {
                    var templates = line.Templates;
                    AuxLayout.AddClickableRow(content, TakeWithYouRow(line), listX, ref y, inner, false,
                        () => GameStyle.InspectItem(templates != null && templates.Count > 0 ? templates[0] : null));
                }
            }

            // ---- items to look for here
            var items = !ModSettings.Ready || ModSettings.ShowItemsSection.Value
                ? ItemsHere(set, graph, profile)
                : new List<ItemWatchlistView.WatchedItem>();
            if (items.Count > 0)
            {
                y += 10f;
                AuxLayout.AddSectionHeader(content, ref y, "Items to find here", listX, inner);

                foreach (var item in items)
                {
                    var template = item.Template;
                    AuxLayout.AddClickableRow(content, ItemWatchlistView.Format(item), listX, ref y, inner, false,
                        () => GameStyle.InspectItem(template));
                }
            }

            // ---- credits
            if (!ModSettings.Ready || ModSettings.ShowCredits.Value)
            {
                y += 12f;
                AddCredit(content, entry, listX, ref y, inner);
            }

            y += SidebarInset;

            content.sizeDelta = new Vector2(0f, y);

            // A quest opened from its pin may sit far down the column: scroll it into view, but
            // only if it is off screen, and then only far enough to show it with room for its
            // detail beneath - never to the top, which would hide everything above it.
            if (_pendingScrollY.HasValue)
            {
                var rowY = _pendingScrollY.Value;
                _pendingScrollY = null;

                var reveal = height * 0.4f;
                if (rowY + reveal > height)
                {
                    var maxScroll = Mathf.Max(0f, y - height);
                    content.anchoredPosition = new Vector2(0f, Mathf.Clamp(rowY - reveal, 0f, maxScroll));
                }
            }
        }

        /// <summary>The quest items that spawn on this map, with what the stash already holds.
        /// Joined on the marker's template id, which is what the server sends the pin with. Empty
        /// without the profile payload, since have/need is the point of the section.</summary>
        private static List<ItemWatchlistView.WatchedItem> ItemsHere(
            MapMarkerSetDto set, QuestGraphBuilder graph, ProfilePayloadDto profile)
        {
            var result = new List<ItemWatchlistView.WatchedItem>();
            if (set?.Markers == null || profile == null) return result;

            var templates = new HashSet<string>(
                set.Markers
                    .Where(m => m != null && !string.IsNullOrEmpty(m.Template) &&
                                string.Equals(m.Kind, ItemKind, StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.Template),
                StringComparer.Ordinal);

            if (templates.Count == 0) return result;

            // The pin carries the item's real name from the game's locale; the watchlist only has
            // what it could parse out of the objective sentence, which for "Locate and obtain the
            // golden Zibbo lighter on Customs" is the whole sentence.
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var marker in set.Markers)
            {
                if (marker == null || string.IsNullOrEmpty(marker.Template) || string.IsNullOrEmpty(marker.ItemName)) continue;
                if (!names.ContainsKey(marker.Template)) names[marker.Template] = marker.ItemName;
            }

            // The template set goes IN rather than being applied to the result - see Collect's own
            // note on why filtering afterwards was the expensive way round.
            var items = ItemWatchlistView.Collect(graph, profile, templates);

            foreach (var item in items)
                if (names.TryGetValue(item.Template, out var name)) item.Name = name;

            return items
                .OrderBy(i => i.Outstanding == 0 ? 1 : 0)
                .ThenBy(i => i.Outstanding)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Switches the view to the floor the quest's pin is on, so the pin being flown to
        /// is the solid one on the current storey rather than a dimmed off-floor ghost. Silently does
        /// nothing when the quest has no marker or the map has no floors to choose between.</summary>
        private static void SelectFloorFor(string questId, DynamicMapsLibrary.MapEntry entry)
        {
            var marker = FindMarkerFor(questId, entry);
            if (marker == null) return;

            var owner = OwnerFor(marker, entry);
            if (owner != null) _selectedLevel = owner.Level;
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
            Sprite sprite, float x, float y, float width, float height, QuestGraphBuilder graph,
            HashSet<string> shownIds, Action onRepaint)
        {
            var viewportGo = new GameObject(
                "MapViewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            var viewport = (RectTransform)viewportGo.transform;
            viewport.SetParent(parent, worldPositionStays: false);
            viewport.anchorMin = viewport.anchorMax = new Vector2(0f, 1f);
            viewport.pivot = new Vector2(0f, 1f);
            viewport.anchoredPosition = new Vector2(x, -y);
            viewport.sizeDelta = new Vector2(width, height);

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
            var fit = Mathf.Min(width / bounds.x, height / bounds.y);

            // Restore the previous view when this is the same map as last time - a floor change
            // rebuilds everything, and losing pan and zoom there defeats the purpose of the switch.
            var stateKey = entry != null ? string.Join(",", entry.InternalNames) : "";
            var sameMap = stateKey == _viewStateKey && _savedScale > 0f;

            space.localScale = sameMap
                ? new Vector3(_savedScale, _savedScale, 1f)
                : new Vector3(fit, fit, 1f);

            space.anchoredPosition = sameMap ? _savedPan : -layer.BoundsCentre * fit;

            _viewStateKey = stateKey;

            var imageGo = new GameObject("MapImage", typeof(RectTransform), typeof(SVGImage));
            var image = (RectTransform)imageGo.transform;
            image.SetParent(space, worldPositionStays: false);
            image.anchorMin = image.anchorMax = new Vector2(0.5f, 0.5f);
            image.pivot = new Vector2(0.5f, 0.5f);

            PlaceArtwork(image, entry, layer, bounds);

            if (ShowGuides) BuildGuides(space, layer);

            var svg = imageGo.GetComponent<SVGImage>();
            svg.sprite = sprite;
            svg.preserveAspect = false;
            svg.raycastTarget = false;

            // The handler is created before the overlays because they register with it to be held
            // at a constant on-screen size. Zoom limits and step are scaled by the fit, since the
            // container already sits at that scale.
            var panZoom = viewportGo.AddComponent<PanZoomHandler>();
            panZoom.Init(space, MinZoom * fit, MaxZoom * fit, ZoomSpeed * fit);

            // Recorded as it moves, so the next rebuild can pick it back up.
            panZoom.OnViewChanged = (scale, pan) =>
            {
                _savedScale = scale;
                _savedPan = pan;
            };

            _savedScale = space.localScale.x;
            _savedPan = space.anchoredPosition;

            BuildPlaceLabels(space, entry, layer, panZoom);
            BuildMarkers(space, entry, layer, panZoom, graph, shownIds, onRepaint);

            // Last, so it overrides the restored pan and zoom above - and after the markers, since
            // FocusOn re-applies their counter-scale for the zoom it lands on.
            FocusPendingQuest(entry, layer, panZoom, fit);
        }

        /// <summary>
        /// Moves the view to the pin of the quest just clicked in the list, if there is one.
        ///
        /// Best-effort by design: a quest with no marker on this map - and every quest at all when
        /// the companion server mod is absent - still selects and expands, it just does not move the
        /// map. The request is cleared either way, so an unfindable quest cannot leave the view
        /// snapping back on every later rebuild.
        /// </summary>
        private static void FocusPendingQuest(
            DynamicMapsLibrary.MapEntry entry, DynamicMapsLibrary.MapLayer layer,
            PanZoomHandler panZoom, float fit)
        {
            if (string.IsNullOrEmpty(_pendingFocusQuestId)) return;

            var questId = _pendingFocusQuestId;
            _pendingFocusQuestId = null;

            var marker = FindMarkerFor(questId, entry);
            if (marker == null)
            {
                // The row opened but the map did not move, and nothing said why.
                _notice = "No known location for this quest on this map.";
                return;
            }

            panZoom.FocusOn(PositionFor(marker, layer, entry), fit * FocusZoom);
        }

        /// <summary>
        /// Lays the picture over the rectangle the map's coordinates describe.
        ///
        /// This is one line because the sprite is now built against the SVG's viewBox - see
        /// DynamicMapsLibrary.LoadSvgSprite - so the sprite IS the rectangle ImageBounds describes
        /// and there is nothing to convert. Everything this method used to do was compensating for
        /// a sprite that had been sized to its ink instead, and none of it was ever right.
        /// </summary>
        private static void PlaceArtwork(
            RectTransform image, DynamicMapsLibrary.MapEntry entry,
            DynamicMapsLibrary.MapLayer layer, Vector2 bounds)
        {
            // The artwork is drawn in a frame turned by the map's own CoordinateRotation from the
            // coordinates every marker and label uses - BSG does not point north the same way on
            // every map, which is why the maps declare it. Turning the PICTURE by it brings the two
            // into the same frame.
            //
            // Confirmed on Woods, which declares 180: ZB-016 sits at map (-397, 18) and USEC Camp's
            // label at (290, -475), and reflecting the first through the bounds centre gives
            // (288, -490). Those two are opposite ends of a half turn to within 15 units on a
            // 1403-unit map, which is exactly what "ZB-016 and USEC Camp are flipped" describes.
            //
            // Only the image is turned. Markers, labels and guides stay in game coordinates, so
            // rotating them too would move everything together and change nothing - which is why
            // this could never have been fixed by rotating the whole container.
            var rotation = entry != null ? entry.CoordinateRotation : 0;
            rotation = ((rotation + ArtworkRotation) % 360 + 360) % 360;

            // At a quarter turn the rect has to swap its sides, or the picture is squeezed into the
            // wrong aspect - the same reason DynamicMaps sizes its own layer through a rotated
            // rectangle rather than the raw bounds.
            var quarterTurned = rotation == 90 || rotation == 270;
            image.sizeDelta = quarterTurned ? new Vector2(bounds.y, bounds.x) : bounds;

            image.anchoredPosition = layer.BoundsCentre;
            image.localRotation = Quaternion.Euler(0f, 0f, rotation);

            // Mirroring is a negative x scale rather than another rotation, since a mirror is not a
            // rotation and the two together cover every way the art could be turned.
            image.localScale = MirrorArtwork
                ? new Vector3(-1f, 1f, 1f)
                : Vector3.one;
        }


        /// <summary>
        /// Draws the rectangle the map's coordinates claim to cover, plus a crosshair on the map
        /// origin.
        ///
        /// A diagnostic, off by default. Judging alignment from a screenshot otherwise means
        /// fitting a transform from label positions and comparing it against remembered geography,
        /// which is how four wrong answers in a row got through. With a rectangle drawn at a known
        /// map coordinate, the picture either fills it or does not, and by how much is readable
        /// straight off the screen.
        /// </summary>
        private static void BuildGuides(RectTransform space, DynamicMapsLibrary.MapLayer layer)
        {
            var guide = new Color(1f, 0.3f, 0.3f, 0.85f);
            var size = layer.BoundsSize;
            var centre = layer.BoundsCentre;

            // The ImageBounds rectangle, as four hairlines so the map stays visible through it.
            AddGuideBar(space, new Vector2(centre.x, layer.BoundsMax.y), new Vector2(size.x, 3f), guide);
            AddGuideBar(space, new Vector2(centre.x, layer.BoundsMin.y), new Vector2(size.x, 3f), guide);
            AddGuideBar(space, new Vector2(layer.BoundsMin.x, centre.y), new Vector2(3f, size.y), guide);
            AddGuideBar(space, new Vector2(layer.BoundsMax.x, centre.y), new Vector2(3f, size.y), guide);

            // Map origin, which is a fixed point every coordinate is measured from.
            var cross = new Color(0.4f, 0.9f, 1f, 0.9f);
            AddGuideBar(space, Vector2.zero, new Vector2(size.x * 0.06f, 2f), cross);
            AddGuideBar(space, Vector2.zero, new Vector2(2f, size.y * 0.06f), cross);
        }

        private static void AddGuideBar(
            RectTransform space, Vector2 position, Vector2 size, Color colour)
        {
            var go = new GameObject("Guide", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(space, worldPositionStays: false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            var image = go.GetComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;
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
            RectTransform space, DynamicMapsLibrary.MapEntry entry,
            DynamicMapsLibrary.MapLayer layer, PanZoomHandler panZoom)
        {
            if (entry == null) return;

            foreach (var label in entry.Labels)
            {
                // Which floor the place is on, by the same height-band test the markers use. A name
                // whose height matches no band is treated as being on the floor you are looking at,
                // rather than dropped - the bands do not tile the world, and a real place name is
                // worth more than a tidy rule.
                var owner = entry.LayerFor(label.Position.x, label.Position.y, label.Height);
                var onThisFloor = owner == null || owner == layer;

                var go = new GameObject("PlaceLabel", typeof(RectTransform));
                var rect = (RectTransform)go.transform;
                rect.SetParent(space, worldPositionStays: false);
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = label.Position;
                rect.sizeDelta = new Vector2(160f, 18f);

                // Negated because these angles are clockwise-positive, as screen and SVG angles are,
                // while Unity's Z rotation is counter-clockwise - the same negation DynamicMaps
                // applies when it hands a rotation to its own labels. Not combined with the map's
                // CoordinateRotation: a label's position is unambiguously in game coordinates, so
                // its angle is read the same way rather than in the artwork's frame.
                if (Mathf.Abs(label.Rotation) > 0.01f)
                    rect.localRotation = Quaternion.Euler(0f, 0f, -label.Rotation);

                panZoom.KeepConstantScale(rect);

                var text = go.AddComponent<TextMeshProUGUI>();
                text.text = label.Text;
                text.fontSize = 15;

                // Full strength and bold, against a background that is teal, tan and grey by turns.
                // At 75% white it washed out over the pale buildings; the black outline the shared
                // material already carries does the rest of the work. White rather than a hue on
                // purpose - the markers own green and grey, and place names should not compete with
                // them for meaning.
                // Names on another floor recede rather than disappear. Hiding them would strip 63
                // of Interchange's 77 off its ground floor and take the sense of place with them.
                text.color = onThisFloor ? Color.white : new Color(1f, 1f, 1f, 0.45f);
                text.fontStyle = onThisFloor ? FontStyles.Bold : FontStyles.Normal;

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
        /// <summary>
        /// What a marker says: the item, the quest that wants it, the floor if it is not this one,
        /// and how many places the item can turn up.
        ///
        /// That last part matters. The loot table lists every position an item may take and it
        /// takes exactly one of them per raid, so a pin is a place to look rather than a place the
        /// item is. Saying "1 of 4" is the difference between a map that is wrong three times out
        /// of four and one that told you the odds.
        /// </summary>
        private static string LabelFor(
            MapMarkerDto marker, DynamicMapsLibrary.MapLayer owner, bool onThisFloor, string openedQuestName)
        {
            var text = GameStyle.Safe(marker.ItemName);

            // Named after the quest a click will open, so the pin never reads one name and opens
            // another. Falls back to the first name the server sent - and only by name: Quests and
            // QuestIds are de-duplicated separately server-side, so they are not index-aligned.
            var quest = openedQuestName
                ?? (marker.Quests != null && marker.Quests.Count > 0 ? marker.Quests[0] : null);

            if (!string.IsNullOrEmpty(quest) && quest != marker.ItemName)
            {
                // Quests can be null when the name came from openedQuestName - the two lists are
                // built separately server-side, and a marker may carry ids without names.
                var more = marker.Quests != null && marker.Quests.Count > 1 ? $" +{marker.Quests.Count - 1}" : "";
                text += $"  <color=#FFFFFF70>{GameStyle.Safe(quest)}{more}</color>";
            }

            if (marker.Alternatives > 1)
                text += $"  <color=#FFFFFF50>(1 of {marker.Alternatives})</color>";

            if (!onThisFloor && owner != null)
                text += $"  <color=#FFFFFF50>({owner.Name})</color>";

            return text;
        }

        /// <summary>
        /// Where a marker sits, in map coordinates.
        ///
        /// Item spawns arrive as world coordinates and are used as they are. Objective markers
        /// arrive as a percentage across and down the map IMAGE, because that is how their source
        /// states it - so they are read against the layer's own rectangle and then turned by the
        /// map's CoordinateRotation, the same turn the picture gets, which puts them in the same
        /// frame as everything else.
        ///
        /// Confirmed numerically before it was written: mapping the percentages without that turn
        /// put twelve Customs objectives 400-760 units from the very spawns their own items use,
        /// and with it they land within 1 to 17.
        /// </summary>
        private static Vector2 PositionFor(
            MapMarkerDto marker, DynamicMapsLibrary.MapLayer layer, DynamicMapsLibrary.MapEntry entry)
        {
            // Map space is (game.x, game.z) with game.y as the height - see DynamicMapsLibrary.
            if (marker.LeftPercent <= 0f && marker.TopPercent <= 0f)
                return new Vector2(marker.X, marker.Z);

            var size = layer.BoundsSize;

            var point = new Vector2(
                layer.BoundsMin.x + marker.LeftPercent / 100f * size.x,
                layer.BoundsMax.y - marker.TopPercent / 100f * size.y);

            var rotation = entry?.CoordinateRotation ?? 0;
            if (rotation == 0) return point;

            var radians = rotation * Mathf.Deg2Rad;
            var cos = Mathf.Cos(radians);
            var sin = Mathf.Sin(radians);
            var offset = point - layer.BoundsCentre;

            return layer.BoundsCentre + new Vector2(
                offset.x * cos - offset.y * sin,
                offset.x * sin + offset.y * cos);
        }

        /// <summary>
        /// Which floor a marker belongs to.
        ///
        /// A marker that names its floor is matched against the layer's FloorName - the name the
        /// artwork's own filename uses - because that is the vocabulary the two sides share. The
        /// config's display key is not: matching on it put 171 of 232 objective pins on no layer at
        /// all, and Interchange's "First_Floor" is Level 1 rather than the ground, which a name-free
        /// ordinal guess gets backwards.
        ///
        /// The comparison is exact rather than a substring, because "Underground_Level" contains
        /// "Ground_Level" and a loose match would put underground pins on the ground floor.
        ///
        /// Null means "no idea", which the caller draws on whatever floor is being viewed. That is
        /// the honest answer for a marker with no coordinates: the height-band test below only
        /// applies to item spawns, which have real ones. Asking it about a marker whose position is
        /// (0, 0, 0) returned whichever layer covers the map origin - an answer, but a fictional one.
        /// </summary>
        private static DynamicMapsLibrary.MapLayer OwnerFor(
            MapMarkerDto marker, DynamicMapsLibrary.MapEntry entry)
        {
            if (!string.IsNullOrEmpty(marker.Floor))
            {
                var named = entry.Layers.FirstOrDefault(l =>
                    string.Equals(l.FloorName, marker.Floor, StringComparison.OrdinalIgnoreCase));

                if (named != null) return named;

                // A map drawn as a single image names no floor in its filename, so any floor the
                // data gives is that one map.
                return entry.Layers.Count == 1 ? entry.Layers[0] : null;
            }

            return entry.LayerFor(marker.X, marker.Z, marker.Y);
        }

        /// <summary>
        /// What a pin is, and what it opens, in one walk over the quests it serves.
        ///
        /// Status is the most actionable status among ALL its quests - Active over Available over
        /// Locked over Completed, the same <see cref="StatusRank"/> the quest list is sorted by
        /// rather than a second opinion about what "most relevant" means. Null when no quest
        /// resolves, which is not the same as "locked": the payload can name a quest the graph does
        /// not hold, and guessing a status for it would be a lie.
        ///
        /// The click target is the most actionable of its quests that the list is actually
        /// SHOWING - the map drops completed quests (see GroupByMap) while the graph still holds
        /// them, and opening one would select a row that never renders. The selected quest wins
        /// outright when the pin serves it: the pin is painted as selected because of it, so a
        /// second click has to close IT, not switch to whichever other quest ranks higher. Null
        /// when none of its quests has a row; the pin still names itself on hover, and Fallback
        /// is then its best quest in the graph, so a click can at least say why there is no row.
        /// </summary>
        private static (string ClickId, ENodeStatus? Status, QuestNode Fallback) BestQuestFor(
            MapMarkerDto marker, QuestGraphBuilder graph, HashSet<string> shown)
        {
            if (marker.QuestIds == null) return (null, null, null);

            ENodeStatus? status = null;
            string clickId = null;
            QuestNode fallback = null;
            var clickRank = int.MaxValue;

            foreach (var id in marker.QuestIds)
            {
                if (id == null || !graph.NodesById.TryGetValue(id, out var node)) continue;

                var rank = StatusRank(node.Status);
                if (status == null || rank < StatusRank(status.Value))
                {
                    status = node.Status;
                    fallback = node;
                }

                if (shown.Contains(id) && rank < clickRank)
                {
                    clickId = id;
                    clickRank = rank;
                }
            }

            if (_selectedQuestId != null && shown.Contains(_selectedQuestId) && marker.QuestIds.Contains(_selectedQuestId))
                clickId = _selectedQuestId;

            return (clickId, status, clickId == null ? fallback : null);
        }

        private static void BuildMarkers(
            RectTransform space, DynamicMapsLibrary.MapEntry entry,
            DynamicMapsLibrary.MapLayer layer, PanZoomHandler panZoom, QuestGraphBuilder graph,
            HashSet<string> shownIds, Action onRepaint)
        {
            if (entry == null) return;

            var set = MarkerSetFor(entry);
            if (set?.Markers == null) return;

            // Active quests first, then this floor, so the pin that draws on top of a pile is the
            // one you have started and could walk to right now.
            var ordered = set.Markers
                .Where(m => m != null)
                .Select(m =>
                {
                    var (clickId, status, fallback) = BestQuestFor(m, graph, shownIds);
                    return (
                        Marker: m,
                        Owner: OwnerFor(m, entry),
                        Status: status,
                        ClickId: clickId,
                        Fallback: fallback,
                        Active: status == ENodeStatus.Active,
                        Objective: string.Equals(m.Kind, ObjectiveKind, StringComparison.OrdinalIgnoreCase));
                })
                .Where(m => !StartedOnly || m.Active)
                .OrderBy(m => m.Active ? 0 : 1)
                .ThenBy(m => m.Objective ? 0 : 1)
                .ThenBy(m => m.Owner == null || m.Owner == layer ? 0 : 1)
                .ToList();

            // Lifted above the other pins once they all exist - doing it as it is built would only
            // put it above the markers created so far.
            RectTransform selectedRect = null;

            // The labels that may show at rest, placed after the loop and again on every zoom
            // (see PlaceRestLabels): their screen size is constant, so which of them overlap
            // depends on the zoom, and a test made once at build time went stale on the first
            // wheel notch.
            var restLabels = new List<RestLabel>();

            foreach (var (marker, owner, status, clickId, fallback, active, objective) in ordered)
            {
                // A marker whose height matches no floor at all is treated as belonging to the one
                // being shown rather than dropped: the bands do not tile the world exhaustively, and
                // a real spawn is worth more than a tidy rule.
                var onThisFloor = owner == null || owner == layer;

                var isSelected = _selectedQuestId != null && marker.QuestIds != null &&
                                 marker.QuestIds.Contains(_selectedQuestId);

                // Colour carries quest status - taken from the shared palette so a started quest is
                // the same green here as in the tree and the legend. Everything you have not
                // started is held back, so the pins you can act on now carry the map.
                var colour = status.HasValue ? QuestNodeView.ColorFor(status.Value) : UnknownMarkerColor;
                if (!active) colour.a *= 0.6f;
                if (!onThisFloor) colour.a *= 0.55f;

                // The pin the list sent you to, at full strength whatever its status - having flown
                // the map to it, the one thing it must not be is hard to pick out.
                if (isSelected) colour = GameStyle.AccentColor;

                var position = PositionFor(marker, layer, entry);

                // Which quest this pin opens - see BestQuestFor. Null selects nothing.
                var clickTarget = clickId;

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

                if (isSelected) selectedRect = rect;

                var pin = objective ? DynamicMapsLibrary.QuestPin : null;

                if (pin != null)
                {
                    // A real map pin for "the quest happens here", pivoted at its tip by the sprite
                    // itself, so the rect's own position is the place being marked. Hit-testable so
                    // it can be hovered and clicked; drag and scroll still reach the viewport - see
                    // MapMarkerClick for why that is safe.
                    var icon = go.AddComponent<Image>();
                    icon.sprite = pin;
                    icon.color = colour;
                    icon.raycastTarget = true;
                    icon.preserveAspect = true;
                    rect.pivot = new Vector2(0.5f, 0f);
                    rect.sizeDelta = new Vector2(PinSize * 0.72f, PinSize);
                }
                else
                {
                    // A glyph rather than an Image: it is round, and it takes the same outline the
                    // rest of the map text uses. A diamond for "the quest happens here", a dot for
                    // "the thing you need lies here". Hollow when it is on another floor.
                    var dot = go.AddComponent<TextMeshProUGUI>();
                    dot.text = objective
                        ? (onThisFloor ? "\u25c6" : "\u25c7")
                        : (onThisFloor ? "\u25cf" : "\u25cb");
                    dot.fontSize = objective ? MarkerSize + 3f : MarkerSize;
                    dot.color = colour;
                    dot.alignment = TextAlignmentOptions.Center;
                    dot.enableWordWrapping = false;
                    dot.raycastTarget = true;
                    GameStyle.ApplyOutlined(dot);
                }

                // The name, shown only for the hovered pin and the selected quest's pins. Drawing
                // every name at once turned any cluster of objectives into a block of text. Built
                // on first need: most pins are never hovered, and a text object per pin was half
                // of everything a map repaint created.
                GameObject labelGo = null;

                GameObject EnsureLabel()
                {
                    if (labelGo != null) return labelGo;

                    labelGo = new GameObject("Label", typeof(RectTransform));
                    var labelRect = (RectTransform)labelGo.transform;
                    labelRect.SetParent(rect, worldPositionStays: false);
                    labelRect.anchorMin = labelRect.anchorMax = new Vector2(0.5f, 0.5f);
                    labelRect.pivot = new Vector2(0f, 0.5f);
                    labelRect.anchoredPosition = objective && pin != null
                        ? new Vector2(PinSize * 0.5f, PinSize * 0.75f)
                        : new Vector2(MarkerSize * 0.6f, 0f);
                    labelRect.sizeDelta = new Vector2(LabelWidth, LabelHeight);

                    var label = labelGo.AddComponent<TextMeshProUGUI>();
                    label.text = LabelFor(marker, owner, onThisFloor,
                        clickTarget != null && graph.NodesById.TryGetValue(clickTarget, out var opened)
                            ? opened.Name
                            : null);
                    label.fontSize = 13;
                    // The pin's own colour, so the name says the status the pin does. Full alpha:
                    // a dimmed off-floor pin is a hint, but its name has to be readable when asked.
                    label.color = new Color(colour.r, colour.g, colour.b, 1f);
                    label.alignment = TextAlignmentOptions.Left;
                    label.enableWordWrapping = false;
                    label.overflowMode = TextOverflowModes.Ellipsis;
                    label.raycastTarget = false;
                    GameStyle.ApplyOutlined(label);
                    return labelGo;
                }

                // Shown at rest for the selected quest, and beyond that for whichever pins the
                // Settings say - never where it would land on a label already placed, except the
                // selected quest's, which is the one name that must not lose the collision. Hover
                // shows any name regardless.
                var mode = ModSettings.Ready ? ModSettings.PinLabels.Value : ModSettings.PinLabelMode.HoverOnly;
                var actionable = status == ENodeStatus.Active || status == ENodeStatus.Available;
                var eligible = isSelected ||
                               mode == ModSettings.PinLabelMode.All ||
                               (mode == ModSettings.PinLabelMode.Actionable && actionable);

                var rest = new RestLabel
                {
                    Position = position,
                    Selected = isSelected,
                    Show = show =>
                    {
                        if (show) EnsureLabel().SetActive(true);
                        else if (labelGo != null) labelGo.SetActive(false);
                    }
                };
                if (eligible) restLabels.Add(rest);

                var click = go.AddComponent<MapMarkerClick>();

                click.OnHover = hovering =>
                {
                    if (rect == null) return;
                    rest.Hovered = hovering;
                    rest.Show(hovering || rest.Visible);
                    // Above its neighbours while hovered, so the name is not under the next pin.
                    if (hovering) rect.SetAsLastSibling();
                };

                if (clickTarget != null)
                {
                    var wasSelected = clickTarget == _selectedQuestId;

                    click.OnClicked = () =>
                    {
                        GameStyle.PlaySound(EUISoundType.ButtonClick);

                        // Clicking the open quest's pin again closes it, matching the rows.
                        _selectedQuestId = wasSelected ? null : clickTarget;

                        // Deliberately NOT setting _pendingFocusQuestId or the floor: you clicked a
                        // pin you can already see, so re-centring and jumping the zoom would throw
                        // the view away for nothing. That is the row's job, not the pin's.
                        if (!wasSelected) _pendingScrollQuestId = clickTarget;

                        onRepaint();
                    };
                }
                else if (fallback != null)
                {
                    // A pin with no row used to swallow the click without a sound. It still opens
                    // nothing - there is no row to open - but it says which quest it is and why.
                    var unlisted = fallback;
                    click.OnClicked = () =>
                    {
                        GameStyle.PlaySound(EUISoundType.ButtonClick);
                        _notice = unlisted.Status == ENodeStatus.Completed
                            ? $"{GameStyle.Safe(unlisted.Name)} is complete - nothing left to do here."
                            : $"{GameStyle.Safe(unlisted.Name)} is not among the {MaxQuestRows} quests listed here.";
                        onRepaint();
                    };
                }
            }

            selectedRect?.SetAsLastSibling();

            PlaceRestLabels(restLabels, space.localScale.x);

            // Re-placed on every zoom: the pins and names keep their screen size, so the footprint
            // each name claims in map units changes with the scale.
            var placedAt = space.localScale.x;
            panZoom.OnViewChanged += (scale, _) =>
            {
                if (Mathf.Approximately(scale, placedAt)) return;
                placedAt = scale;
                PlaceRestLabels(restLabels, scale);
            };
        }

        /// <summary>A pin name that may be shown at rest: where it is, whether it is the selected
        /// quest's (which never loses a collision), and how to show or hide it.</summary>
        private sealed class RestLabel
        {
            public Vector2 Position;
            public bool Selected;
            public Action<bool> Show;
            public bool Visible;
            public bool Hovered;
        }

        /// <summary>Decides which at-rest names are drawn at this zoom: the selected quest's
        /// always, then each of the rest unless it would land on a name already placed. Names
        /// are held at a constant screen size, so their span in map units is the screen span
        /// divided by the scale.</summary>
        private static void PlaceRestLabels(List<RestLabel> labels, float scale)
        {
            var span = new Vector2(LabelWidth, LabelHeight) / Mathf.Max(0.0001f, scale);
            var claimed = new List<Rect>();

            foreach (var label in labels.OrderBy(l => l.Selected ? 0 : 1))
            {
                var footprint = new Rect(
                    label.Position.x + span.x * 0.1f, label.Position.y - span.y * 0.5f, span.x, span.y);

                var visible = label.Selected || !claimed.Any(other => other.Overlaps(footprint));
                if (visible) claimed.Add(footprint);

                // A name under the cursor stays whatever the collision says; a zoom must not
                // take it away until the pointer leaves.
                label.Visible = visible;
                label.Show(visible || label.Hovered);
            }
        }

        /// <summary>This map's markers from the server payload, matched on any of the map's internal
        /// names. Shared by everything that needs them so the lookup exists once.</summary>
        private static MapMarkerSetDto MarkerSetFor(DynamicMapsLibrary.MapEntry entry)
        {
            if (entry == null) return null;

            var payload = QuestDataClient.GetMapMarkers();
            if (payload?.Maps == null) return null;

            // Asked five or six times per build (the coverage line, the sidebar, the pin count,
            // the markers, each fly-to), always for the same map of the same payload.
            if (ReferenceEquals(payload, _setPayload) && ReferenceEquals(entry, _setEntry)) return _set;

            _set = payload.Maps.FirstOrDefault(m =>
                m?.LocationKey != null &&
                entry.InternalNames.Any(n => string.Equals(n, m.LocationKey, StringComparison.OrdinalIgnoreCase)));
            _setPayload = payload;
            _setEntry = entry;
            return _set;
        }

        private static MapMarkerPayloadDto _setPayload;
        private static DynamicMapsLibrary.MapEntry _setEntry;
        private static MapMarkerSetDto _set;

        /// <summary>Where a quest is on this map, or null if it has no marker here. An objective pin
        /// is where the quest actually happens; an item marker is only somewhere one of the things it
        /// asks for can spawn - so the objective wins when a quest has both.</summary>
        private static MapMarkerDto FindMarkerFor(string questId, DynamicMapsLibrary.MapEntry entry)
        {
            if (string.IsNullOrEmpty(questId)) return null;

            var set = MarkerSetFor(entry);
            if (set?.Markers == null) return null;

            return set.Markers
                .Where(m => m?.QuestIds != null && m.QuestIds.Contains(questId))
                .OrderBy(m => string.Equals(m.Kind, ObjectiveKind, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
        }

        /// <summary>How many item spawns this map has markers for, for the line under the heading.</summary>
        private static int MarkerCountFor(DynamicMapsLibrary.MapEntry entry) =>
            MarkerSetFor(entry)?.Markers?.Count ?? 0;

        /// <summary>The map's own author credit, shown because the images are someone else's work
        /// (tarkov.dev, via DynamicMaps) and their licence is only satisfied with attribution.</summary>
        private static void AddCredit(
            RectTransform parent, DynamicMapsLibrary.MapEntry entry, float x, ref float y, float width)
        {
            if (entry == null) return;

            if (!string.IsNullOrEmpty(entry.Attribution))
            {
                AddAt(parent, $"<color=#FFFFFF50>Map: {entry.Attribution}, via DynamicMaps</color>",
                    x, ref y, 15f, 10, width);
            }

            // Both of these are required rather than courteous. The pin is game-icons.net art under
            // CC BY 3.0, which only permits use with attribution, and the objective locations are
            // someone else's collected work that this mod fetches rather than owns.
            var pin = DynamicMapsLibrary.QuestPin != null
                ? "  ·  Pin icon by Delapouite (game-icons.net), CC BY 3.0"
                : "";

            AddAt(parent, $"<color=#FFFFFF50>Objective locations: TarkovTracker/tarkovdata{pin}</color>",
                x, ref y, 15f, 10, width);
        }

        /// <summary>A panel behind a column, so its text reads as a block rather than as words lying
        /// loose on whatever is behind them.</summary>
        private static string CoverageLine(MapMarkerSetDto set)
        {
            if (set == null || string.IsNullOrEmpty(set.HarvestedAt))
                return "Zones: not harvested yet - one raid on this map pins every objective on it";

            if (set.ZonesWanted == 0)
                return $"Zones: none needed on this map (harvested {set.HarvestedAt})";

            return set.ZonesKnown < set.ZonesWanted
                ? $"Zones located: {set.ZonesKnown}/{set.ZonesWanted} - raid this map again to find the rest"
                : $"Zones located: {set.ZonesKnown}/{set.ZonesWanted} (harvested {set.HarvestedAt})";
        }

        /// <summary>
        /// One clickable quest in the list.
        ///
        /// Built by hand rather than through <see cref="AddAt"/> because a bare TextMeshProUGUI has
        /// no raycast target at all - which is exactly why these rows did nothing before. The Image
        /// is what makes the row hit-testable; it is fully transparent when unselected, and Unity
        /// still raycasts a zero-alpha Graphic, so nothing is drawn for it.
        /// </summary>
        private static void AddQuestRow(
            RectTransform parent, QuestNode node, float x, ref float y, float width, bool selected, Action onClick)
        {
            const float height = AuxLayout.RowHeight;

            var go = new GameObject("QuestRow", typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);

            var background = go.GetComponent<Image>();
            var accent = GameStyle.AccentColor;
            background.color = selected
                ? new Color(accent.r, accent.g, accent.b, 0.22f)
                : Color.clear;

            var button = go.GetComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(() =>
            {
                GameStyle.PlaySound(EUISoundType.ButtonClick);
                onClick();
            });

            // The same lift and sound the Do next and Items rows have - these were the only rows
            // in the mod that gave nothing back under the pointer.
            GameStyle.AddHoverFeedback(go, background);

            var labelGo = new GameObject("Label", typeof(RectTransform));
            var labelRect = (RectTransform)labelGo.transform;
            labelRect.SetParent(rect, worldPositionStays: false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(4f, 0f);
            labelRect.offsetMax = Vector2.zero;

            var hex = ColorUtility.ToHtmlStringRGB(QuestNodeView.ColorFor(node.Status));

            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = $"<color=#{hex}>{QuestNodeView.GlyphFor(node.Status)}</color>  {GameStyle.Safe(node.Name)}" +
                         $"  <color=#FFFFFF60>{GameStyle.Safe(node.TraderName)}</color>";
            label.fontSize = 12;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Left;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            GameStyle.Apply(label);

            y += height;
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
            // Plain text: left as a raycast target it swallowed the wheel over the sidebar's
            // header lines, so the list only scrolled from over a row.
            label.raycastTarget = false;
            GameStyle.Apply(label);

            y += height;
        }

        /// <summary>How actionable a status is, lowest first: in progress, startable, then everything
        /// still to come, and finished last.
        ///
        /// Completed used to fall into the same bucket as Locked and Gated, so BestQuestFor's claim of
        /// "Active over Available over Locked over Completed" was false, and a pin serving one completed and
        /// one locked quest chose between them by enumeration order - deciding whether it said "nothing left
        /// to do here" or named the quest still waiting.
        ///
        /// The sidebar list is unaffected either way: GroupByMap skips Completed, so it has never held
        /// one.</summary>
        private static int StatusRank(ENodeStatus status) => status switch
        {
            ENodeStatus.Active => 0,
            ENodeStatus.Available => 1,
            ENodeStatus.Completed => 3,
            _ => 2
        };
    }
}
