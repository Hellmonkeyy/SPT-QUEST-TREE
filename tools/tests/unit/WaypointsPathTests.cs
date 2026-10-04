// (a) MenuMapHost.WaypointsBundleFile - DrakiaXYZ-Waypoints' bundle path rule, mirrored for the menu capture (05fd2d6).
// The location ids map to the 11 bundle names Waypoints ships; an id with no bundle gives no file.
//
// @@MUTATE WaypointsPath :: if (name.StartsWith("sandbox")) name = "sandbox"; :: @@
// @@MUTATE WaypointsPath :: if (name.StartsWith("factory4")) name = "factory4"; :: if (name == "factory4_day") name = "factory4";@@
// @@MUTATE WaypointsPath :: name = locationKey.ToLower(); :: name = locationKey;@@
// @@MUTATE WaypointsPath :: return File.Exists(path) ? path : null; :: return path;@@
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnitTests.WaypointsPath
{
    internal static class MenuMapHost
    {
// @@REGION WaypointsPath@@
    }

    internal static class Tests
    {
        private const string Installed = @"C:\Games\SPT\BepInEx\plugins\DrakiaXYZ-Waypoints\navmesh";

        // Every vanilla location id (GameWorld.LocationId) with its bundle name.
        private static readonly (string Id, string Name)[] Known =
        {
            ("bigmap", "bigmap"),
            ("factory4_day", "factory4"),
            ("factory4_night", "factory4"),
            ("Interchange", "interchange"),
            ("laboratory", "laboratory"),
            ("Labyrinth", "labyrinth"),
            ("Lighthouse", "lighthouse"),
            ("RezervBase", "rezervbase"),
            ("Sandbox", "sandbox"),
            ("Sandbox_high", "sandbox"),
            ("Shoreline", "shoreline"),
            ("TarkovStreets", "tarkovstreets"),
            ("Woods", "woods"),
        };

        private static readonly string[] Unknown = { "hideout", "develop", "Terminal", "town" };

        // The 11 bundles Waypoints 1.9.0 ships, as listed in its navmesh folder.
        private static readonly string[] Shipped =
        {
            "bigmap", "factory4", "interchange", "laboratory", "labyrinth", "lighthouse", "rezervbase", "sandbox",
            "shoreline", "tarkovstreets", "woods"
        };

        public static void Run(T t)
        {
            var fake = Path.Combine(Path.GetTempPath(), "questtree-unit-waypoints-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fake);

            try
            {
                foreach (var name in Shipped) File.WriteAllBytes(Path.Combine(fake, name + "-navmesh.bundle"), new byte[0]);

                foreach (var (id, expected) in Known)
                    t.Case($"{id} -> {expected}-navmesh.bundle", () =>
                    {
                        var path = MenuMapHost.WaypointsBundleFile(fake, id, out var name);
                        t.Eq(expected, name, "bundle name");
                        t.Eq(Path.Combine(fake, expected + "-navmesh.bundle"), path, "path");
                    });

                foreach (var id in Unknown)
                    t.Case($"unknown id '{id}' gives no file", () =>
                    {
                        var path = MenuMapHost.WaypointsBundleFile(fake, id, out var name);
                        t.Eq(null, path, "path");
                        t.Eq(id.ToLower(), name, "bundle name looked for");
                    });

                t.Case("a missing bundle gives no file", () =>
                {
                    File.Delete(Path.Combine(fake, "woods-navmesh.bundle"));
                    t.Eq(null, MenuMapHost.WaypointsBundleFile(fake, "Woods", out _), "path");
                });
            }
            finally
            {
                try { Directory.Delete(fake, true); } catch (Exception) { }
            }

            if (!Directory.Exists(Installed))
            {
                t.Skip("the installed Waypoints folder: all 11 bundles reached", "no " + Installed);
                return;
            }

            t.Case("the installed Waypoints folder: all 11 bundles reached, no id unmatched", () =>
            {
                var files = Directory.GetFiles(Installed, "*-navmesh.bundle")
                    .Select(f => Path.GetFileName(f).Replace("-navmesh.bundle", ""))
                    .OrderBy(n => n, StringComparer.Ordinal).ToArray();

                t.Eq(string.Join(",", Shipped), string.Join(",", files), "bundles in " + Installed);

                var reached = new HashSet<string>();
                foreach (var (id, expected) in Known)
                {
                    var path = MenuMapHost.WaypointsBundleFile(Installed, id, out var name);
                    t.True(path != null && File.Exists(path), $"{id}: no file ({name})");
                    if (path != null) reached.Add(Path.GetFileName(path).Replace("-navmesh.bundle", ""));
                }

                t.Eq(string.Join(",", files), string.Join(",", reached.OrderBy(n => n, StringComparer.Ordinal)), "bundles reached by the ids");

                foreach (var id in Unknown)
                    t.Eq(null, MenuMapHost.WaypointsBundleFile(Installed, id, out _), $"unknown id '{id}'");
            });
        }
    }
}
