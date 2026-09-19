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
    /// a FINGERPRINT of the item database - every template's id, the numbers a build is judged on, the
    /// conflicts and footprint that decide whether it assembles at all, plus its slots - and any entry
    /// saved under a different fingerprint is discarded unread.
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
        // 10: the proof machinery is gone. The squeeze loop no longer stops at a cost lower bound and no
        // longer falls through into shrinking part count, so a history solved under 9 can differ from what
        // 10 would find; the carry-over branch keeps the builds and re-measures them.
        //
        // 11: THE OBJECTIVE ITSELF CHANGED. A part is now priced at the cheapest trader cash price rather
        // than at the handbook, and a part no trader sells costs handbook times a multiple - so a build that
        // was cheapest under 10 can be beaten under 11 by one that swaps a flea-only part for a trader-sold
        // equivalent. Nothing about the search changed; the number it is minimising did, which breaks the
        // same promise in the same way. The carry-over branch keeps the builds and re-measures them, so an
        // existing install loses no work: every build is still legal, still verified before use, and the
        // search starts from it rather than from nothing.
        private const int CurrentSolver = 11;

        /// <summary>Shape of the file itself, for the day a field is added.</summary>
        private const int CurrentSchema = 1;

        private static readonly JsonSerializerOptions FileOptions = new() { WriteIndented = true };

        /// <summary>Where the history lives. Overridable by QUESTTREE_CACHE_DIR so a test can point it
        /// at scratch space instead.
        ///
        /// That override exists because the two things this file is for pull in opposite directions. The
        /// determinism gate has to DELETE the history before every boot, or fifty boots test one search and
        /// forty-nine reads of its answer. Training has to ACCUMULATE it. Sharing one path meant the gate
        /// quietly ate the one artifact worth shipping, twice, before anybody noticed.</summary>
        private static string Folder =>
            Environment.GetEnvironmentVariable("QUESTTREE_CACHE_DIR")
            ?? System.IO.Path.Combine(AppContext.BaseDirectory, "user", "mods", "QuestTree", "cache");

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

            /// <summary>Parts this build needs that the weapon does not ship with - THE OBJECTIVE. What the
            /// player pays in roubles and trader trips, which is the thing a quest build is actually trying
            /// to keep small.
            ///
            /// Remembered beside the part count because the write rule is lexicographic on both, so a boot
            /// has to know what the remembered build cost as well as how big it was.</summary>
            public int Changes { get; set; }

            /// <summary>What this build costs under the handbook pricing it was last measured with - THE
            /// OBJECTIVE - and the PerPurchase it was measured at. Informational: the write rule compares
            /// against the incumbent's cost measured LIVE, never against this number, because a stored zero
            /// once read as a perfect score and blocked every improvement (ledger, defect 7).</summary>
            public long Cost { get; set; }

            public long PerPurchase { get; set; }

            /// <summary>Boots that have tried to beat this build and failed. Not a promise that it is
            /// minimal - it is the amount of evidence that it might be, and the honest thing to show
            /// beside a build nobody has proved anything about.</summary>
            public int Attempts { get; set; }

            /// <summary>The solver's skeleton size for this requirement - WeaponSolver.Result.Floor, which is
            /// what the search stops at and explicitly not a lower bound. Recorded with the build and, today,
            /// read by nothing - kept because it is in every shipped history and costs one integer. This doc
            /// used to call it "the proven lower bound"; it was never that, and the bound it was confused
            /// with no longer exists.</summary>
            public int Floor { get; set; }

            /// <summary>The most nodes a search of this build has ever opened. The worst build on this
            /// install searches 376,062 and a typical one a fraction of that, which is the difference
            /// between a scheduler that can run ten cheap builds in the time one expensive one takes and
            /// one that cannot tell them apart.</summary>
            public int Nodes { get; set; }

            /// <summary>Which thresholds this build meets with nothing to spare - the reason it cannot lose a
            /// part, as far as anything here knows.
            ///
            /// Remembered rather than recomputed because it is what makes the next search DIRECTED: the only
            /// way to drop a part is to buy slack on one of these first, and a search that does not know
            /// which they are spends its effort uniformly over five thresholds instead of on the one that is
            /// actually in the way.
            ///
            /// Measured, never guessed: it comes from the same reading of the assembled build that decides
            /// whether the thresholds are met at all. An empty list means nobody has looked yet, not that
            /// nothing binds.</summary>
            public List<string> Binding { get; set; } = new();
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

        /// <summary>Whether this launch is TRAINING - grinding the builds cheaper until the training cap
        /// or the operator stops it - rather than doing the single round every launch does.
        ///
        /// A launch-time environment variable and NOTHING ELSE, which is a deliberate narrowing. A marker
        /// file did the same job and was the better developer experience, but it survives being copied: a
        /// release zipped up from a machine that had been training would carry it, and every user who
        /// installed that release would burn a core for three minutes on every start without ever asking
        /// to. An environment variable cannot be packaged by accident. Set it for the launch you want to
        /// train and nothing is left behind for the next one.
        ///
        /// Every launch improves the builds either way. This only decides whether it stops after one round
        /// or keeps going, which is the difference between a player's machine helping a little and a
        /// developer's machine producing the history that ships.</summary>
        public bool Training =>
            Environment.GetEnvironmentVariable("QUESTTREE_TRAIN") is "1" or "true" or "TRUE" or "yes";

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

        /// <summary>Records what a remembered build COSTS - the parts the weapon does not already wear.
        ///
        /// Needed for the same reason Note is: the common case is a boot that serves the remembered build
        /// unchanged, and that boot measures it, so it knows the cost. Without this the field stays at zero
        /// for every build written before the objective existed - and zero does not mean "nothing to buy", it
        /// means "nobody has looked", which is the difference between a monotonicity check and a blank.</summary>
        public void Changed(string key, int changes, long cost, long perPurchase)
        {
            lock (_lock)
            {
                Load();

                if (!_file!.Builds.TryGetValue(key, out var build)) return;
                if (build.Changes == changes && build.Cost == cost && build.PerPurchase == perPurchase) return;

                build.Changes = changes;
                build.Cost = cost;
                build.PerPurchase = perPurchase;
                _dirty = true;
            }
        }

        /// <summary>Records what the thresholds looked like on a build nobody changed.
        ///
        /// Separate from Put because the common case is a boot that serves the remembered build unchanged -
        /// and that boot still measures it, so it still knows which thresholds are on the line. Without this
        /// the hint would only ever be written for a build that had just got smaller, so the builds that
        /// most need directing - the ones that have resisted for hundreds of rounds - would be the only ones
        /// with nothing recorded.</summary>
        public void Note(string key, IReadOnlyCollection<string> binding)
        {
            if (binding.Count == 0) return;

            lock (_lock)
            {
                Load();

                if (!_file!.Builds.TryGetValue(key, out var build)) return;

                // Only when it actually changed: the file is rewritten on every flush and a boot that
                // learned nothing new should not make it look like it did.
                if (build.Binding.Count == binding.Count && !binding.Except(build.Binding).Any()) return;

                build.Binding = binding.ToList();
                _dirty = true;
            }
        }

        /// <summary>Records what searching this build costs, so effort can be spent where it buys most.</summary>
        public void Cost(string key, int nodes)
        {
            if (nodes <= 0) return;

            lock (_lock)
            {
                Load();

                if (!_file!.Builds.TryGetValue(key, out var build) || build.Nodes >= nodes) return;

                build.Nodes = nodes;
                _dirty = true;
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

        public void Put(
            string key,
            IReadOnlyList<WeaponSolver.FittedPart> parts,
            int floor,
            IReadOnlyCollection<string>? binding = null,
            int changes = 0,
            long cost = 0,
            long perPurchase = 0)
        {
            lock (_lock)
            {
                Load();

                _file!.Builds.TryGetValue(key, out var previous);

                var replacement = new CachedBuild
                {
                    Floor = floor,
                    Changes = changes,
                    Cost = cost,
                    PerPurchase = perPurchase,
                    Binding = binding == null ? new List<string>() : binding.ToList(),
                    Parts = parts.Select(part => new CachedPart
                    {
                        Slot = part.SlotName,
                        Template = part.Template.ToString(),
                        Depth = part.Depth,
                        Parent = part.Parent
                    }).ToList()
                };

                // What a search of this requirement costs is a fact about the quest and the item data, not
                // about the parts that happened to be remembered, so it outlives the build. Attempts is about
                // the build that just lost, and starts at zero. (A bound, its stability and its falsification
                // evidence used to be carried here too; all three went with the proof machinery.)
                if (previous != null) replacement.Nodes = previous.Nodes;

                _file.Builds[key] = replacement;

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
                        // The solver version too, or the carry-over never ends: the file kept the
                        // old number, was written back with it, and every boot after the 9 -> 10 bump
                        // re-opened all sixty builds and reported "carried over" - a check that
                        // could not stop failing, found by reading the log of the second boot.
                        _file.SolverVersion = CurrentSolver;
                        _dirty = true;

                        Authoritative = false;

                        // EVERYTHING MEASURED IS RE-OPENED. A build carried over from another install is still
                        // a build and the verifier will say whether it is a legal one - but its cost, its
                        // change count and what a search of it spent were all facts about a different set of
                        // parts, and a stale number would let a worse build hold its place under the write
                        // rule.
                        foreach (var build in found.Builds.Values)
                        {
                            // Zero, not "unknown": a build's cost is recomputed the first time this boot
                            // measures it, and a stale count would let a worse build hold its place under the
                            // lexicographic write rule.
                            build.Changes = 0;
                            build.Cost = 0;
                            build.PerPurchase = 0;
                            build.Nodes = 0;
                            build.Binding.Clear();
                        }

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
        /// Every template's id, the numbers a build is judged on, its conflicts and grid footprint, and the
        /// shape of its slots. A new weapon mod changes it and every cached answer is discarded - which is
        /// the point: new parts may make a smaller build possible, and serving the old one would hide that
        /// rather than be stale in some visible way.
        ///
        /// Conflicts and footprint were added after a review: neither changes a build's SCORE, which is why
        /// they were missed, but both change whether it can be built - and a cached build the modding screen
        /// refuses is worse than a stale one.
        ///
        /// Adding them moves the hash, so every existing install pays the carry-over branch once. That keeps
        /// Parts - the builds themselves survive - and zeroes what was measured around them: Nodes, Cost,
        /// Changes, PerPurchase and Binding. Worth the invalidation, and worth stating rather than calling it
        /// a re-solve.
        ///
        /// WHAT IS DELIBERATELY NOT IN HERE: the trader tables, even though the shared objective now reads
        /// prices out of them. Two reasons, and the second is the important one.
        ///
        /// A cached build is a claim about LEGALITY and a claim about being cheapest, and only the first is
        /// what this hash protects. Changing what a scope costs cannot make a build unassemblable; it can
        /// only mean a cheaper one now exists, which is a reason to keep searching - exactly what the
        /// per-profile Fingerprint over in PartAvailability says about trader stock, and handled the same
        /// way: the answer is stale in quality, never wrong.
        ///
        /// And hashing them would end the shipped history. Every trader mod, every price tweak, every
        /// assort edit anybody installs would move the hash, so no modded install would ever load the seed
        /// on its fast path again - it would take the carry-over branch on every single boot, permanently
        /// non-authoritative, re-measuring sixty builds forever to discover the same answer. The shipped
        /// seed is the most valuable thing in this file and it is worth more than reacting to a price
        /// change the search will notice by itself on its next pass.</summary>
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

                // Conflicts, and the grid footprint the size check reads. Neither was here, and both decide
                // whether a cached build still ASSEMBLES: a mod update that introduces a conflict between a
                // handguard and a foregrip a stored build uses together left this hash unchanged, so Load()
                // took the fast path, Sound() passed - the verifier has no opinion on conflicts - and the
                // game's modding screen refused the build. The ExtraSize half was caught only because the
                // verifier recomputes the footprint, which costs a re-solve per boot instead of an
                // invalidation.
                foreach (var conflict in (props.ConflictingItems ?? Enumerable.Empty<MongoId>())
                             .Select(c => c.ToString())
                             .OrderBy(c => c, StringComparer.Ordinal))
                    text.Append('x').Append(conflict);

                // 1, not 0, because that is what WeaponGraph and the verifier read a null as - defaulting
                // to 0 here would hash an absent Width the same as an explicit 0 while the two behave
                // differently everywhere else.
                text.Append(':').Append(props.Width ?? 1).Append('x').Append(props.Height ?? 1)
                    .Append(':').Append(props.ExtraSizeUp ?? 0)
                    .Append(':').Append(props.ExtraSizeDown ?? 0)
                    .Append(':').Append(props.ExtraSizeLeft ?? 0)
                    .Append(':').Append(props.ExtraSizeRight ?? 0)
                    .Append(props.ExtraSizeForceAdd == true ? "!" : "");

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

