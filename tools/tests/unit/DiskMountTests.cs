// MapStore.ChooseMount (B1, 2026-10-05): the server's free-disk checks (DiskShortfall, SizeCeilings -> ClampStoreToDisk)
// measure the filesystem that holds the maps folder. On Unix every path root is "/", so the mount is the longest
// DriveInfo root that prefixes the folder's path, compared with a trailing '/' so a sibling with a longer name is not a
// match; on Windows the path root is kept exactly as before; no match falls back to the path root.
//
// @@MUTATE DiskMount :: prefix.Length > bestLength && :: @@
// @@MUTATE DiskMount :: var prefix = root.EndsWith('/') ? root : root + "/"; :: var prefix = root;@@
// @@MUTATE DiskMount :: if (windows || string.IsNullOrEmpty(fullPath)) return pathRoot; :: if (string.IsNullOrEmpty(fullPath)) return pathRoot;@@
// @@MUTATE DiskMount :: return best ?? pathRoot; :: return best;@@
using System;
using System.Collections.Generic;

namespace UnitTests.DiskMount
{
    internal static class MapStore
    {
// @@REGION DiskMount@@

        internal static class Tests
        {
            private static string Unix(string path, params string[] roots) => ChooseMount(path, roots, false, "/");

            internal static void Run(T t)
            {
                t.Case("only / mounted", () =>
                {
                    t.Eq("/", Unix("/srv/spt/user/mods/QuestTreeServer/maps", "/"), "the root filesystem");
                });

                t.Case("/ plus /mnt/data, folder under each", () =>
                {
                    t.Eq("/mnt/data", Unix("/mnt/data/spt/maps", "/", "/mnt/data"), "under /mnt/data");
                    t.Eq("/mnt/data", Unix("/mnt/data/spt/maps", "/mnt/data", "/"), "order of the drive list does not matter");
                    t.Eq("/", Unix("/home/spt/maps", "/", "/mnt/data"), "under / only");
                    t.Eq("/mnt/data", Unix("/mnt/data", "/", "/mnt/data"), "the mount point itself");
                });

                t.Case("nested mounts: the longest prefix wins", () =>
                {
                    t.Eq("/mnt/data/spt", Unix("/mnt/data/spt/maps", "/", "/mnt/data", "/mnt/data/spt", "/mnt"), "deepest mount");
                });

                t.Case("sibling-prefix trap", () =>
                {
                    t.Eq("/", Unix("/mnt/data2/x", "/", "/mnt/data"), "/mnt/data2 is not under /mnt/data");
                    t.Eq("/mnt/data2", Unix("/mnt/data2/x", "/", "/mnt/data", "/mnt/data2"), "its own mount wins");
                });

                t.Case("trailing-slash root", () =>
                {
                    t.Eq("/mnt/data/", Unix("/mnt/data/spt/maps", "/", "/mnt/data/"), "a root given with its trailing '/'");
                    t.Eq("/mnt/data/", Unix("/mnt/data/", "/", "/mnt/data/"), "a path with a trailing '/'");
                });

                t.Case("ordinal on Unix", () =>
                {
                    t.Eq("/", Unix("/MNT/DATA/spt", "/", "/mnt/data"), "case differs: not the same directory on Unix");
                });

                t.Case("Windows semantics unchanged", () =>
                {
                    t.Eq(@"D:\", ChooseMount(@"D:\SPT\user\mods\QuestTreeServer\maps", new[] { @"C:\", @"D:\SPT\" }, true, @"D:\"),
                        "the path root, whatever the drive list says");
                    t.Eq(@"D:\", ChooseMount(@"D:\SPT\maps", new[] { @"D:\SPT\maps", "/" }, true, @"D:\"),
                        "a listed root equal to the folder still does not replace the drive");
                    t.Eq(@"\host\share\", ChooseMount(@"\host\share\SPT\maps", new[] { @"C:\" }, true, @"\host\share\"),
                        "a UNC root is handed on as before (DriveInfo refuses it; the callers' catch keeps the old fallback)");
                });

                t.Case("no matching root falls back to the path root", () =>
                {
                    t.Eq("/", Unix("/srv/maps"), "no drives listed");
                    t.Eq("/", Unix("/srv/maps", "/mnt/data", "", null), "only unrelated or empty roots");
                    t.Eq(null, ChooseMount("/srv/maps", new string[0], false, null), "no path root either: unknown");
                });
            }
        }
    }
}
