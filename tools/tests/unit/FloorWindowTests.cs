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
using System;
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

// @@REGION FloorWindow@@

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
            }
        }
    }
}
