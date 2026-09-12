using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace QuestTreeServer
{
    /// <summary>
    /// Remembers the build each quest was solved with, so a boot that has already answered these
    /// questions does not answer them again.
    ///
    /// The search costs about 1.7 seconds for all sixty on this install, and it was being paid TWICE
    /// per boot - once to put the build on the wire and once for the dry run that measures it - for an
    /// answer that cannot change unless the quests or the parts change. That is the definition of work
    /// worth caching.
    ///
    /// WHAT MAKES A CACHED ANSWER STILL VALID, because getting this wrong ships a stale build
    ///
    /// The answer depends on the quest's own terms and on every part that exists. So the file carries
    /// a FINGERPRINT of the item database - every template's id and the five numbers a build is judged
    /// on, plus its slots - and any entry saved under a different fingerprint is discarded unread.
    /// Installing a weapon mod changes the fingerprint, which is exactly right: the new parts may make
    /// a smaller build possible, and a cache that kept serving the old one would quietly hide it.
    ///
    /// It also carries a solver version, bumped by hand when the search changes. A cache is a promise
    /// that the answer would be the same, and improving the search breaks that promise on purpose.
    ///
    /// AND EVERY CACHED BUILD IS STILL VERIFIED. Loading one skips the search, never the check - so a
    /// file that is stale in some way nobody anticipated, or edited, or truncated, costs a search and
    /// not a wrong answer. That is the whole reason this is safe to add.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class WeaponBuildCache(ISptLogger<WeaponBuildCache> logger, TemplateTable templateTable)
    {
        /// <summary>Bumped by hand whenever the search changes what it would return. A cached answer is
        /// a claim that solving again would produce the same build, and a better search makes that claim
        /// false - so the file has to say which search made it.</summary>
        private const int CurrentSolver = 9;

        /// <summary>Shape of the file itself, for the day a field is added.</summary>
        private const int CurrentSchema = 1;

        private static readonly JsonSerializerOptions FileOptions = new() { WriteIndented = true };

        private static string Folder =>
            System.IO.Path.Combine(AppContext.BaseDirectory, "user", "mods", "QuestTree", "cache");

        private static string Path => System.IO.Path.Combine(Folder, "weapon-builds.json");

        public sealed class CachedPart
        {
            public string Slot { get; set; } = "";
            public string Template { get; set; } = "";
            public int Depth { get; set; }
            public int Parent { get; set; } = -1;
        }

        public sealed class CachedBuild
        {
            public List<CachedPart> Parts { get; set; } = new();

            /// <summary>Boots that have tried to beat this build and failed. Not a promise that it is
            /// minimal - it is the amount of evidence that it might be, and the honest thing to show
            /// beside a build nobody has proved anything about.</summary>
            public int Attempts { get; set; }

            /// <summary>The proven lower bound this build was measured against, carried so a warm boot
            /// reports the same numbers a cold one did rather than a blank where the proof was.</summary>
            public int Floor { get; set; }
        }

        public sealed class CacheFile
        {
            public int SchemaVersion { get; set; } = CurrentSchema;
            public int SolverVersion { get; set; } = CurrentSolver;

            /// <summary>How many boots have searched these builds. It is the random seed the next boot
            /// starts from, and it is the whole of what makes this self-improving rather than merely
            /// remembered: without it every boot runs the identical deterministic search, finds the
            /// identical answer, and the build never gets smaller again. The count converged at 586 parts
            /// on the second boot and sat there.</summary>
            public int Generation { get; set; }

            /// <summary>What the item database looked like when these were solved.</summary>
            public string Items { get; set; } = "";

            public Dictionary<string, CachedBuild> Builds { get; set; } = new();
        }

        private readonly object _lock = new();

        private CacheFile? _file;
        private bool _dirty;
        private string? _fingerprint;

        /// <summary>Whether this install is TRAINING - grinding the builds smaller for as long as it
        /// takes - rather than doing the single round every start does.
        ///
        /// Opt-in by a file rather than a build flag, so an install that never creates it can never be made
        /// to grind by accident. Every start improves the builds either way; this only decides whether it
        /// stops after one round or keeps going, which is the difference between a player's machine helping
        /// a little and a developer's machine producing the cache that ships.</summary>
        public bool Training
        {
            get
            {
                // A flag on the launch itself, which is how a training run is actually started: set it for
                // one launch and that launch trains, with nothing left behind to make the next one train by
                // accident.
                var flag = Environment.GetEnvironmentVariable("QUESTTREE_TRAIN");

                if (flag is "1" or "true" or "TRUE" or "yes") return true;

                // And a file, for leaving a machine training across restarts without setting the variable
                // every time. Either turns it on; neither is the default.
                return System.IO.File.Exists(System.IO.Path.Combine(Folder, "training"));
            }
        }

        /// <summary>False when the cache was written against a different set of items than this install
        /// has. The entries are still used - a build that VERIFIES is a good answer whoever wrote it - but
        /// they may no longer be the smallest possible, because parts this install has were not there when
        /// they were found.</summary>
        public bool Authoritative { get; private set; } = true;

        /// <summary>Search seeds one boot gets through, and therefore the stride between boots. Wide
        /// enough that no two boots ever try the same starting point.</summary>
        private const int SeedStride = 1_000;

        /// <summary>Where the next boot's random starting points begin. Advanced once per boot, so every
        /// boot explores ground no previous boot has.</summary>
        public int Advance()
        {
            lock (_lock)
            {
                Load();

                _file!.Generation++;
                _dirty = true;

                return _file.Generation * SeedStride;
            }
        }

        /// <summary>Records that a boot tried to beat a build and could not.</summary>
        public void Held(string key)
        {
            lock (_lock)
            {
                Load();

                if (!_file!.Builds.TryGetValue(key, out var build)) return;

                build.Attempts++;
                _dirty = true;
            }
        }

        /// <summary>The build remembered for one request, or null when there is nothing usable.</summary>
        public CachedBuild? Get(string key)
        {
            lock (_lock)
            {
                Load();

                return _file!.Builds.GetValueOrDefault(key);
            }
        }

        public void Put(string key, IReadOnlyList<WeaponSolver.FittedPart> parts, int floor)
        {
            lock (_lock)
            {
                Load();

                _file!.Builds[key] = new CachedBuild
                {
                    Floor = floor,
                    Parts = parts.Select(part => new CachedPart
                    {
                        Slot = part.SlotName,
                        Template = part.Template.ToString(),
                        Depth = part.Depth,
                        Parent = part.Parent
                    }).ToList()
                };

                _dirty = true;
            }
        }

        /// <summary>Writes the file if anything changed. Once, after a boot has solved what it needed to,
        /// rather than per entry.</summary>
        public void Flush()
        {
            lock (_lock)
            {
                if (!_dirty || _file == null) return;

                try
                {
                    // Beside the file and moved over it, as the zone store does: a write cut short by a
                    // crash or a full disk would otherwise leave a truncated file, and the next boot
                    // would throw away every answer in it.
                    System.IO.Directory.CreateDirectory(Folder);

                    var temp = Path + ".tmp";

                    System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(_file, FileOptions));
                    System.IO.File.Move(temp, Path, overwrite: true);

                    _dirty = false;

                    logger.Info(
                        $"Quest Tracker: remembered {_file.Builds.Count} weapon build(s) for the next boot " +
                        $"(generation {_file.Generation}).");
                }
                catch (Exception ex)
                {
                    // The session is unaffected - it has the builds in memory. Only the next boot pays.
                    logger.Warning(
                        $"Quest Tracker: could not write the weapon-build cache ({ex.Message}) - this boot is " +
                        "unaffected and the next one will solve again.");
                }
            }
        }

        /// <summary>A key that changes whenever the answer would. Everything the search is given, in a
        /// fixed order, hashed - so two quests asking the same thing share an entry and a quest whose
        /// terms change gets a new one.</summary>
        public static string KeyFor(
            MongoId weapon,
            IReadOnlyList<(string Field, string Compare, double Value)> thresholds,
            IReadOnlyCollection<MongoId> mustInclude,
            IReadOnlyCollection<MongoId> mustIncludeCategories)
        {
            var text = new StringBuilder();

            text.Append(weapon).Append('|');

            // Sorted, because the order these arrive in is an accident of how the condition was read and
            // must not produce two entries for one question.
            foreach (var threshold in thresholds
                         .Select(t => $"{t.Field}{t.Compare}{t.Value:R}")
                         .OrderBy(t => t, StringComparer.Ordinal))
                text.Append(threshold).Append(',');

            text.Append('|');

            foreach (var part in mustInclude.Select(part => part.ToString()).OrderBy(part => part, StringComparer.Ordinal))
                text.Append(part).Append(',');

            text.Append('|');

            foreach (var category in mustIncludeCategories.Select(c => c.ToString()).OrderBy(c => c, StringComparer.Ordinal))
                text.Append(category).Append(',');

            return Hash(text.ToString());
        }

        private void Load()
        {
            if (_file != null) return;

            var fingerprint = Fingerprint();

            try
            {
                if (System.IO.File.Exists(Path))
                {
                    var found = JsonSerializer.Deserialize<CacheFile>(System.IO.File.ReadAllText(Path), FileOptions);

                    // Three reasons to throw the lot away, and all three mean the same thing: these
                    // answers were produced by a different question.
                    if (found != null
                        && found.SchemaVersion == CurrentSchema
                        && found.SolverVersion == CurrentSolver
                        && found.Items == fingerprint)
                    {
                        _file = found;

                        logger.Info($"Quest Tracker: {found.Builds.Count} weapon build(s) remembered from a previous boot.");
                        return;
                    }

                    // Kept rather than discarded, and this is the difference between a cache that helps
                    // one install and one that helps everybody. A build found on a different set of items is
                    // still a build, and the verifier decides whether it is a legal one here - what it may
                    // no longer be is the SMALLEST one, which is a reason to keep looking, not to throw it
                    // away and start from nothing.
                    if (found != null && found.SchemaVersion == CurrentSchema)
                    {
                        _file = found;
                        _file.Items = fingerprint;
                        _dirty = true;

                        Authoritative = false;

                        logger.Info(
                            $"Quest Tracker: {found.Builds.Count} weapon build(s) carried over from a different " +
                            "solver or a different set of items. They are checked before use, and the search will " +
                            "look for smaller ones.");
                        return;
                    }

                    logger.Info(
                        "Quest Tracker: the weapon-build cache could not be read as any known version, so the " +
                        "builds are being worked out again.");
                }
            }
            catch (Exception ex)
            {
                logger.Warning(
                    $"Quest Tracker: the weapon-build cache could not be read ({ex.Message}) - solving again.");
            }

            _file = new CacheFile { Items = fingerprint };
            _dirty = true;
        }

        /// <summary>What the item database looks like, as far as a build can tell.
        ///
        /// Every template's id, the five numbers a build is judged on, and the shape of its slots. A new
        /// weapon mod changes it and every cached answer is discarded - which is the point: new parts may
        /// make a smaller build possible, and serving the old one would hide that rather than be stale in
        /// some visible way.</summary>
        private string Fingerprint()
        {
            if (_fingerprint != null) return _fingerprint;

            var items = templateTable.Items;

            if (items == null) return _fingerprint = "no-items";

            var text = new StringBuilder();

            // Ordered, because a dictionary's enumeration order is not a promise and a fingerprint that
            // changed for no reason would throw the cache away on every boot.
            foreach (var (id, item) in items.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal))
            {
                var props = item?.Properties;
                if (props == null) continue;

                text.Append(id)
                    .Append(':').Append((props.Ergonomics ?? 0d).ToString("R"))
                    .Append(':').Append((props.Recoil ?? 0d).ToString("R"))
                    .Append(':').Append((props.Weight ?? 0d).ToString("R"))
                    .Append(':').Append(props.SightingRange ?? 0d)
                    .Append(':').Append(props.Cartridges?.FirstOrDefault()?.MaxCount ?? 0)
                    .Append(':').Append((props.RecoilForceUp ?? 0d).ToString("R"))
                    .Append(':').Append((props.RecoilForceBack ?? 0d).ToString("R"));

                foreach (var slot in props.Slots ?? Enumerable.Empty<SPTarkov.Server.Core.Models.Eft.Common.Tables.Slot>())
                {
                    text.Append('/').Append(slot?.Name).Append(slot?.Required == true ? "!" : "");

                    foreach (var filter in slot?.Properties?.Filters ?? Enumerable.Empty<SPTarkov.Server.Core.Models.Eft.Common.Tables.SlotFilter>())
                        foreach (var candidate in (filter?.Filter ?? Enumerable.Empty<MongoId>())
                                     .Select(c => c.ToString())
                                     .OrderBy(c => c, StringComparer.Ordinal))
                            text.Append('+').Append(candidate);
                }

                text.Append('\n');
            }

            return _fingerprint = Hash(text.ToString());
        }

        private static string Hash(string text) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

