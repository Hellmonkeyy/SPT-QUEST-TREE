// QuestGraph/MenuMapHost.cs HostScenes (review findings B6/B7/B17/B18): which scenes a menu-host run may unload, and
// which unload steps may run. A scene that appears during a run is REQUESTED (the scene of one of the host's own loads,
// claimed by that load's handle, or by name only while unique and before any foreign signal) or a STRAY. A stray is the
// run's only before the freeze and never when it is one of the game's own scenes. At the freeze every stray is dropped
// (left loaded) - also one that appeared during a host load - and no diff, later pass, sweep or restore runs.
//
// The simulator replays a run the way MenuMapHost.NoteAppeared / ClaimFor / Freeze use the helpers.
//
// @@MUTATE HostScenes :: return !frozen; :: return true;@@
// @@MUTATE HostScenes :: if (inBaseline || gameScene) return false; :: if (inBaseline) return false;@@
// @@MUTATE HostScenes :: => requested && !gameScene; :: => !gameScene;@@
// @@MUTATE HostScenes :: else dropped.Add(item); :: else kept.Add(item);@@
// @@MUTATE HostScenes :: return handle == knownHandle; :: return handle != 0;@@
// @@MUTATE HostScenes :: newScenesOfThatName == 1 :: newScenesOfThatName >= 1@@
// @@MUTATE HostScenes :: == 1 && !frozen; :: == 1;@@
// @@MUTATE HostScenes :: GameScenePrefixes = { "Vendors_" }; :: GameScenePrefixes = { };@@
// @@MUTATE HostScenes :: pass == 1 || (!frozen && pass <= maxPasses) :: pass == 1 || pass <= maxPasses@@
// @@MUTATE HostScenes :: !frozen && haveBaseline :: haveBaseline@@
// @@MUTATE HostScenes :: MayDiff(bool frozen) => !frozen; :: MayDiff(bool frozen) => true;@@
// @@MUTATE HostScenes :: if (newHandlesOfThatName.Count >= 2) sawTwo = true; :: if (false) sawTwo = true;@@
// @@MUTATE HostScenes :: if (sawTwo || newHandlesOfThatName.Count != 1) return 0; :: if (newHandlesOfThatName.Count != 1) return 0;@@
// @@MUTATE HostScenes :: if (gameSceneNames == null) return true; :: if (gameSceneNames == null) return false;@@
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnitTests.HostScenes
{
    internal static class MenuMapHost
    {
// @@REGION HostScenes@@
    }

    internal static class Tests
    {
        private const int MaxUnloadPasses = 8;

        private static readonly string[] GameNames = { "bunker_2", "EmptyScene", "MenuUIScene", "Ligtkeeper_test_scene" };

        private sealed class Noted
        {
            internal string Name;
            internal int Handle;
            internal bool Requested;
        }

        /// <summary>One run's scene set, kept as MenuMapHost keeps RunContext.Appeared.</summary>
        private sealed class Sim
        {
            private readonly HashSet<int> _baseline;
            private readonly List<(int Handle, string Name)> _listed = new List<(int, string)>();
            internal readonly List<Noted> Set = new List<Noted>();
            internal readonly List<Noted> Dropped = new List<Noted>();

            /// <summary>The host's loads: entry name -> its scene's handle (0 = never seen), and whether claimed.</summary>
            internal readonly Dictionary<string, int> Loads = new Dictionary<string, int>();
            internal readonly HashSet<string> Claimed = new HashSet<string>();
            internal bool Frozen;

            internal Sim(params int[] baseline)
            {
                _baseline = new HashSet<int>(baseline);
                foreach (var h in baseline) _listed.Add((h, "menu_" + h));
            }

            internal IEnumerable<string> Owned => Set.Select(n => n.Name + "#" + n.Handle);

            /// <summary>A scene SceneManager lists (still loading) - before it has appeared.</summary>
            internal void Listed(int handle, string name) => _listed.Add((handle, name));

            internal void Appear(int handle, string name)
            {
                if (!_listed.Any(l => l.Handle == handle)) _listed.Add((handle, name));
                if (_baseline.Contains(handle) || Set.Any(n => n.Handle == handle)) return;

                var game = MenuMapHost.HostScenes.IsGameScene(name, GameNames);
                if (game) Freeze();

                string claimedFor = null;
                if (!game)
                {
                    foreach (var load in Loads)
                    {
                        if (Claimed.Contains(load.Key)) continue;
                        var nameMatches = string.Equals(load.Key, name, StringComparison.OrdinalIgnoreCase);
                        if (load.Value == 0 && !nameMatches) continue;
                        var same = load.Value != 0 ? 0 : _listed.Count(l => !_baseline.Contains(l.Handle) && l.Name == name);
                        if (MenuMapHost.HostScenes.IsRequested(nameMatches, handle, load.Value, same, Frozen)) { claimedFor = load.Key; break; }
                    }
                }

                if (!MenuMapHost.HostScenes.Owns(_baseline.Contains(handle), game, claimedFor != null, Frozen)) return;
                if (claimedFor != null) Claimed.Add(claimedFor);
                Set.Add(new Noted { Name = name, Handle = handle, Requested = claimedFor != null });
            }

            /// <summary>The host's own load of <paramref name="name"/> whose scene gets <paramref name="handle"/>.</summary>
            internal void Load(string name, int handle, bool handleSeen = true)
            {
                Loads[name] = handleSeen ? handle : 0;
                Appear(handle, name);
            }

            internal void Freeze()
            {
                if (Frozen) return;
                Frozen = true;
                MenuMapHost.HostScenes.FreezeSet(Set, n => n.Requested, n => MenuMapHost.HostScenes.IsGameScene(n.Name, GameNames), Dropped);
            }
        }

        private static string Show(IEnumerable<string> names) => string.Join(",", names.OrderBy(n => n, StringComparer.Ordinal));

        public static void Run(T t)
        {
            t.Case("a normal run owns exactly its requested scenes, never the menu's", () =>
            {
                var run = new Sim(1, 2);
                run.Load("customs_a", 10);
                run.Load("customs_b", 11);
                run.Load("customs_AI", 12, handleSeen: false);
                run.Appear(1, "menu_1");
                t.Eq("customs_AI#12,customs_a#10,customs_b#11", Show(run.Owned), "owned set");
                t.True(run.Set.All(n => n.Requested), "a requested scene not marked requested");
                t.True(MenuMapHost.HostScenes.MayDiff(false), "a normal run may not diff");
                t.True(MenuMapHost.HostScenes.MaySweep(false), "a normal run may not sweep");
                t.True(MenuMapHost.HostScenes.MayRestore(false, true), "a normal run may not restore");
            });

            t.Case("a stray before any foreign signal is still owned on a normal run (pass 2 catches hosted scripts' loads)", () =>
            {
                var run = new Sim(1);
                run.Load("customs_a", 10);
                run.Appear(20, "customs_a_child");
                t.Eq("customs_a#10,customs_a_child#20", Show(run.Owned), "owned set");
                for (var pass = 1; pass <= MaxUnloadPasses; pass++)
                    t.True(MenuMapHost.HostScenes.MayRunPass(pass, false, MaxUnloadPasses), $"pass {pass} refused in a normal run");
                t.True(!MenuMapHost.HostScenes.MayRunPass(MaxUnloadPasses + 1, false, MaxUnloadPasses), "the pass cap is gone");
            });

            t.Case("a known game scene name is never owned, even with no foreign signal", () =>
            {
                foreach (var name in new[] { "bunker_2", "Vendors_Fence", "vendors_scripts", "EmptyScene", "Ligtkeeper_test_scene" })
                {
                    t.True(MenuMapHost.HostScenes.IsGameScene(name, GameNames), $"'{name}' is not a game scene");
                    var run = new Sim(1);
                    run.Appear(30, name);
                    t.Eq("", Show(run.Owned), $"'{name}' owned");
                    t.True(run.Frozen, $"'{name}' did not freeze the run");
                }

                foreach (var name in new[] { "customs_vendors_shop", "bigmap_AI", "" })
                    t.True(!MenuMapHost.HostScenes.IsGameScene(name, GameNames), $"'{name}' read as a game scene");
                t.True(MenuMapHost.HostScenes.IsGameScene("Vendors_Prapor", new string[0]), "the Vendors_ prefix does not apply alone");
                foreach (var requested in new[] { false, true })
                foreach (var frozen in new[] { false, true })
                    t.True(!MenuMapHost.HostScenes.Owns(false, true, requested, frozen),
                        $"a game scene owned (requested {requested}, frozen {frozen})");
            });

            t.Case("a vendor scene during a host load is never owned; a stray from that load is dropped at the freeze", () =>
            {
                var run = new Sim(1);
                run.Load("customs_a", 10);
                run.Listed(11, "customs_b");
                run.Loads["customs_b"] = 11;              // in flight, handle seen
                run.Appear(40, "modded_extra");           // a stray during the host load: owned so far...
                t.Eq("customs_a#10,modded_extra#40", Show(run.Owned), "owned before the trader dialog");
                run.Appear(41, "Vendors_Fence");          // the trader dialog's scene, before its world exists
                t.True(run.Frozen, "the vendor scene did not freeze the set");
                t.Eq("customs_a#10", Show(run.Owned), "owned after the freeze");
                t.Eq("modded_extra#40", Show(run.Dropped.Select(n => n.Name + "#" + n.Handle)), "dropped at the freeze");
                run.Appear(11, "customs_b");              // the in-flight load still lands as the run's
                t.Eq("customs_a#10,customs_b#11", Show(run.Owned), "owned after the in-flight load arrived");
            });

            t.Case("a raid's scenes loading after the freeze are never owned", () =>
            {
                var run = new Sim(1);
                run.Load("customs_a", 10);
                run.Freeze();
                run.Appear(50, "factory_entry");
                run.Appear(51, "factory_rest");
                t.Eq("customs_a#10", Show(run.Owned), "owned set after the raid");
                t.True(!MenuMapHost.HostScenes.Owns(false, false, false, true), "a stray after the freeze is owned");
                t.True(!MenuMapHost.HostScenes.MayDiff(true), "a frozen run diffs");
                t.True(!MenuMapHost.HostScenes.MaySweep(true), "a frozen run sweeps");
                t.True(!MenuMapHost.HostScenes.MayRestore(true, true), "a frozen run restores the menu over the raid");
                t.True(MenuMapHost.HostScenes.MayRunPass(1, true, MaxUnloadPasses), "a frozen run skips its own pass 1");
                for (var pass = 2; pass <= MaxUnloadPasses + 1; pass++)
                    t.True(!MenuMapHost.HostScenes.MayRunPass(pass, true, MaxUnloadPasses), $"a frozen run runs pass {pass}");
            });

            t.Case("a same-name scene with a different handle from the in-flight one is not claimed", () =>
            {
                var run = new Sim(1);
                run.Load("customs_a", 10);
                run.Listed(11, "customs_b");
                run.Loads["customs_b"] = 11;
                run.Freeze();                             // a raid on the same map starts
                run.Appear(77, "customs_b");              // the raid's copy
                t.Eq("customs_a#10", Show(run.Owned), "the raid's same-name scene was claimed");
                run.Appear(11, "customs_b");
                t.Eq("customs_a#10,customs_b#11", Show(run.Owned), "the host's own scene was not claimed");
                t.True(!MenuMapHost.HostScenes.IsRequested(true, 77, 11, 0, false), "a different handle claimed before a freeze");
            });

            t.Case("with no handle seen, the name claims only a unique scene and only before the freeze", () =>
            {
                t.True(MenuMapHost.HostScenes.IsRequested(true, 5, 0, 1, false), "a unique name before the freeze not claimed");
                t.True(!MenuMapHost.HostScenes.IsRequested(true, 5, 0, 2, false), "two same-name scenes: one claimed");
                t.True(!MenuMapHost.HostScenes.IsRequested(true, 5, 0, 1, true), "the name claimed after the freeze");
                t.True(!MenuMapHost.HostScenes.IsRequested(false, 5, 0, 1, false), "another name claimed");

                var run = new Sim(1);
                run.Loads["customs_b"] = 0;
                run.Freeze();
                run.Appear(60, "customs_b");
                t.Eq("", Show(run.Owned), "a name-only claim after the freeze");
            });

            t.Case("a load's handle is recorded only while exactly one scene of its name exists, and never after two did", () =>
            {
                var sawTwo = false;
                t.Eq(0, MenuMapHost.HostScenes.LearnHandle(new int[0], ref sawTwo), "a handle with no scene listed");
                t.Eq(11, MenuMapHost.HostScenes.LearnHandle(new[] { 11 }, ref sawTwo), "the only scene of the name not recorded");
                t.True(!sawTwo, "one scene read as two");

                // The host's copy activated (still counted, loaded or not) and a same-map raid's copy is loading.
                sawTwo = false;
                t.Eq(0, MenuMapHost.HostScenes.LearnHandle(new[] { 11, 77 }, ref sawTwo), "a handle recorded with two candidates");
                t.True(sawTwo, "two candidates not remembered");

                // Two have existed, one finished (left the list): the remaining one is still never recorded.
                t.Eq(0, MenuMapHost.HostScenes.LearnHandle(new[] { 77 }, ref sawTwo), "a handle recorded after two had existed");
                t.True(sawTwo, "the two-candidates memory was lost");
            });

            t.Case("unreadable game scene names are fail-safe: every named scene is the game's, so nothing is owned", () =>
            {
                foreach (var name in new[] { "customs_a", "bigmap_AI", "Vendors_Fence" })
                {
                    t.True(MenuMapHost.HostScenes.IsGameScene(name, null), $"'{name}' not a game scene with the names unreadable");
                    t.True(!MenuMapHost.HostScenes.Owns(false, MenuMapHost.HostScenes.IsGameScene(name, null), true, false),
                        $"'{name}' owned with the names unreadable");
                }

                var set = new List<Noted> { new Noted { Name = "customs_a", Requested = true } };
                var dropped = new List<Noted>();
                MenuMapHost.HostScenes.FreezeSet(set, n => n.Requested, n => MenuMapHost.HostScenes.IsGameScene(n.Name, null), dropped);
                t.Eq(0, set.Count, "a scene kept by the freeze with the names unreadable");
            });

            t.Case("the freeze keeps requested scenes in order and drops every stray", () =>
            {
                var set = new List<Noted>
                {
                    new Noted { Name = "a", Requested = true }, new Noted { Name = "s1" }, new Noted { Name = "b", Requested = true },
                    new Noted { Name = "s2" }, new Noted { Name = "Vendors_Fence", Requested = true },
                };
                var dropped = new List<Noted>();
                var left = MenuMapHost.HostScenes.FreezeSet(set, n => n.Requested, n => MenuMapHost.HostScenes.IsGameScene(n.Name, GameNames), dropped);
                t.Eq(2, left, "count left");
                t.Eq("a,b", string.Join(",", set.Select(n => n.Name)), "kept, in order");
                t.Eq("s1,s2,Vendors_Fence", string.Join(",", dropped.Select(n => n.Name)), "dropped, in order");
            });

            t.Case("baseline scenes are never owned", () =>
            {
                foreach (var game in new[] { false, true })
                foreach (var requested in new[] { false, true })
                foreach (var frozen in new[] { false, true })
                    t.True(!MenuMapHost.HostScenes.Owns(true, game, requested, frozen),
                        $"baseline scene owned (game {game}, requested {requested}, frozen {frozen})");
                var run = new Sim(1, 2);
                run.Loads["menu_2"] = 2;
                run.Appear(2, "menu_2");
                t.Eq("", Show(run.Owned), "a baseline scene owned through a claim");
                t.True(!MenuMapHost.HostScenes.MayRestore(false, false), "a restore without a baseline");
            });
        }
    }
}
