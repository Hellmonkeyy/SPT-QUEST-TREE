// (c) Map3DView's floor window rule (5360921): a face on no floor's band keeps its filed floor when that floor's
// capture camera drew its height; else the TOPMOST floor takes it when its deeper window holds it; else the nearest
// other floor whose window holds it; else no picture (tint). Bands are the real Woods and Reserve captures'.
//
// @@MUTATE FloorWindow.Prep :: if (own != _topRange && InWindow(_topRange, y)) return FloorRanges[_topRange].Level; :: @@
// @@MUTATE FloorWindow :: (top ? CaptureTopDepthBelow : 0f) :: 0f@@
// @@MUTATE FloorWindow.Prep :: if (InWindow(own, y)) return filed; :: @@
// @@MUTATE FloorWindow.Prep :: best < 0 || distance < bestDistance || :: best < 0 || distance > bestDistance ||@@
// @@MUTATE FloorWindow.Prep :: && range.Level > best)) :: && range.Level < best))@@
// @@MUTATE FloorWindow.Prep :: range.Level > FloorRanges[best].Level :: range.Level < FloorRanges[best].Level@@
//
// (d) the slice slider's stops (S1): home = today's CutHeight, ceiling = the band's picture camera (CaptureWindow over the
// next floor up from NextMinY, shared with Prep.MeasureWindows), a section under MinSectionMetres a notch, the band window.
// @@MUTATE FloorWindow.Slice :: else if (ceiling - home >= MinSectionMetres) :: else if (ceiling - home >= 0f)@@
// @@MUTATE FloorWindow.Slice :: return top + CutAboveFloor; :: return top;@@
// @@MUTATE FloorWindow :: if (j != index && low > minY && low < next) next = low; :: if (j != index && low > minY) next = low;@@
// @@MUTATE FloorWindow.Slice :: var first = Mathf.Max(0, last - cap + 1); :: var first = last;@@
// @@MUTATE FloorWindow.Slice :: || !window.Contains(layer.Level) || :: ||@@
// @@MUTATE FloorWindow.Slice :: if (!known) sectionsOff = true; :: @@
// @@MUTATE FloorWindow :: if (j != index && low > minY && low < next) next = low; :: if (j != index && low >= minY && low < next) next = low;@@
// @@MUTATE FloorWindow.Slice :: if (seen.Contains(layer.Level)) continue; :: @@
//
// (e) S2, built versus drawn: the view builds the window and draws its bands at or below the active band; at every
// selection what is drawn (bands, ground band, roof owners) is what DrawnLevels and the old roof rule gave.
// @@MUTATE FloorWindow.Slice :: return lowest != int.MaxValue ? lowest : selected; :: return selected;@@
// @@MUTATE FloorWindow.Slice :: (roofLevel <= active ? floorAt(roofLevel) : null) :: floorAt(roofLevel)@@
// @@MUTATE FloorWindow.Slice :: (selected == active ? floorAt(active) : null) :: floorAt(active)@@
// @@MUTATE FloorWindow.Slice :: var room = levels.Contains(selected) ? cap : cap - 1; :: var room = cap;@@
// @@MUTATE FloorWindow.Slice :: var active = ActiveLevelOf(bandLevels, selected); :: var active = selected;@@
// @@MUTATE FloorWindow.Slice :: private static bool Shown(int level, int active) => level <= active; :: private static bool Shown(int level, int active) => level < active;@@
// @@MUTATE FloorWindow.Slice :: if (level <= selected && (!below || level > highestBelow)) :: if (level <= selected && (!below || level < highestBelow))@@
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnitTests.FloorWindow
{
    internal static class Map3DView
    {
// @@CONST Source/Tarkov-QuestTree/UI/Map3DView.cs FloorFaceSlack@@
// @@CONST Source/Tarkov-QuestTree/UI/Map3DView.cs CaptureTopCameraHeight@@
// @@CONST Source/Tarkov-QuestTree/UI/Map3DView.cs CaptureCeilingClearance@@
// @@CONST Source/Tarkov-QuestTree/UI/Map3DView.cs CaptureFarClipSlack@@
// @@CONST Source/Tarkov-QuestTree/UI/Map3DView.cs CaptureTopDepthBelow@@
// @@CONST Source/Tarkov-QuestTree/UI/Map3DView.cs CutAboveFloor@@
// @@CONST Source/Tarkov-QuestTree/UI/Map3DView.cs MinSectionMetres@@

// @@REGION FloorWindow@@

// @@REGION FloorWindow.Slice@@

        internal sealed class Prep
        {
// @@CONST Source/Tarkov-QuestTree/UI/Map3DView.cs NoPictureFloor@@

// @@REGION FloorWindow.Prep@@
        }

        internal static class Tests
        {
            // Map3DView.MeasureFloorRanges: (level, minY - slack, maxY + slack) per band, then Prep.MeasureWindows.
            private static Prep Floors(params (int Level, float MinY, float MaxY)[] bands)
            {
                var prep = new Prep
                {
                    FloorRanges = bands.Select(b => (b.Level, b.MinY - FloorFaceSlack, b.MaxY + FloorFaceSlack)).ToArray()
                };
                prep.MeasureWindows();
                return prep;
            }

            // Woods.map.json and RezervBase.map.json as captured (floors minY..maxY).
            private static Prep Woods() => Floors((-1, -4.5f, 1f), (0, 6.5f, 19f), (1, 20.5f, 25.5f));
            private static Prep Reserve() => Floors((0, -15f, 0.5f), (1, 1.5f, 3f), (2, 4.5f, 6f));

            public static void Run(T t)
            {
                const int Tint = Prep.NoPictureFloor;

                t.Case("CaptureWindow: the top floor's camera draws 50 m deeper, a lower one to the next floor", () =>
                {
                    CaptureWindow(20.5f, 25.5f, float.PositiveInfinity, out var low, out var high);
                    t.Eq(-30.5f, low, "top low");
                    t.Eq(325.5f, high, "top high");
                    CaptureWindow(-4.5f, 1f, 6.5f, out low, out high);
                    t.Eq(-5.5f, low, "basement low");
                    t.Eq(6f, high, "basement high");
                });

                t.Case("on a band: the floor it stands on", () =>
                {
                    var woods = Woods();
                    t.Eq(0, woods.FloorForFace(10f, -1), "y 10 filed basement");
                    t.Eq(-1, woods.FloorForFace(0f, 1), "y 0 filed floor 2");
                });

                t.Case("Woods shore bank: y -14 filed under the basement (-4.5..1) goes to the top floor", () =>
                {
                    var woods = Woods();
                    t.Eq(1, woods.FloorForFace(-14f, -1), "y -14");
                    t.Eq(1, woods.FloorForFace(-16f, -1), "y -16");
                    t.Eq(1, woods.FloorForFace(-8.5f, -1), "y -8.5");
                });

                t.Case("Woods outdoor ground y 3 filed under Ground: top first, not the nearer basement", () =>
                    t.Eq(1, Woods().FloorForFace(3f, 0), "y 3 filed ground"));

                t.Case("the filed floor keeps a face its camera drew", () =>
                    t.Eq(-1, Woods().FloorForFace(3f, -1), "y 3 filed basement"));

                t.Case("Reserve gap face (y 3.75, between Floor 2 and Floor 3)", () =>
                {
                    var reserve = Reserve();
                    t.Eq(1, reserve.FloorForFace(3.75f, 1), "filed Floor 2: its camera drew it");
                    t.Eq(2, reserve.FloorForFace(3.75f, 0), "filed Ground: the top floor's window");
                });

                t.Case("nearest: neither filed nor top window holds it, the nearest other window does", () =>
                {
                    var deep = Floors((-3, -140f, -130f), (-2, -100f, -90f), (-1, -60f, -50f), (0, 0f, 10f));
                    t.Eq(-2, deep.FloorForFace(-75f, -1), "one other window");
                    t.Eq(-2, deep.FloorForFace(-100.7f, -1), "the nearer of two windows");
                });

                t.Case("on two bands at equal distance: the higher floor wins, in either order", () =>
                {
                    // ranges -0.5..10.5 and 9.5..20.5: y 10 is 0 m from both declared bands
                    t.Eq(1, Floors((0, 0f, 10f), (1, 10f, 20f)).FloorForFace(10f, 0), "lower listed first");
                    t.Eq(1, Floors((1, 10f, 20f), (0, 0f, 10f)).FloorForFace(10f, 0), "higher listed first");
                });

                t.Case("nearest windows at equal distance: the higher floor wins", () =>
                {
                    // y -80.75 is off every band, outside the filed (-1) and top windows, inside the windows of -3 (band
                    // to -81.5) and -2 (band from -80), 0.75 m from each
                    var tie = Floors((-3, -100f, -81.5f), (-2, -80f, -70f), (-1, -60f, -50f), (0, 0f, 10f));
                    t.Eq(-2, tie.FloorForFace(-80.75f, -1), "tie");
                });

                t.Case("tint: no picture drew that height", () =>
                {
                    t.Eq(Tint, Woods().FloorForFace(-40f, -1), "Woods y -40");
                    t.Eq(Tint, Reserve().FloorForFace(-60f, 0), "Reserve y -60");
                });

                t.Case("a filed floor with no range is kept", () =>
                    t.Eq(7, Woods().FloorForFace(-14f, 7), "filed 7"));

                RunSlices(t);
                RunBuiltAndDrawn(t);
            }

            // --- (e) S2: built versus drawn --------------------------------------------------------------------------

            private static string Join(IEnumerable<int> levels) => string.Join(",", levels);

            /// <summary>Map3DView's roof owner before S2, over what it built then (today's DrawnLevels set): FloorAt(roof)
            /// ?? FloorAt(selected), null meaning the drawing floor's own material.</summary>
            private static string TodayRoofOwner(int roofLevel, int selected, List<int> drawn, int drawing)
            {
                string FloorAt(int level) => drawn.Contains(level) ? "L" + level : null;
                return FloorAt(roofLevel) ?? FloorAt(selected) ?? "L" + drawing;
            }

            /// <summary>Every selection from 3 under the lowest band to 3 over the highest, on <paramref name="levels"/>
            /// (in the order given): the window holds the active band, at most 5, consecutive; the DRAWN part equals today's
            /// DrawnLevels; the ground band (the active one) is today's top drawn band; every roof owner a drawn floor can
            /// meet is today's.</summary>
            private static void SameAsToday(T t, List<int> levels, string map)
            {
                var sorted = levels.OrderBy(l => l).ToList();

                for (var selected = sorted.First() - 3; selected <= sorted.Last() + 3; selected++)
                {
                    var built = BandWindow(levels, selected, Cap);
                    var active = ActiveLevelOf(levels, selected);
                    var drawn = ShownOf(built, levels, selected);
                    var today = TodayDrawn(selected, sorted);
                    var at = $"{map} selected {selected}";

                    t.Eq(Join(today), Join(drawn), $"{at}: drawn = today's DrawnLevels");
                    t.Eq(today.Last(), active, $"{at}: active = today's ground band");
                    t.True(built.Contains(active), $"{at}: the window holds the active band");
                    var room = sorted.Contains(selected) ? Cap : Cap - 1;
                    t.Eq(Math.Min(sorted.Count, Math.Max(today.Count, room)), built.Count, $"{at}: window size");

                    // the pictures held: the window's plus the flat path's selected floor - at most the larger of 5 (the cap
                    // less one) and what was held before the window, so no steady-state eviction
                    var held = built.Union(new[] { selected }).Count();
                    var heldToday = today.Union(new[] { selected }).Count();
                    t.True(held <= Math.Max(Cap, heldToday), $"{at}: {held} pictures held, {heldToday} before the window");
                    var from = sorted.IndexOf(built[0]);
                    t.Eq(Join(sorted.Skip(from).Take(built.Count)), Join(built), $"{at}: window consecutive");

                    foreach (var drawing in drawn)
                        for (var roof = sorted.First() - 1; roof <= sorted.Last() + 1; roof++)
                        {
                            var owner = RoofOwner(roof, active, selected, l => built.Contains(l) ? "L" + l : null) ?? "L" + drawing;
                            t.Eq(TodayRoofOwner(roof, selected, today, drawing), owner, $"{at}: roof {roof} on floor {drawing}");
                        }
                }
            }

            private static void RunBuiltAndDrawn(T t)
            {
                t.Case("S2: on 1 to 5 bands every band is built, and what is drawn is today's at every selection", () =>
                {
                    for (var n = 1; n <= 5; n++)
                    {
                        var levels = Enumerable.Range(-1, n).ToList();
                        t.Eq(Join(levels), Join(BandWindow(levels, -1, Cap)), $"{n} band(s): all built at the bottom");
                        SameAsToday(t, levels, $"{n} band(s)");
                        levels.Reverse();
                        SameAsToday(t, levels, $"{n} band(s) listed top first");
                    }

                    SameAsToday(t, new List<int> { 0, 2, 5 }, "levels with gaps");
                });

                t.Case("S2: 8 bands - bottom, middle and top selections draw today's set out of a window of 5", () =>
                {
                    var levels = Enumerable.Range(0, 8).ToList();
                    SameAsToday(t, levels, "8 bands");

                    t.Eq("0,1,2,3,4", Join(BandWindow(levels, 0, Cap)), "bottom: window");
                    t.Eq("0", Join(ShownOf(BandWindow(levels, 0, Cap), levels, 0)), "bottom: drawn");
                    t.Eq("0,1,2,3,4", Join(BandWindow(levels, 4, Cap)), "middle: window");
                    t.Eq("0,1,2,3,4", Join(ShownOf(BandWindow(levels, 4, Cap), levels, 4)), "middle: drawn");
                    t.Eq("3,4,5,6,7", Join(BandWindow(levels, 7, Cap)), "top: window");
                    t.Eq("3,4,5,6,7", Join(ShownOf(BandWindow(levels, 7, Cap), levels, 7)), "top: drawn");
                });

                t.Case("S2: the fallback - a selection under every band draws the lowest band only", () =>
                {
                    var levels = new List<int> { 2, 0, 1 };
                    t.Eq(0, ActiveLevelOf(levels, -1), "active");
                    t.Eq("0", Join(ShownOf(BandWindow(levels, -1, Cap), levels, -1)), "drawn");
                    t.Eq(7, ActiveLevelOf(new List<int>(), 7), "no band: the selection itself");

                    // drawn by the ACTIVE band, not the selection: a selection under or over every band
                    t.True(Shown(0, 0) && !Shown(1, 0), "Shown: at or below the active band");
                    t.Eq("0", Join(ShownOf(new List<int> { 0, 1, 2 }, levels, -1)), "selection -1: the lowest band drawn");
                    t.Eq("0,1", Join(ShownOf(new List<int> { 0, 1 }, new List<int> { 0, 1 }, 5)), "selection 5 over bands 0, 1: both drawn");
                });

                t.Case("S2: a roof filed above the active floor takes the active floor's picture though that floor is built", () =>
                {
                    var built = new List<int> { 0, 1, 2, 3, 4 };
                    string FloorAt(int l) => built.Contains(l) ? "L" + l : null;
                    t.Eq("L0", RoofOwner(2, 0, 0, FloorAt), "roof on 2, active 0");
                    t.Eq("L1", RoofOwner(1, 3, 3, FloorAt), "roof on 1, active 3");
                    t.Eq("L3", RoofOwner(-2, 3, 3, FloorAt), "roof below the window, active 3");
                    t.Eq(null, RoofOwner(-2, 9, 9, FloorAt), "neither built");
                    t.Eq(null, RoofOwner(4, 3, 7, FloorAt), "selection 7 is no band: the drawing floor's own, as before");
                });
            }

            // --- (d) slice stops -------------------------------------------------------------------------------------------

            /// <summary>The picture cache's cap less one, what DrawnLevels keeps (DynamicMapsLibrary.MaxResidentSprites is 6).</summary>
            private const int Cap = 5;

            private const SliceKind Section = SliceKind.Section;
            private const SliceKind Notch = SliceKind.Notch;
            private const SliceKind Rebuild = SliceKind.Rebuild;

            private static (int Level, float MinY, float MaxY)[] WoodsBands() =>
                new[] { (-1, -4.5f, 1f), (0, 6.5f, 19f), (1, 20.5f, 25.5f) };

            private static (int Level, float MinY, float MaxY)[] ReserveBands() =>
                new[] { (0, -15f, 0.5f), (1, 1.5f, 3f), (2, 4.5f, 6f) };

            // the plan's illustration from MapExtentProbe's band notes (the garage's bins end at 19, the ground's at 24)
            private static (int Level, float MinY, float MaxY)[] InterchangeBands() =>
                new[] { (-1, 17.5f, 19.5f), (0, 20.5f, 24.5f), (1, 26.5f, 28.5f), (2, 35.5f, 37.5f) };

            // Shoreline/Icebreaker-shaped: 8 bands of 1.5 m, 4 m apart
            private static (int Level, float MinY, float MaxY)[] EightBands() =>
                Enumerable.Range(0, 8).Select(l => (l, 4f * l, 4f * l + 1.5f)).ToArray();

            private static List<SliceLayer> LayersOf((int Level, float MinY, float MaxY)[] bands, int unusable, SliceLayer[] extra) =>
                bands.Select(b => new SliceLayer { Level = b.Level, Name = "L" + b.Level, MinY = b.MinY, MaxY = b.MaxY, Usable = b.Level != unusable })
                    .Concat(extra).ToList();

            /// <summary>The inputs as BeginBuild makes them: one usable layer per band (but <paramref name="unusable"/>), plus
            /// <paramref name="extra"/> layers; the floor ranges as MeasureFloorRanges fills them (both ends inside +-1000,
            /// slack added).</summary>
            private static List<SliceStop> Stops(int selected, (int Level, float MinY, float MaxY)[] bands, int unusable = int.MinValue,
                params SliceLayer[] extra)
            {
                var levels = bands.Select(b => b.Level).ToList();
                var ranges = bands.Where(b => b.MinY > -1000f && b.MaxY < 1000f && !(b.MaxY < b.MinY))
                    .Select(b => (b.Level, b.MinY - FloorFaceSlack, b.MaxY + FloorFaceSlack)).ToList();

                return MeasureSlices(LayersOf(bands, unusable, extra), levels, ranges, selected, Cap);
            }

            /// <summary>Map3DView.CutHeight as it was at e13d08e, written out again with a literal 0.3: the cut the floor
            /// picker gives when <paramref name="selected"/> is chosen. Every stop's home must be exactly this.</summary>
            private static float TodayCut(int selected, IEnumerable<int> bandLevels, IEnumerable<SliceLayer> layers)
            {
                if (!bandLevels.Any(l => l > selected)) return float.NaN;

                var found = layers.Where(l => l.Level == selected).ToList();
                if (found.Count == 0 || float.IsNaN(found[0].MaxY)) return float.NaN;   // no layer, or no GameBounds

                var top = found[0].MaxY;
                if (float.IsNaN(top) || float.IsInfinity(top) || top >= 1000f) return float.NaN;

                return top + 0.3f;
            }

            /// <summary>Map3DView.DrawnLevels as it is at e13d08e: the bands at or below the selection, the highest 5, else
            /// the lowest band.</summary>
            private static List<int> TodayDrawn(int selected, List<int> bandLevels)
            {
                var levels = bandLevels.Where(l => l <= selected).OrderBy(l => l).ToList();
                if (levels.Count == 0) return new List<int> { bandLevels.Min() };
                while (levels.Count > Cap) levels.RemoveAt(0);
                return levels;
            }

            private static void Near(T t, float expected, float actual, string what) =>
                t.True(Math.Abs(expected - actual) < 1e-4f, $"{what}: expected {expected}, got {actual}");

            private static void Stop(T t, SliceStop stop, int level, SliceKind kind, float home, float ceiling)
            {
                t.Eq(level, stop.Level, $"level {level}");
                t.Eq(kind, stop.Kind, $"level {level} kind");
                if (float.IsNaN(home)) t.True(float.IsNaN(stop.Home), $"level {level} home: expected NaN (no cut), got {stop.Home}");
                else Near(t, home, stop.Home, $"level {level} home");
                if (float.IsNaN(ceiling)) t.True(float.IsNaN(stop.Ceiling), $"level {level} ceiling: expected NaN, got {stop.Ceiling}");
                else Near(t, ceiling, stop.Ceiling, $"level {level} ceiling");
            }

            /// <summary>Every stop's home equals today's cut for that floor, bit for bit, at every selection.</summary>
            private static void HomesAreToday(T t, (int Level, float MinY, float MaxY)[] bands, params SliceLayer[] extra)
            {
                var layers = LayersOf(bands, int.MinValue, extra);
                var levels = bands.Select(b => b.Level).ToList();

                foreach (var selected in layers.Select(l => l.Level).Distinct())
                    foreach (var stop in Stops(selected, bands, int.MinValue, extra))
                        t.Eq(TodayCut(stop.Level, levels, layers), stop.Home, $"selected {selected}: home of {stop.Level} = today's CutHeight");
            }

            private static void RunSlices(T t)
            {
                t.Case("slices, Woods: basement section 1.3 -> 6.0, ground a 19.3 notch, top no cut", () =>
                {
                    foreach (var selected in new[] { -1, 0, 1 })
                    {
                        var s = Stops(selected, WoodsBands());
                        t.Eq(3, s.Count, "stops");
                        Stop(t, s[0], -1, Section, 1.3f, 6f);
                        Stop(t, s[1], 0, Notch, 19.3f, 20f);
                        Stop(t, s[2], 1, Notch, float.NaN, float.NaN);
                        t.True(s.All(x => x.Known), "all known");
                        t.Eq("L-1,L0,L1", string.Join(",", s.Select(x => x.Name)), "names");
                    }

                    HomesAreToday(t, WoodsBands());
                });

                t.Case("slices, Reserve: two notches (0.8, 3.3) and the top", () =>
                {
                    var s = Stops(0, ReserveBands());
                    t.Eq(3, s.Count, "stops");
                    Stop(t, s[0], 0, Notch, 0.8f, 1f);
                    Stop(t, s[1], 1, Notch, 3.3f, 4f);
                    Stop(t, s[2], 2, Notch, float.NaN, float.NaN);
                    HomesAreToday(t, ReserveBands());
                });

                t.Case("slices, Interchange: garage notch 19.8, ground 24.8 -> 26.0, floor 2 28.8 -> 35.0, top", () =>
                {
                    foreach (var selected in new[] { -1, 0, 1, 2 })
                    {
                        var s = Stops(selected, InterchangeBands());
                        t.Eq(4, s.Count, "stops");
                        Stop(t, s[0], -1, Notch, 19.8f, 20f);
                        Stop(t, s[1], 0, Section, 24.8f, 26f);
                        Stop(t, s[2], 1, Section, 28.8f, 35f);
                        Stop(t, s[3], 2, Notch, float.NaN, float.NaN);
                    }

                    // listed top first: the next floor up is found by height, not by list position
                    var r = Stops(0, InterchangeBands().Reverse().ToArray());
                    Stop(t, r[0], -1, Notch, 19.8f, 20f);
                    Stop(t, r[1], 0, Section, 24.8f, 26f);
                    Stop(t, r[2], 1, Section, 28.8f, 35f);
                    HomesAreToday(t, InterchangeBands());
                });

                t.Case("slices, a +-2000 placeholder band: discrete stops only, NaN home for the unknown floor", () =>
                {
                    var bands = new[] { (-1, -4.5f, 1f), (0, -2000f, 2000f), (1, 20.5f, 25.5f) };
                    var s = Stops(1, bands);
                    t.Eq(3, s.Count, "stops");
                    Stop(t, s[0], -1, Notch, 1.3f, 20f);   // a 18.7 m stretch - but sections are off with an unknown band
                    Stop(t, s[1], 0, Notch, float.NaN, float.NaN);
                    Stop(t, s[2], 1, Notch, float.NaN, float.NaN);
                    t.True(!s[1].Known && s[0].Known && s[2].Known, "only the placeholder floor is unknown");
                    HomesAreToday(t, bands);

                    // a layer with no height band at all (no GameBounds): the same fallback
                    var noBounds = new[] { (-1, -4.5f, 1f), (0, float.NaN, float.NaN), (1, 20.5f, 25.5f) };
                    t.True(Stops(1, noBounds).All(x => x.Kind == Notch), "no bounds: all notches");
                    HomesAreToday(t, noBounds);
                });

                t.Case("slices, one band: a single no-cut stop, so no slider", () =>
                {
                    var one = new[] { (0, -5f, 60f) };
                    var s = Stops(0, one);
                    t.True(s.Count < 2, "fewer than two stops");
                    Stop(t, s[0], 0, Notch, float.NaN, float.NaN);
                    HomesAreToday(t, one);
                });

                t.Case("slices, 8 bands: a window of 5 holding the selection, the rest rebuild notches", () =>
                {
                    var bands = EightBands();
                    var levels = bands.Select(b => b.Level).ToList();

                    foreach (var (selected, from) in new[] { (0, 0), (3, 0), (5, 1), (7, 3) })
                    {
                        var window = BandWindow(levels, selected, Cap);
                        t.Eq(string.Join(",", Enumerable.Range(from, 5)), string.Join(",", window), $"selected {selected}: window");

                        // today's built set is the window's bottom, ending at the selection
                        var drawn = TodayDrawn(selected, levels);
                        t.Eq(string.Join(",", drawn), string.Join(",", window.Take(drawn.Count)), $"selected {selected}: today's set first");

                        var s = Stops(selected, bands);
                        t.Eq(8, s.Count, "stops");

                        foreach (var stop in s)
                        {
                            var inside = stop.Level >= from && stop.Level < from + 5;
                            var kind = !inside ? Rebuild : stop.Level == 7 ? Notch : Section;

                            if (stop.Level == 7) Stop(t, stop, 7, kind, float.NaN, float.NaN);
                            else Stop(t, stop, stop.Level, kind, 4f * stop.Level + 1.8f, 4f * stop.Level + 3.5f);
                        }
                    }

                    HomesAreToday(t, bands);
                });

                t.Case("slices: today's fallback window, a layer with no band and a failed picture are rebuild notches", () =>
                {
                    var levels = new List<int> { 2, 0, 1 };
                    t.Eq("0,1,2", string.Join(",", BandWindow(levels, -1, Cap)), "selection under every band");
                    t.Eq("0", string.Join(",", BandWindow(levels, -1, 1)), "cap 1: today's lowest band");
                    t.Eq("1,2", string.Join(",", BandWindow(levels, 2, 2)), "cap 2: the highest at or below");
                    t.Eq("0,1", string.Join(",", BandWindow(levels, 1, 2)), "cap 2: today's set fills it");
                    t.Eq("0,1", string.Join(",", BandWindow(levels, 0, 2)), "cap 2: then up");

                    // a basement with a picture but no band (DrawnLevels' fallback case)
                    var basement = new SliceLayer { Level = -1, Name = "Basement", MinY = -10f, MaxY = -6f, Usable = true };
                    var upper = WoodsBands().Where(b => b.Level >= 0).ToArray();
                    var s = Stops(-1, upper, int.MinValue, basement);
                    t.Eq(3, s.Count, "stops");
                    Stop(t, s[0], -1, Rebuild, -5.7f, float.NaN);
                    Stop(t, s[1], 0, Notch, 19.3f, 20f);
                    Stop(t, s[2], 1, Notch, float.NaN, float.NaN);
                    HomesAreToday(t, upper, basement);

                    // Woods' basement picture failed: a rebuild notch, the others as before
                    var f = Stops(0, WoodsBands(), -1);
                    Stop(t, f[0], -1, Rebuild, 1.3f, 6f);
                    Stop(t, f[1], 0, Notch, 19.3f, 20f);
                });

                t.Case("slices: two floors with the same minY are not each other's next floor", () =>
                {
                    // as the loop at HEAD: only a minY strictly above counts, so both look past each other to 10
                    var bands = new[] { (0, 0f, 2f), (1, 0f, 5f), (2, 10f, 12f) };
                    var ranges = bands.Select(x => (x.Item1, x.Item2 - FloorFaceSlack, x.Item3 + FloorFaceSlack)).ToArray();
                    t.Eq(10f, NextMinY(ranges, 0), "next of 0");
                    t.Eq(10f, NextMinY(ranges, 1), "next of 1");

                    var s = Stops(0, bands);
                    Stop(t, s[0], 0, Section, 2.3f, 9.5f);
                    Stop(t, s[1], 1, Section, 5.3f, 9.5f);
                    Stop(t, s[2], 2, Notch, float.NaN, float.NaN);
                    HomesAreToday(t, bands);
                });

                t.Case("slices: two layers on one level - the first is the stop, as LayerOf finds it", () =>
                {
                    var duplicate = new SliceLayer { Level = 0, Name = "Dup", MinY = 6.5f, MaxY = 50f, Usable = false };
                    var s = Stops(0, WoodsBands(), int.MinValue, duplicate);
                    t.Eq(3, s.Count, "stops");
                    t.Eq("L-1,L0,L1", string.Join(",", s.Select(x => x.Name)), "names");
                    Stop(t, s[1], 0, Notch, 19.3f, 20f);
                    HomesAreToday(t, WoodsBands(), duplicate);
                });

                t.Case("NextMinY is MeasureWindows' old loop, bit for bit", () =>
                {
                    var random = new Random(1907);

                    for (var trial = 0; trial < 200; trial++)
                    {
                        var ranges = Enumerable.Range(0, 1 + random.Next(8))
                            .Select(l => (l, (float)(random.NextDouble() * 200 - 100) - FloorFaceSlack, 0f)).ToArray();

                        for (var i = 0; i < ranges.Length; i++)
                        {
                            // Prep.MeasureWindows' loop at e13d08e
                            var minY = ranges[i].Item2 + FloorFaceSlack;
                            var nextMinY = float.PositiveInfinity;

                            for (var j = 0; j < ranges.Length; j++)
                            {
                                var low = ranges[j].Item2 + FloorFaceSlack;
                                if (j != i && low > minY && low < nextMinY) nextMinY = low;
                            }

                            t.Eq(nextMinY, NextMinY(ranges, i), $"trial {trial} range {i}");
                        }
                    }
                });
            }
        }
    }
}
