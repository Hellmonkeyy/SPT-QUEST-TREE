using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Locales;
using SPTarkov.Server.Core.Services.Server;
using SPTarkov.Server.Core.Utils.Json;

namespace QuestTreeServer
{
    /// <summary>
    /// Builds the complete, unfiltered quest list the Quest Tracker client mod draws its tree from.
    ///
    /// This exists because the game's own quest list cannot answer the question. /client/quest/list
    /// is served by QuestHelper.GetClientQuests, which returns a quest only if it is already in the
    /// player's profile, or else passes every one of: side check, edition blacklist, edition
    /// whitelist, seasonal-event check, player-level check, trader-exists check, AND has all of its
    /// AvailableForStart prerequisite quests already satisfied in the profile. That is exactly the
    /// set of quests a progression tree needs to see PAST, so the client has never had the data.
    ///
    /// templateTable.Quests is the same source GetClientQuests filters down from, so this serves
    /// the real thing rather than a reconstruction - deliberately with no filtering whatsoever.
    /// Level-gated, other-faction, seasonal and edition-locked quests are all included and merely
    /// flagged, leaving the client to decide how to present them.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class QuestPayloadBuilder(
        ISptLogger<QuestPayloadBuilder> logger,
        TemplateTable templateTable,
        LocaleService localeService,
        QuestConfig questConfig,
        QuestFacts facts,
        ZoneStore zoneStore,
        WeaponStatModel weaponStatModel,
        WeaponGraph weaponGraph,
        WeaponSolver weaponSolver,
        WeaponBuildVerifier weaponBuildVerifier,
        WeaponBuildCache weaponBuildCache,
        WeaponPresets weaponPresets,
        PartAvailability partAvailability,
        ProfileBuilds profileBuilds,
        PartPrices partPrices,
        SPTarkov.Server.Core.Helpers.Profile.ProfileHelper profileHelper,
        SPTarkov.Server.Core.Servers.SaveServer saveServer) : IOnLoad
    {
        /// <summary>Weapons named by a WeaponAssembly condition on this install, filled while the
        /// quests are mapped.</summary>
        private readonly HashSet<MongoId> _questWeapons = new();

        /// <summary>Every build requirement on this install, paired with the quest that states it,
        /// so the solver can be run over all of them at boot.</summary>
        private readonly List<(string Quest, WeaponBuildDto Build)> _questBuilds = new();

        /// <summary>What the solver said about each build requirement, keyed the way the cache keys it.
        ///
        /// Here because the search was being run TWICE for every quest - once to put the build on the
        /// wire and once for the dry run that measures it - which is the same question asked twice and
        /// paid for twice.</summary>
        private readonly Dictionary<string, WeaponSolver.Result> _solved = new();

        /// <summary>Where this boot's random starting points begin, and how many builds it improved on
        /// what the last boot managed.</summary>
        private int _seed;
        private int _improved;

        /// <summary>The first proven bound seen for each requirement this boot, and how many later answers
        /// disagreed with it. Concurrent because the training threads all write it.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _bounds = new();

        private int _boundsDisagreed;
        private int _boundsChecked;

        /// <summary>Adversarial searches run against builds the prover calls minimal, and how many of them
        /// found a smaller legal build - which would mean the PROVER is wrong.</summary>
        private int _falsifyTries;
        private int _falsified;

        /// <summary>Adversarial searches a single bound needs before the budget moves elsewhere. Evidence
        /// has diminishing returns: the thousandth failed attack on one build says far less than the first
        /// attack on a build nobody has tested.</summary>
        private const int FalsifyEnough = 2_000;

        /// <summary>Whether this launch tries to DISPROVE its own minimality proofs.
        ///
        /// A gate that rewards a higher proven count is an incentive to loosen the prover, and loosening it
        /// is the one direction a prover may never err: a bound that understates the ceiling proves builds
        /// minimal that are not, with no crash and no log line. Every other check here compares the proof
        /// against other things this code believes. This one compares it against reality - it takes the
        /// builds called minimal, ignores the settled flag, spends a long search asking for a strictly
        /// smaller one, and requires the answer to be no.
        ///
        /// Off by default because it is pure cost on a player's machine: nothing it finds makes a build
        /// smaller, it only says whether a claim is false.</summary>
        private static bool Falsifying =>
            Environment.GetEnvironmentVariable("QUESTTREE_FALSIFY") is "1" or "true" or "TRUE" or "yes";

        /// <summary>Where training is searching FROM for each build, which is not always what the cache
        /// holds: a wander round moves this without moving the answer.</summary>
        private readonly Dictionary<string, List<WeaponSolver.FittedPart>> _working = new();

        /// <summary>Requirements whose build matches the fewest PARTS any satisfying build could have.
        ///
        /// It used to be a skip list - a build at its bound could not get smaller, so it was never searched
        /// again. It cannot be one any more. The objective is now the number of parts a player has to BUY,
        /// and a build already as small as possible may still be cheaper to assemble, so every requirement
        /// stays searchable.
        ///
        /// What it still is: a true statement about part count, produced by a bound that cost 566 million
        /// nodes to establish, and the set the falsifier attacks. What it is NOT is a count of builds proven
        /// minimal over the new objective - that needs a lower bound over CHANGES, which does not exist yet,
        /// so that count is reported as zero rather than inheriting this one's number.</summary>
        private readonly HashSet<string> _proven = new();

        /// <summary>Requirements this boot could prove NO lower bound for, which is the opposite end of
        /// _proven rather than a weaker version of it.
        ///
        /// Kept for two reasons, both learned the hard way. It keeps the Warning to one line per key, since
        /// Proven is re-entered on every search for a key that never becomes proven. And it is what tells
        /// the falsifier to attack these: they are the requirements with nothing standing between them and
        /// a smaller build, so they want the adversarial search MORE than a proved one does, and the first
        /// attempt at making their unprovability honest accidentally exempted them from it.
        ///
        /// A ConcurrentDictionary-backed set would be tidier; this is guarded by the same lock as _proven
        /// at every touch, which is the convention the rest of this file already follows.</summary>
        private readonly HashSet<string> _unbounded = new();

        /// <summary>Searches spent this boot. The unit the progress line and both gates count in, since a
        /// round stopped existing when the barrier did.</summary>
        private int _attempts;

        /// <summary>Built while the server starts, for the reason MapMarkerPayloadBuilder gives:
        /// the client's request handler is synchronous on Unity's main thread, so paying for the
        /// first build there froze the game on the first panel open.</summary>
        public Task OnLoadAsync(CancellationToken cancellationToken)
        {
            // Before anything is solved: every boot searches from starting points no previous boot used,
            // which is what lets the answer keep getting smaller instead of settling on whatever the
            // first boot happened to find.
            _seed = weaponBuildCache.Advance();

            GetPayloadJson();

            // After the payload, because the set of weapons to walk is filled while it is built.
            // Deliberately at boot: a cyclic slot graph is uncatchable at runtime, so the walk that
            // would meet one has to happen where a log line is read rather than inside a request.
            weaponGraph.Survey(_questWeapons);
            SurveySolver();
            AuditAvailability();

            // After the survey, because that is what finishes solving: the payload builds what it needs
            // and the survey covers the rest.
            weaponBuildCache.Flush();

            logger.Info(_improved > 0
                ? $"Quest Tracker: this boot found smaller builds for {_improved} requirement(s)."
                : "Quest Tracker: no smaller build found during startup.");

            // And then it keeps going, off the boot path entirely. One generation per boot is a slow clock
            // - eight boots took four minutes of restarting to find two improvements - and there is no
            // reason the search has to stop just because the server has finished starting.
            Improve(cancellationToken);

            return Task.CompletedTask;
        }

        /// <summary>The shared baseline's pricing: the handbook, static and the same for everyone, so the
        /// history it produces ships. Built once; PartPrices reads the environment for PerPurchase.</summary>
        private WeaponSolver.Pricing Handbook =>
            _handbook ??= new WeaponSolver.Pricing
            {
                Price = partPrices.Of,
                PerPurchase = partPrices.PerPurchase
            };

        /// <summary>Runs the solver over every build requirement on this install, once, and says
        /// how many it could satisfy.
        ///
        /// A search is either tractable on real data or it is not, and counting is the only way to
        /// find out. This runs against the FULL parts list rather than what the player can buy,
        /// deliberately: it is asking whether the search works, not whether this profile can afford
        /// the answer, and conflating the two would make a solver bug look like a poor trader level.
        ///
        /// Debug, because it is a developer's question. The one-line summary is Info.</summary>
        private WeaponSolver.Pricing? _handbook;

        private void SurveySolver()
        {
            if (_questBuilds.Count == 0) return;

            var solved = 0;
            var ceiling = 0;

            // Part counts, because "satisfied" says nothing about whether the build is sane. A
            // seventeen-part AKS-74N wearing two identical sights satisfied every threshold too.
            var parts = 0;
            var widestBuild = 0;
            var duplicates = 0;
            var unscorable = 0;
            // How many parts are PROVEN necessary, summed. NOT the solver's own floor, which is the size
            // of one particular mandatory skeleton and is not a bound at all - Gunsmith 18 comes in at
            // 9 parts against a solver floor of 10, which settles it. Only the verifier's number is a
            // bound, so only the verifier's number is reported as one.
            var proven = 0;
            var irreducible = 0;
            var atFloor = 0;

            // Requirements no bound could be argued for. Reported rather than inferred from a gap between
            // two other numbers: this used to be indistinguishable from a bound of int.MaxValue, which is
            // to say indistinguishable from a proof.
            //
            // Counted over every requirement surveyed, where atFloor is counted only over the ones that
            // solved - so the two are not fractions of the same denominator and the log line says which.
            var unbounded = 0;
            var unproven = new List<string>();
            var unverifiable = 0;
            var disagreed = 0;

            // THE OBJECTIVE, summed: parts these builds need that the weapons do not already wear. Reported
            // beside the part count rather than replacing it, because the bounds and the proofs are over part
            // count and would read as claims about this number if it stood alone.
            var changes = 0;
            var cost = 0L;
            var unpriced = 0;
            var withDefaults = 0;
            var failed = new List<string>();

            // The search has a wall-clock ceiling, so how much of it the worst request actually spends
            // is the difference between a solver that always answers and one that answers differently
            // on a busy machine. Reported rather than assumed.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var worst = 0;

            // Why, not just how many. A pass rate with no cause behind it invites guessing at the
            // algorithm, and the reasons separate three very different problems: a search too weak
            // to place the parts a quest names, a search that places them and misses a number, and a
            // model that cannot see the stat at all.
            var reasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var (questName, build) in _questBuilds)
            {
                if (!build.WeaponTemplate.TryParseMongoId(out var weapon)) continue;

                var thresholds = build.Thresholds
                    .Select(t => (t.Field, t.Compare, t.Value))
                    .ToList();

                var mustInclude = new List<MongoId>();
                foreach (var id in build.RequiredItemIds)
                    if (id.TryParseMongoId(out var parsed)) mustInclude.Add(parsed);

                // "Must include a Silencer" is a requirement in exactly the way a named part is, and
                // 16 of the 32 vanilla conditions state one. Stage A kept the raw ids alongside the
                // names for precisely this.
                var mustIncludeCategories = new List<MongoId>();
                foreach (var id in build.RequiredCategoryIds)
                    if (id.TryParseMongoId(out var parsed)) mustIncludeCategories.Add(parsed);

                var result = Solve(weapon, thresholds, mustInclude, mustIncludeCategories);

                if (result.HitCeiling) ceiling++;
                if (result.NodesOpened > worst) worst = result.NodesOpened;

                // THE SCORE IS THE VERIFIER'S, not the solver's. Asking the solver whether the solver
                // is happy cannot find a bug living in the solver's own bookkeeping, and a pass rate
                // measured that way is not evidence of anything.
                var verdict = weaponBuildVerifier.Verify(
                    weapon, result.Parts, thresholds, mustInclude, mustIncludeCategories);

                // PROOF, not the solver's opinion: the fewest parts any satisfying build could have,
                // argued from the item data without looking at the build. A build that matches it is
                // minimum; one above it is only as small as the passes could make it.
                var lowest = weaponBuildVerifier.LowestPossible(weapon, thresholds, mustInclude, mustIncludeCategories);

                // Seeded from the SURVEY, which runs before any training thread exists: these sixty are the
                // single-threaded answers, and every concurrent recomputation for the rest of the boot is
                // compared against them build by build. Sixty matching sixty, not one total matching another
                // - two different wrong bounds can sum to the same number.
                var boundKey = WeaponBuildCache.KeyFor(weapon, thresholds, mustInclude, mustIncludeCategories);

                // Same rule as Proven: an unbounded floor is recorded nowhere and cross-checked against
                // nothing, because it is the absence of a claim rather than a weak one.
                if (!lowest.Unbounded)
                {
                    Crosscheck(boundKey, lowest.Parts, weapon);

                    // Recorded HERE as well as in Proven, and the first version of this missed it: the survey
                    // is the only place a bound is computed on a boot that does no training, so without this
                    // the evidence line reported zeros on every normal launch - a denominator of nothing,
                    // which is the exact failure it was added to prevent.
                    weaponBuildCache.Bound(boundKey, lowest.Parts);
                }
                else
                {
                    unbounded++;
                }

                duplicates += verdict.Duplicates;
                unverifiable += verdict.Unverifiable.Count;
                if (verdict.Unverifiable.Count > 0) unscorable++;

                // Logged loudly and never resolved quietly in the solver's favour: the two agreeing is
                // the only reason to believe either of them.
                if (verdict.Verified != result.Found)
                {
                    disagreed++;
                    logger.Warning(
                        $"Quest Tracker: the solver and the verifier DISAGREE about '{questName}' " +
                        $"({build.WeaponName}) - the solver says {result.Found}, the verifier says " +
                        $"{verdict.Verified}. Solver: [{string.Join("; ", result.Unmet)}]. " +
                        $"Verifier: [{string.Join("; ", verdict.Failures)}]. The verifier is right.");
                }

                if (verdict.Verified)
                {
                    solved++;
                    parts += result.Parts.Count;

                    // Guarded the way atFloor below is, and for the same reason: an unbounded floor
                    // carries Parts = 0 deliberately, so adding it unconditionally folded "no bound could
                    // be proven" into the same total as "zero parts are provably necessary". The evidence
                    // line would have understated itself with nothing to show it had.
                    if (!lowest.Unbounded) proven += lowest.Parts;
                    changes += result.Changes;
                    cost += result.Cost;
                    unpriced += result.Unpriced;
                    if (weaponPresets.For(weapon) != null) withDefaults++;

                    if (verdict.Irreducible) irreducible++;
                    foreach (var spare in verdict.Spare) logger.Warning($"Quest Tracker: '{questName}' carries a spare part - {spare}. The search should not have left it.");

                    // Explicitly, not by relying on Parts being zeroed: this counts the PROVEN MINIMUM
                    // line, and it read every unbounded floor as a match while the absence of a bound was
                    // carried as int.MaxValue.
                    if (!lowest.Unbounded && result.Parts.Count <= lowest.Parts) atFloor++;
                    else unproven.Add(
                        $"{questName} at {result.Parts.Count} parts, proven necessary {lowest.Parts} " +
                        $"({lowest.Reason}), solver floor {result.Floor}, binding " +
                        (result.Binding.Count > 0 ? string.Join("/", result.Binding) : "nothing - it has slack everywhere"));
                    if (result.Parts.Count > widestBuild) widestBuild = result.Parts.Count;

                    logger.Debug(
                        $"Quest Tracker: solved '{questName}' ({build.WeaponName}) with {result.Parts.Count} parts " +
                        $"in {result.NodesOpened:N0} nodes.");
                    continue;
                }

                // Everything the next person to look at a failure needs, and nothing that needs the
                // solver run twice to produce: what the quest asked for, what the search spent, and
                // the parts it settled on. Debug, because on a working install there are none of
                // these; the one-line summary above is what a normal boot says.
                logger.Debug(
                    $"Quest Tracker: unsolved '{questName}' ({build.WeaponName}) wants " +
                    string.Join(", ", build.Thresholds.Select(t => $"{t.Field} {t.Compare} {t.Value:0.##}")) +
                    $"; requires {string.Join(", ", build.RequiredItemNames)}" +
                    $"; searched {result.NodesOpened:N0} nodes" + (result.HitCeiling ? " AND HIT THE BUDGET" : "") +
                    $"; settled on {string.Join(" ", result.Parts.Select(p => $"{p.SlotName}={p.Template}"))}.");

                // A bare template id does not say which part the search could not place, and that is
                // the only question these lines get read to answer. The DTO already carries the
                // names parallel to the ids, so the substitution costs nothing.
                failed.Add(
                    $"{questName} ({build.WeaponName}): " +
                    string.Join("; ", verdict.Failures.Take(8).Select(u => Named(u, build))));

                foreach (var unmet in verdict.Failures)
                {
                    // The reason's first words identify its kind; the numbers after it are per
                    // quest and would make every row unique.
                    var kind = unmet.Contains("NOT REACHABLE", StringComparison.Ordinal)
                        ? "required part not reachable (graph gap)"
                        : unmet.StartsWith("could not fit", StringComparison.OrdinalIgnoreCase)
                            ? "required part reachable but not placed (search gap)"
                            : unmet.StartsWith("required slot", StringComparison.OrdinalIgnoreCase)
                                ? "required slot left empty (not assemblable)"
                                : unmet.StartsWith("no fitted part from", StringComparison.OrdinalIgnoreCase)
                                    ? "no part from a required category"
                                    : unmet.Contains("no value", StringComparison.OrdinalIgnoreCase)
                                        ? $"{unmet.Split(':')[0]}: nothing provides it"
                                        : unmet.Split(' ')[0];

                    reasons[kind] = reasons.TryGetValue(kind, out var count) ? count + 1 : 1;
                }
            }

            logger.Info(
                $"Quest Tracker: weapon solver dry run - {solved} of {_questBuilds.Count} build requirement(s) " +
                $"satisfied from the full parts list" +
                (ceiling > 0 ? $", {ceiling} hit the search budget" : "") +
                $" - {clock.ElapsedMilliseconds:N0} ms for all of them, {worst:N0} nodes for the worst one, " +
                $"{(solved > 0 ? (double)parts / solved : 0d):0.##} parts per build ({parts} total), " +
                $"{widestBuild} at most, {atFloor} of them PROVEN MINIMUM, " +
                (unbounded > 0 ? $"{unbounded} of all {_questBuilds.Count} with NO PROVABLE BOUND, " : "") +
                $"{irreducible} PROVEN IRREDUCIBLE, " +
                $"{proven} of {parts} parts proven necessary" +
                (duplicates > 0 ? $", {duplicates} duplicated part(s)" : "") +
                (unverifiable > 0
                    ? $", {unverifiable} constraint(s) across {unscorable} build(s) that nothing here can score"
                    : "") +
                (disagreed > 0 ? $", SOLVER AND VERIFIER DISAGREED ON {disagreed}" : "") + ".");

            // The objective's own line, and the statement about what is NOT proven about it. Every bound and
            // every proof in this file is over part count; none of them says anything about how many parts a
            // player has to buy, so the count of builds proven minimal on the objective is zero - not the
            // twenty-one carried over from a bound that answers a different question.
            logger.Info(
                $"Quest Tracker: the objective - {cost:N0} roubles across {solved} build(s) at handbook prices " +
                $"plus {partPrices.PerPurchase:N0} per purchase ({(solved > 0 ? cost / Math.Max(1, solved) : 0):N0} per " +
                $"build), over {changes} change(s) from the default presets ({(solved > 0 ? (double)changes / solved : 0d):0.##} " +
                $"per build), {unpriced} purchase(s) with no handbook price; {withDefaults} of them have a default preset " +
                $"to be measured against; {partPrices.Count:N0} templates priced. 0 of {solved} are proven minimal on cost: " +
                "the bound that proves minimality is over PART COUNT, and no bound over cost exists yet.");

            // Per quest, how far the build is above what can be PROVEN necessary. A gap is not waste -
            // the bound omits the chains that named parts have to be routed through, and bounding
            // those exactly is a Steiner tree - but it is the honest measure of what is still open.
            // The denominators. "Zero counterexamples" and "zero comparisons" read identically otherwise,
            // which is the same defect as a check that cannot fail - and this is the claim the whole feature
            // rests on, so it is the last place to leave it implicit.
            var evidence = weaponBuildCache.Evidence();

            logger.Info(
                $"Quest Tracker: the evidence behind the proofs - the least-tested minimal build has survived " +
                $"{evidence.Weakest:N0} adversarial search(es), {evidence.Untested} of them have never been " +
                $"attacked, {evidence.Nodes:N0} node(s) have been spent trying to beat them, and the " +
                $"shortest-standing bound has held for {evidence.Sessions} session(s). Set QUESTTREE_FALSIFY=1 " +
                "to spend a launch attacking them.");

            foreach (var line in unproven)
                logger.Debug($"Quest Tracker: minimality unproven - {line}");

            if (reasons.Count > 0)
                logger.Info(
                    "Quest Tracker: solver misses by - " +
                    string.Join(", ", reasons.OrderByDescending(r => r.Value).Take(6).Select(r => $"{r.Key} x{r.Value}")));

            foreach (var line in failed.Take(12))
                logger.Info($"Quest Tracker: unsolved - {line}");
        }

        /// <summary>One unmet reason with every required-part id in it replaced by the part's
        /// name.</summary>
        private static string Named(string unmet, WeaponBuildDto build)
        {
            unmet = Substitute(unmet, build.RequiredItemIds, build.RequiredItemNames);

            // Categories too. They were left out when this was written, which meant a category
            // failure printed 550aa4cd4bdc2dd8348b456c where it meant "Silencer" - and a category
            // failure is exactly the kind a reader has no other way to identify, since the id names
            // a base class rather than anything they could look up in the handbook.
            return Substitute(unmet, build.RequiredCategoryIds, build.RequiredCategoryNames);
        }

        private static string Substitute(string text, List<string> ids, List<string> names)
        {
            // Pairwise, so a list that has drifted out of step substitutes what it can rather than
            // throwing. The two are built together and cannot drift today; this is here so that a
            // log line never becomes the thing that breaks a payload.
            var count = Math.Min(ids.Count, names.Count);

            for (var i = 0; i < count; i++)
                text = text.Replace(ids[i], names[i], StringComparison.Ordinal);

            return text;
        }

        /// <summary>Condition type that names another quest as a prerequisite.</summary>
        private const string QuestConditionType = "Quest";

        /// <summary>Condition type carrying the quest's player-level requirement.</summary>
        private const string LevelConditionType = "Level";

        /// <summary>Key into Quest.Rewards for the rewards paid on handing the quest in.</summary>
        private const string SuccessRewardKey = "Success";


        private readonly object _buildLock = new();
        private string? _cachedJson;

        private readonly RebuildGate _gate = new(60);

        /// <summary>Builds again now, after a harvest has taught the server where new zones are.
        ///
        /// Needed since 1.9.0 and its absence was a shipped-broken bug waiting to happen: derived
        /// locations come from the harvested zone index, so a raid that harvests a map would give a
        /// quest its PINS - the marker builder rebuilds - while its map on the client stayed empty
        /// until the next server restart. That is the "in the list with no pins, or the reverse"
        /// split the derivation exists to prevent, arriving by the back door.
        ///
        /// Built into a local and swapped, never nulled first: a GET arriving mid-rebuild would
        /// otherwise block on _buildLock, on the client's main thread, behind its 15-second cap. The
        /// cache is therefore never absent, only briefly stale.</summary>
        public void Rebuild()
        {
            lock (_buildLock)
            {
                _gate.Clear();

                try
                {
                    var payload = Build();
                    payload.ModVersion = ModInfo.Version;
                    _cachedJson = JsonSerializer.Serialize(payload, WireJson.Options);
                }
                catch (Exception ex)
                {
                    // Keep serving the previous answer. A harvest is not a reason to lose the tree.
                    logger.Warning($"Quest Tracker: the quest list rebuild after a harvest failed ({ex.Message}) - serving the previous list.");
                }
            }
        }

        /// <summary>Serialized and cached. The quest database itself does not change while the
        /// server is running, but the derived locations on it do - see Rebuild.</summary>
        public string GetPayloadJson()
        {
            if (_cachedJson != null) return _cachedJson;

            lock (_buildLock)
            {
                if (_cachedJson != null) return _cachedJson;
                if (_gate.Paused) return JsonSerializer.Serialize(new QuestPayloadDto(), WireJson.Options);

                QuestPayloadDto payload;

                try
                {
                    payload = Build();
                }
                catch (Exception ex)
                {
                    // Now built at startup, where a throw would abort SPT's boot. An empty list is
                    // a valid payload the client already degrades on - answered, not cached, so a
                    // fault at boot does not mean an empty tree until the server restarts.
                    logger.Error($"Quest Tracker: could not build the quest list - the tree will be empty: {ex}");
                    _gate.PauseThenRewarm(() => GetPayloadJson(),
                        message => logger.Warning($"Quest Tracker: the quest list re-build did not run ({message})."));
                    return JsonSerializer.Serialize(new QuestPayloadDto(), WireJson.Options);
                }

                payload.ModVersion = ModInfo.Version;
                return _cachedJson = JsonSerializer.Serialize(payload, WireJson.Options);
            }
        }

        private QuestPayloadDto Build()
        {
            var payload = new QuestPayloadDto();

            // Emptied first, because this runs again on every rebuild and these are APPENDED to as the
            // quests are walked. Without the clear they grow by sixty each time: the training run, which
            // rebuilds the payload every time it finds a smaller build, reported "20 of 180 are provably
            // minimal" and was searching every requirement three times a round.
            //
            // Latent since Rebuild was written - a harvest rebuild is rare enough that nobody saw it - and
            // only visible once something started rebuilding often.
            lock (_questBuilds)
            {
                _questBuilds.Clear();
                _questWeapons.Clear();
            }

            var quests = templateTable.Quests;
            if (quests == null)
            {
                logger.Error("Quest Tracker: the quest template table is empty - serving nothing.");
                return payload;
            }

            var locale = localeService.GetLocaleDb();

            // Hoisted: built once per payload rather than once per quest, like QuestsByLocation.
            var zoneToMap = zoneStore.ZoneToMap();

            foreach (var quest in quests.Values)
            {
                if (quest == null) continue;

                try
                {
                    payload.Quests.Add(MapQuest(quest, zoneToMap, locale));
                }
                catch (Exception ex)
                {
                    // One malformed quest - which in a modded install is entirely possible - must
                    // not cost the client every other quest in the game.
                    logger.Warning($"Quest Tracker: skipped quest '{quest.Id}': {ex.Message}");
                }
            }

            logger.Info($"Quest Tracker {ModInfo.Stamp}: serving {payload.Quests.Count} quests to the client mod.");
            return payload;
        }

        private QuestDto MapQuest(
            Quest quest,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> zoneToMap,
            Dictionary<string, string> locale)
        {
            var id = quest.Id.ToString();

            return new QuestDto
            {
                Id = id,
                Name = ResolveQuestName(quest, id, locale),
                TraderId = quest.TraderId.ToString(),
                Side = ResolveSide(quest),
                Type = quest.Type.ToString(),
                Level = ResolveLevelRequirement(quest),
                LocationId = ResolveLocationName(quest, locale),
                LocationKey = ResolveLocationKey(quest),
                IsEvent = IsEventQuest(quest.Id),
                EditionRestricted = IsEditionRestricted(quest.Id),
                Prerequisites = MapPrerequisites(quest),
                DerivedLocations = DeriveLocations(quest, zoneToMap, locale),
                Objectives = MapObjectives(quest, locale),
                WeaponBuilds = MapWeaponBuilds(quest, locale),
                Rewards = MapRewards(quest, locale)
            };
        }

        /// <summary>Quest.Name is a locale key, not a display name - the real name lives under
        /// "&lt;questId&gt; name" (the same key QuestHelper.GetQuestNameFromLocale uses). Falls back
        /// to the internal QuestName rather than to a raw key, so the client never renders one.</summary>
        internal static string ResolveQuestName(Quest quest, string id, Dictionary<string, string> locale)
        {
            if (locale.TryGetValue($"{id} name", out var localized) && !string.IsNullOrWhiteSpace(localized))
                return localized;

            if (!string.IsNullOrWhiteSpace(quest.QuestName)) return quest.QuestName!;

            return string.IsNullOrWhiteSpace(quest.Name) ? id : quest.Name;
        }

        /// <summary>Most quests carry Side "Pmc" even when they are faction-locked; the actual
        /// restriction lives in the quest config's Bear/Usec-only sets, which is what
        /// QuestHelper.QuestIsForOtherSide checks.</summary>
        private string ResolveSide(Quest quest)
        {
            if (questConfig.BearOnlyQuests?.Contains(quest.Id) == true) return "Bear";
            if (questConfig.UsecOnlyQuests?.Contains(quest.Id) == true) return "Usec";
            return quest.Side ?? "";
        }

        /// <summary>Whether the game is hiding this quest because it belongs to an event.
        ///
        /// Delegated to the gate itself - see QuestFacts.IsHiddenEventQuest for what the hand-rolled
        /// copy that used to live here got wrong, and why the copy existing at all was the bug.</summary>
        private bool IsEventQuest(MongoId questId) => facts.IsHiddenEventQuest(questId);

        /// <summary>True when the quest carries any game-edition restriction at all - an entry in
        /// the exclusive whitelist, or in any edition's inclusive blacklist.</summary>
        private bool IsEditionRestricted(MongoId questId)
        {
            if (questConfig.ProfileWhitelist?.ContainsKey(questId) == true) return true;

            return questConfig.ProfileBlacklist?.Values.Any(blacklisted => blacklisted.Contains(questId)) == true;
        }

        /// <summary>
        /// The map's internal name ("bigmap", "Labyrinth"), which is what map tools key on.
        ///
        /// Quest.Location is the location's MongoId, not its internal name, so the two never match
        /// without this. The locations table is the authority on the pairing - every location
        /// carries both forms, IdField being the MongoId a quest cites and Id the internal name -
        /// so it is read directly rather than inverting questConfig.LocationIdMap. That config was
        /// the obvious source and is the wrong one: it has no Labyrinth entry, so the Labyrinth
        /// quests resolved to a bare MongoId and matched nothing.
        ///
        /// The pairing itself lives on QuestFacts, which builds it once and hands the same table to
        /// the marker builder - the two must agree about which map a quest is on, and two copies of
        /// that is how they stop agreeing. Falls back to the raw id, which is at least stable, for a
        /// location that is not in the table at all.
        /// </summary>
        private string ResolveLocationKey(Quest quest)
        {
            var location = quest.Location;
            if (string.IsNullOrWhiteSpace(location)) return "";
            if (location.Equals(QuestFacts.AnyLocation, StringComparison.OrdinalIgnoreCase)) return location;

            // Blank rather than the raw id for a location that is not a location. Six vanilla quests
            // declare "marathon", which matches nothing in the table, and the raw-id fallback made
            // that into a map key: the locale answers "marathon Name" with "Transition", so
            // GroupByMap filed them under a phantom map of that name with no image, no floors and no
            // pins, because FindByLocationKey never matched. Blank is already skipped there.
            //
            // "any" is excepted deliberately - it is not a location id either, but it is a real
            // declaration the client names and tests for in three places.
            return facts.LocationIdToKey.TryGetValue(location, out var key) ? key : "";
        }

        /// <summary>The maps a quest is actually done on when it refuses to say.
        ///
        /// A quest whose Location is "any" can still be firmly placed: "Sanitary Investigation -
        /// Part 5" declares "any" and then names five Shoreline zones. Left alone, GroupByMap drops
        /// it and the marker builder never gives it pins, so the quest is missing from the one map
        /// it belongs to.
        ///
        /// A list, not a value: a quest can genuinely span maps - one plants at an aishi_shoreline
        /// zone and an aishi_woods zone - and collapsing that to a single map would be a different
        /// lie. Empty for hand-ins, skills and trader tasks, which have no zones and belong on no
        /// map.
        ///
        /// One line of derivation, shared with MapMarkerPayloadBuilder through QuestFacts. It is not
        /// enough for the two to agree today: the invariant is that a quest never appears in a map's
        /// list without pins, or the reverse, and that only holds if both read the same function.</summary>
        private List<DerivedLocationDto> DeriveLocations(
            Quest quest,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> zoneToMap,
            Dictionary<string, string> locale)
        {
            // Only when the declaration is useless. A quest that names a REAL map is trusted, even
            // if its zones say otherwise - overriding the author would be this mod deciding it knows
            // better.
            if (!facts.IsUselessLocation(quest.Location)) return new List<DerivedLocationDto>();

            var maps = new List<DerivedLocationDto>();

            foreach (var key in facts.MapKeysOfQuest(quest, zoneToMap))
                maps.Add(new DerivedLocationDto { Key = key, Name = facts.LocationNameOf(key, locale) });

            return maps;
        }

        /// <summary>Quest.Location is a raw map id (e.g. 5704e3c2d2720bac5b8b4567), which is no use
        /// on a node subtitle. The map's display name lives in the locale table under
        /// "&lt;locationId&gt; Name". "any" is passed through untouched - the client treats it as
        /// "no specific map" and hides it.</summary>
        private string ResolveLocationName(Quest quest, Dictionary<string, string> locale)
        {
            var location = quest.Location;
            if (string.IsNullOrWhiteSpace(location)) return "";
            if (location.Equals(QuestFacts.AnyLocation, StringComparison.OrdinalIgnoreCase))
                return QuestFacts.AnyLocation;

            // Blank for a non-location, matching ResolveLocationKey: the locale WILL answer
            // "marathon Name" with "Transition", which is precisely how a phantom map got a display
            // name convincing enough to sit in the map dropdown.
            if (!facts.LocationIdToKey.ContainsKey(location)) return "";

            return locale.TryGetValue($"{location} Name", out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : location;
        }

        /// <summary>The player level gate, which lives as a "Level" condition alongside the quest
        /// prerequisites in AvailableForStart. Returns 0 when the quest has no level requirement.
        /// Takes the highest if a quest somehow declares more than one.</summary>
        private static int ResolveLevelRequirement(Quest quest)
        {
            var conditions = quest.Conditions?.AvailableForStart;
            if (conditions == null) return 0;

            var level = 0;

            foreach (var condition in conditions)
            {
                if (condition == null) continue;
                if (!string.Equals(condition.ConditionType, LevelConditionType, StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = Numbers.ToCount(condition.Value);
                if (value > level) level = value;
            }

            return level;
        }

        private static List<PrerequisiteDto> MapPrerequisites(Quest quest)
        {
            var prerequisites = new List<PrerequisiteDto>();

            var conditions = quest.Conditions?.AvailableForStart;
            if (conditions == null) return prerequisites;

            foreach (var condition in conditions)
            {
                if (condition == null) continue;
                if (!string.Equals(condition.ConditionType, QuestConditionType, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var target in TargetIds(condition.Target))
                {
                    if (string.IsNullOrWhiteSpace(target)) continue;

                    prerequisites.Add(new PrerequisiteDto
                    {
                        Target = target,
                        Status = condition.Status?.Select(status => status.ToString()).ToList() ?? new List<string>(),
                        AvailableAfter = condition.AvailableAfter ?? 0
                    });
                }
            }

            return prerequisites;
        }

        private static List<ObjectiveDto> MapObjectives(Quest quest, Dictionary<string, string> locale)
        {
            var objectives = new List<ObjectiveDto>();

            var conditions = quest.Conditions?.AvailableForFinish;
            if (conditions == null) return objectives;

            foreach (var condition in conditions)
            {
                if (condition == null) continue;

                var conditionId = condition.Id.ToString();

                var targetItems = IsAnyItemCondition(condition.ConditionType)
                    ? TargetIds(condition.Target).Where(id => !string.IsNullOrWhiteSpace(id)).ToList()
                    : new List<string>();

                objectives.Add(new ObjectiveDto
                {
                    Id = conditionId,
                    // Objective descriptions are keyed in the locale table by the condition's own
                    // id. A modded quest may not have one, hence the condition type as a fallback.
                    Text = locale.TryGetValue(conditionId, out var text) && !string.IsNullOrWhiteSpace(text)
                        ? text
                        : condition.ConditionType ?? "",
                    IsNecessary = condition.IsNecessary ?? true,
                    ConditionType = condition.ConditionType ?? "",
                    TargetItems = targetItems,
                    TargetItemNames = targetItems.Select(template => ResolveItemName(template, locale)).ToList(),
                    Count = Numbers.ToCount(condition.Value),
                    FoundInRaid = condition.OnlyFoundInRaid ?? false,
                    ZoneIds = ZoneIdsOf(condition).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                });
            }

            return objectives;
        }

        /// <summary>Condition type stating a weapon build requirement.</summary>
        private const string WeaponAssemblyType = "WeaponAssembly";

        /// <summary>What a Gunsmith-style quest actually asks for, in words - EVERY build it asks
        /// for, not the first.
        ///
        /// Without this such a quest renders as "Handover the custom M4A1  0/1", which tells you
        /// nothing about the twelve numbers it is really checking. 56 quests on this install carry
        /// one of these conditions.
        ///
        /// A quest can carry several. "Gunsmith - Old Friend's Request" wants a T-5000M, a PP-19-01
        /// AND a Glock 17, each with its own thresholds and its own parts - and returning on the
        /// first match showed the rifle and silently dropped the other two, so the panel described a
        /// third of the task while the objectives listed all three.</summary>
        private List<WeaponBuildDto> MapWeaponBuilds(Quest quest, Dictionary<string, string> locale)
        {
            var builds = new List<WeaponBuildDto>();

            var conditions = quest.Conditions?.AvailableForFinish;
            if (conditions == null) return builds;

            foreach (var condition in conditions)
            {
                if (condition == null) continue;
                if (!string.Equals(condition.ConditionType, WeaponAssemblyType, StringComparison.OrdinalIgnoreCase))
                    continue;

                var weapon = TargetIds(condition.Target).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                if (string.IsNullOrWhiteSpace(weapon)) continue;

                var build = new WeaponBuildDto
                {
                    WeaponTemplate = weapon!,
                    WeaponName = ResolveItemName(weapon!, locale),
                    EmptyTacticalSlots = condition.EmptyTacticalSlot?.Value
                };

                // Every threshold the condition states, by its own field name so a modded stat
                // nobody has heard of still reaches the screen.
                AddThreshold(build, "ergonomics", condition.Ergonomics);
                AddThreshold(build, "recoil", condition.Recoil);
                AddThreshold(build, "weight", condition.Weight);
                AddThreshold(build, "magazine capacity", condition.MagazineCapacity);
                AddThreshold(build, "effective distance", condition.EffectiveDistance);
                AddThreshold(build, "durability", condition.Durability);
                AddThreshold(build, "height", condition.Height);
                AddThreshold(build, "width", condition.Width);
                AddThreshold(build, "base accuracy", condition.BaseAccuracy);
                AddThreshold(build, "muzzle velocity", condition.MuzzleVelocity);

                // Ids as well as names. The names are for reading; a solver needs the ids, and the
                // old parse resolved each one straight into a locale string and dropped it.
                foreach (var item in condition.ContainsItems ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(item)) continue;

                    build.RequiredItemIds.Add(item);
                    build.RequiredItemNames.Add(ResolveItemName(item, locale));
                }

                foreach (var category in condition.HasItemFromCategory ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(category)) continue;

                    build.RequiredCategoryIds.Add(category);
                    build.RequiredCategoryNames.Add(ResolveItemName(category, locale));
                }

                // The requirement's identity on the wire, so the per-profile answer can be joined to it. The
                // same hash the history is keyed on, computed from the same inputs.
                if (weapon!.TryParseMongoId(out var keyWeapon))
                {
                    var keyThresholds = build.Thresholds.Select(t => (t.Field, t.Compare, t.Value)).ToList();
                    var keyParts = build.RequiredItemIds
                        .Select(id => id.TryParseMongoId(out var parsed) ? parsed : (MongoId?)null)
                        .Where(id => id != null).Select(id => id!.Value).ToList();
                    var keyCategories = build.RequiredCategoryIds
                        .Select(id => id.TryParseMongoId(out var parsed) ? parsed : (MongoId?)null)
                        .Where(id => id != null).Select(id => id!.Value).ToList();

                    build.Key = WeaponBuildCache.KeyFor(keyWeapon, keyThresholds, keyParts, keyCategories);
                }

                build.ModelCheck = CheckModel(build);
                build.Solution = SolveBuild(build, locale);
                builds.Add(build);

                // Remembered so the slot graphs can be walked once at boot. Collected here rather
                // than re-derived later because this is the only pass that already knows which
                // weapons the installed quests actually name.
                lock (_questBuilds)
                    if (weapon!.TryParseMongoId(out var weaponId)) _questWeapons.Add(weaponId);

                lock (_questBuilds)
                    _questBuilds.Add((ResolveQuestName(quest, quest.Id.ToString(), locale), build));
            }

            return builds;
        }

        /// <summary>Works out a build that satisfies this quest, and puts it on the wire.
        ///
        /// Solved here, while the payload is built, because the answer does not depend on the
        /// profile - the search runs against every part that exists - and sixty of them cost about
        /// 200ms once at boot rather than a wait inside a request the game makes synchronously on
        /// its main thread.
        ///
        /// That stops being true the moment the search is restricted to parts the player owns or can
        /// buy, because then the answer is different for every profile and cannot be cached across
        /// sessions. At that point this moves to a route of its own; until then, caching it with the
        /// quest is both correct and free.</summary>
        private SolvedBuildDto? SolveBuild(WeaponBuildDto build, Dictionary<string, string> locale)
        {
            if (!build.WeaponTemplate.TryParseMongoId(out var weapon)) return null;

            var thresholds = build.Thresholds.Select(t => (t.Field, t.Compare, t.Value)).ToList();

            var mustInclude = new List<MongoId>();
            foreach (var id in build.RequiredItemIds)
                if (id.TryParseMongoId(out var parsed)) mustInclude.Add(parsed);

            var mustIncludeCategories = new List<MongoId>();
            foreach (var id in build.RequiredCategoryIds)
                if (id.TryParseMongoId(out var parsed)) mustIncludeCategories.Add(parsed);

            var result = Solve(weapon, thresholds, mustInclude, mustIncludeCategories);

            if (result.Parts.Count == 0 && !result.Found) return null;

            var solution = new SolvedBuildDto
            {
                Satisfies = result.Found,
                FullyChecked = result.Unchecked.Count == 0,
                HitBudget = result.HitCeiling
            };

            // THE DIFF, computed here from live data rather than read from the history. The history holds
            // what the search chose; what a player has to do about it depends on the item database in front of
            // them, so computing it at payload-build time means it is always current and there is no second
            // fingerprint to keep in step.
            var defaults = weaponPresets.For(weapon);
            var changes = 0;

            solution.HasDefaults = defaults != null;

            for (var index = 0; index < result.Parts.Count; index++)
            {
                var part = result.Parts[index];

                var row = new SolvedPartDto
                {
                    Slot = part.SlotName,
                    Template = part.Template.ToString(),
                    Name = ResolveItemName(part.Template.ToString(), locale)
                };

                if (defaults != null)
                {
                    // The host is what the part is fitted TO - the weapon itself for a part at the top, or
                    // the template of the part above it. A slot name alone is not a place on a gun.
                    var host = part.Parent >= 0 && part.Parent < result.Parts.Count
                        ? result.Parts[part.Parent].Template
                        : weapon;

                    if (!defaults.Occupants.TryGetValue((host, part.SlotName), out var stock))
                    {
                        row.Status = "add";
                        changes++;
                    }
                    else if (stock == part.Template)
                    {
                        row.Status = "fitted";
                    }
                    else
                    {
                        row.Status = "swap";
                        row.Replaces = ResolveItemName(stock.ToString(), locale);
                        changes++;
                    }
                }

                solution.Parts.Add(row);
            }

            solution.Changes = changes;

            // Two independent counts of the same thing - this one walks the DTO rows, the solver's walks its
            // own tree - so a disagreement means one of them is reading the preset differently and the panel
            // would show a diff that does not match the objective the build was chosen by.
            if (defaults != null && changes != result.Changes)
                logger.Warning(
                    $"Quest Tracker: the panel counts {changes} change(s) for '{build.WeaponName}' and the " +
                    $"search counted {result.Changes}. They read the same preset, so one of them is wrong.");

            if (result.Stats is { } stats)
            {
                solution.Scores.Add($"ergonomics {stats.Ergonomics:0.##}");
                solution.Scores.Add($"recoil {stats.Recoil:0.##}");
                solution.Scores.Add($"weight {stats.Weight:0.###} kg");

                if (stats.MagazineCapacity is { } magazine) solution.Scores.Add($"magazine {magazine}");
                if (stats.EffectiveDistance is { } distance) solution.Scores.Add($"distance {distance:0}");
            }

            solution.Unmet.AddRange(result.Unmet);
            solution.Unchecked.AddRange(result.Unchecked);

            return solution;
        }

        /// <summary>Rounds a normal launch runs before the loop exits for good, after which the process
        /// does no further optimisation for the rest of its life.
        ///
        /// A COUNT rather than a time budget because it is stable across machines: twenty seconds bought a
        /// fast box three times the rounds a slow one got, and how much work a launch does should not depend
        /// on hardware.
        ///
        /// Bounded so completely that no politeness machinery is needed. This many rounds on the lowest
        /// thread priority cannot disturb somebody playing, and detecting whether they are would have been
        /// guesswork dressed up as a feature.
        ///
        /// Training - the QUESTTREE_TRAIN environment variable - ignores this and runs until the server
        /// stops, because that is what produces the history that ships. An environment variable and not a
        /// file, so no release can carry the trigger by accident.
        ///
        /// THE NUMBER IS THE OLD TEN ROUNDS, and it is written here because 390 against a remembered
        /// instruction of "ten" reads like drift: a round was one attempt per build that was not already
        /// settled, 39 of the 60 today, so ten rounds is 390 attempts. It is a fixed CPU budget rather than
        /// a derived one - if more builds settle, the same 390 buys more passes over what is left.</summary>
        private const int LaunchAttempts = 390;

        /// <summary>Restarts a training round spends on a build that has never resisted, and the most it
        /// will escalate to for one that has.
        ///
        /// Effort was uniform, which is the wrong shape twice over: a build that has failed forty rounds at
        /// the same size will not fall to a forty-first identical attempt, and a build nobody has looked at
        /// twice does not need sixty restarts. It now grows with resistance.</summary>
        private const int BaseRestarts = 16;
        private const int MaxRestarts = 96;

        /// <summary>One round in this many is a WANDER: instead of asking for a smaller build it asks for a
        /// different one of the same size, and keeps it as the place to search from next time.
        ///
        /// Without this the search is a hill climb from one fixed point. It converged to 583 parts on the
        /// second round and then found nothing for twenty more, because every round was re-asking the same
        /// question from the same place. A same-size build somewhere else in the space is a new basin to
        /// try shrinking from, and it costs a round to get one.
        ///
        /// A wander never reaches the cache. It changes where training LOOKS, not what the mod serves - the
        /// history is still only ever replaced by something strictly smaller, so no player is told to fit
        /// one thing today and another tomorrow.</summary>
        private const int WanderEveryNthAttempt = 4;

        /// <summary>Threads training spreads across. Half the logical processors by default, and
        /// QUESTTREE_TRAIN_THREADS overrides it for anyone who wants the whole machine.
        ///
        /// It was ProcessorCount - 1 for one measurement, and that was wrong in a specific way:
        /// ProcessorCount counts LOGICAL processors, so on an eight-core machine with SMT it asked for
        /// fifteen threads across eight real cores and pinned every one of them at 100%.
        ///
        /// ThreadPriority.Lowest is why that is rude rather than harmful - the game wins every scheduling
        /// contest - but priority schedules CPU time and nothing else. It does not protect L3 cache or
        /// memory bandwidth, and on a chip whose whole appeal is a large L3 that is exactly what a game
        /// wants. A saturated all-core workload also holds boost clocks down and raises package
        /// temperature, which a player feels as frame pacing even while winning every contest.
        ///
        /// So the DEFAULT is the polite setting. This is a mod other people install, and the default is
        /// what almost everybody runs; a person who wants the whole box can ask for it by name.</summary>
        private int Threads => weaponBuildCache.Training ? TrainingThreads : LaunchThreads;

        /// <summary>The training thread count, resolved once and reported in the banner rather than
        /// described - garbage is ignored with a warning instead of throwing, because a typo in an
        /// environment variable is not a reason to refuse to start.</summary>
        private int TrainingThreads
        {
            get
            {
                if (_trainingThreads > 0) return _trainingThreads;

                var half = Math.Max(1, Environment.ProcessorCount / 2);
                var asked = Environment.GetEnvironmentVariable("QUESTTREE_TRAIN_THREADS");

                if (string.IsNullOrWhiteSpace(asked)) return _trainingThreads = half;

                if (!int.TryParse(asked, out var wanted) || wanted < 1)
                {
                    logger.Warning(
                        $"Quest Tracker: QUESTTREE_TRAIN_THREADS is '{asked}', which is not a thread count - " +
                        $"training is using the default {half}.");

                    return _trainingThreads = half;
                }

                // Clamped rather than trusted: more threads than the machine has is slower, not faster.
                var resolved = Math.Min(wanted, Environment.ProcessorCount);

                if (resolved != wanted)
                    logger.Warning(
                        $"Quest Tracker: QUESTTREE_TRAIN_THREADS asked for {wanted} thread(s) and this machine " +
                        $"has {Environment.ProcessorCount} - training is using {resolved}.");

                return _trainingThreads = resolved;
            }
        }

        private int _trainingThreads;

        /// <summary>How often training says what it is doing. TIME rather than a count of attempts,
        /// because the rate is now the thing being reported and a count-based cadence would speed up or slow
        /// down with it - which is exactly when a progress line is least readable.</summary>
        private static readonly TimeSpan TrainingReportEvery = TimeSpan.FromSeconds(20);

        /// <summary>How long the search may run. Seconds on a normal start, minutes while training. This is
        /// CPU on the machine hosting the game, and a solver improving a build by one part does not get to
        /// cost somebody a raid.</summary>
        /// <summary>Threads a normal launch uses. Two, because a normal launch is brief and the machine
        /// belongs to whoever is playing. Training takes half the cores, which is a session the user chose
        /// to spend.
        ///
        /// It said "because LaunchRounds rounds is brief" for three releases after LaunchRounds was deleted
        /// - rounds stopped existing when the barrier did, and the budget is LaunchAttempts now.</summary>
        private static int LaunchThreads => Math.Max(1, Math.Min(2, Environment.ProcessorCount));

        /// <summary>Pause between attempts on a NORMAL launch, so the search yields the machine rather
        /// than pinning two cores while somebody is playing.
        ///
        /// Training does not pause. It used to, between rounds, and the cost was not the pause: the whole
        /// structure was a barrier. A round ended when the SLOWEST of sixty builds ended, so fourteen
        /// threads waited on one straggler searching 376,000 nodes, three times a round, and then everyone
        /// slept. Doubling the threads from eight to fifteen moved the rate by nothing at all, which is what
        /// says the limit was never total work. A training launch is one the user chose to spend the machine
        /// on; it now spends it.</summary>
        private static readonly TimeSpan BetweenLaunchAttempts = TimeSpan.FromMilliseconds(50);

        /// <summary>Keeps looking for smaller builds after the server is up.
        ///
        /// The boot path needs ONE good answer per quest and the cache gives it that immediately, so making
        /// the answer BETTER is not urgent and does not belong on the boot path. It happens here instead:
        /// a round of fresh starting points, each one only able to replace a build with a smaller one that
        /// verifies, written down for every start after this one.
        ///
        /// This runs on every start, shipped or not, and that is the point. The cache that ships is a
        /// starting position rather than a final answer - a player's install has parts this machine never
        /// had, and one round per start means their builds get smaller too, without anybody waiting for it.
        ///
        /// It cannot make an answer worse: the incumbent only ever loses to something strictly smaller that
        /// passes the verifier. So the risk is not correctness, it is CPU on a machine somebody is playing
        /// on, which is what the budget, the pause between rounds, and the cancellation token are for.
        ///
        /// Every improvement rebuilds the payload, so a client asking after one lands gets the better build
        /// rather than the one this start began with.</summary>
        private void Improve(CancellationToken cancellationToken)
        {
            if (_questBuilds.Count == 0) return;

            _ = Task.Run(() => Work(cancellationToken), cancellationToken);
        }

        /// <summary>The improvement loop: workers taking build after build, with no round between them.
        ///
        /// It used to be a Parallel.ForEach over all sixty requirements, repeated. That made every round a
        /// BARRIER: the round ended when the slowest build ended, so with 39 unsettled builds over fifteen
        /// threads the threads finished their three waves and then waited on one straggler spending its full
        /// two-second ceiling on 376,000 nodes. Raising the threads from eight to fifteen changed the rate by
        /// zero - 30 attempts a minute either way - which is the measurement that says the limit was the
        /// longest single task and not the amount of work.
        ///
        /// So there is no round. Each worker takes the next requirement, searches it, writes anything
        /// smaller down, and takes another. Nothing waits for anything.
        ///
        /// THE UNIT CHANGED WITH IT, and that matters for comparing against older numbers: an ATTEMPT is one
        /// search of one requirement. A round of the old loop was one attempt for each build that was not
        /// already settled - 39 of them here - so 200 old rounds is about 7,800 attempts. Every figure logged
        /// below is in attempts.
        ///
        /// What has not changed is what may be written: strictly smaller, and only after the verifier agrees.
        /// A loop that can only replace a build with a smaller verified one cannot make an answer worse,
        /// however many threads it runs on.</summary>
        private void Work(CancellationToken token)
        {
            var training = weaponBuildCache.Training;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            List<(string Quest, WeaponBuildDto Build)> requirements;
            lock (_questBuilds) requirements = _questBuilds.ToList();

            if (requirements.Count == 0) return;

            if (!training)
                logger.Info(
                    $"Quest Tracker: looking for smaller weapon builds in the background - {LaunchAttempts} " +
                    $"attempt(s) across {Threads} thread(s) on the lowest thread priority, then it stops for " +
                    "good. Anything it finds is used from the next start. Set QUESTTREE_TRAIN=1 to search " +
                    "until the server stops.");
            else
                logger.Warning(
                    $"Quest Tracker: TRAINING. This launch will keep looking for smaller weapon builds FOR AS " +
                    $"LONG AS IT RUNS, across {Threads} of this machine's {Environment.ProcessorCount} " +
                    "thread(s) on the lowest priority, and will not stop on its own. Stop the server when you " +
                    "have had enough - every improvement is written down as it is found, so nothing is lost by " +
                    "stopping. Unset QUESTTREE_TRAIN for a normal launch.");

            var budget = training ? int.MaxValue : LaunchAttempts;
            var cursor = -1;

            void Worker()
            {
                // Below everything else on the machine. Brief and preemptible beats brief and competing with
                // a game for a core.
                try { Thread.CurrentThread.Priority = ThreadPriority.Lowest; }
                catch (Exception) { /* a platform that will not lower it is no reason to skip the work */ }

                while (!token.IsCancellationRequested && _attempts < budget)
                {
                    // Round-robin rather than a shuffled queue, so no requirement is starved and two workers
                    // never take the same one. The wrap is on an unsigned cast because the cursor is allowed
                    // to run past int.MaxValue on a long training session.
                    var index = (int)((uint)Interlocked.Increment(ref cursor) % (uint)requirements.Count);
                    var requirement = requirements[index];

                    // A fresh seed per attempt, from the file, so the next session starts where this one
                    // stopped exploring rather than repeating it.
                    var seed = weaponBuildCache.Advance();
                    var wander = seed % WanderEveryNthAttempt == 0;

                    if (Shrink(requirement.Build, wander, seed))
                    {
                        Interlocked.Increment(ref _improved);
                        weaponBuildCache.Flush();

                        logger.Info(
                            $"Quest Tracker: a smaller build for '{requirement.Quest}' - {_improved} found so " +
                            "far. It will be used from the next start.");
                    }

                    if (!training) Thread.Sleep(BetweenLaunchAttempts);
                }
            }

            void Report(string what)
            {
                var minutes = Math.Max(clock.Elapsed.TotalMinutes, 1e-6);

                logger.Info(
                    $"Quest Tracker: {what} - {_attempts:N0} attempt(s) in {clock.Elapsed.TotalMinutes:0.0} " +
                    $"minute(s) across {Threads} thread(s) ({_attempts / minutes:N0}/min), {_improved} smaller " +
                    $"build(s) found. {_proven.Count} of {Requirements} are provably minimal ON PART COUNT, " +
                    $"0 proven minimal on COST - the objective - because no bound over cost exists yet. " +
                    $"{_boundsChecked:N0} bound comparison(s) across {_bounds.Count} requirement(s), " +
                    $"{_boundsDisagreed} disagreement(s)." +
                    (Falsifying
                        ? $" {_falsifyTries:N0} falsification attempt(s) against proven-minimal builds, " +
                          $"{_falsified} counterexample(s)."
                        : "") +
                    (weaponBuildCache.Training ? " Stop the server to finish." : ""));
            }

            try
            {
                var workers = new Task[Math.Max(1, Threads)];

                for (var worker = 0; worker < workers.Length; worker++)
                    workers[worker] = Task.Run(Worker, token);

                // The progress line comes from HERE rather than from a worker, so its cadence is the clock's
                // and not one build's search time.
                while (!Task.WaitAll(workers, TrainingReportEvery))
                {
                    if (!training) continue;

                    Report("training");

                    // The generation is what stops the next session re-exploring this one's ground, and it
                    // only reaches the file when the file is written. Fruitless exploration is exactly the
                    // exploration worth remembering.
                    weaponBuildCache.Flush();
                }

                weaponBuildCache.Flush();
                Report(training ? "training finished" : "launch search done");
            }
            catch (OperationCanceledException)
            {
                // The server is shutting down, which for a training run is how it is MEANT to end. With
                // nothing reaching the client mid-session, the file is the only place progress lives, and
                // losing the last attempts to a quit would be the one way this is worse than rebuilding live.
                weaponBuildCache.Flush();
                Report("training stopped");
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
            {
                weaponBuildCache.Flush();
                Report("training stopped");
            }
            catch (Exception ex)
            {
                // Never the reason a server falls over: this is an optimisation nobody is waiting on.
                logger.Warning(
                    $"Quest Tracker: the background build search stopped early ({ex.Message}). The builds " +
                    "already found are unaffected.");
            }
        }

        private int Requirements
        {
            get { lock (_questBuilds) return _questBuilds.Count; }
        }

        /// <summary>Whether one build beats another on the objective: fewer purchases, or the same number
        /// of purchases and fewer parts.
        ///
        /// The single place the write rule is expressed, so the improvement loop and the boot solve cannot
        /// disagree about what an improvement is - which they did for a while, in the days when one of them
        /// required strictly smaller and the other did not.</summary>
        private static bool Cheaper(long cost, int parts, long wasCost, int wasParts) =>
            cost < wasCost || (cost == wasCost && parts < wasParts);

        /// <summary>The proven lower bound for one requirement, worked out once and kept. NULL when no
        /// bound could be argued, which is not a bound of any size - see Floor.Unbounded.</summary>
        private int? Proven(
            MongoId weapon,
            List<(string Field, string Compare, double Value)> thresholds,
            List<MongoId> mustInclude,
            List<MongoId> mustIncludeCategories)
        {
            var key = WeaponBuildCache.KeyFor(weapon, thresholds, mustInclude, mustIncludeCategories);

            // Asked BEFORE the work, because the work is the expensive part and for most requirements its
            // answer is already known. Proven is re-entered on every attempt for any key that never becomes
            // proven, and withholding the bound whenever a quest names a mount left 37 of 60 requirements in
            // exactly that state - so nearly two thirds of all attempts were running a full LowestPossible,
            // including its breadth-first walk of the weapon's whole slot graph, and discarding the result.
            //
            // Sound on the same premise everything else here rests on: a bound is a property of the item
            // data, so a requirement that could not be bounded once this boot cannot be bounded later in it.
            // What it gives up is noticing a key that went unbounded and then bounded again, which would be
            // a contradiction rather than news - and the first unbounded answer still goes through the full
            // path below, where Crosscheck compares it against any bound recorded earlier.
            bool known;
            lock (_proven) known = _unbounded.Contains(key);

            if (known) return null;

            var floor = weaponBuildVerifier.LowestPossible(weapon, thresholds, mustInclude, mustIncludeCategories);

            // No number is written when there is no claim to write. Nothing stored is withdrawn either:
            // a bound already on file cannot have outlived its evidence, because Load() zeroes every Bound
            // when the item fingerprint changes and KeyFor hashes the thresholds into the key - so the two
            // ways a bound could go stale both invalidate it already. Wiping it here would only throw away
            // the 16-to-39 sessions of stability the file actually holds.
            //
            // But a key that WAS bounded and now is not is a disagreement, and it is exactly the kind
            // Crosscheck exists for: BestBelow memoises on template alone while its value depends on which
            // descent hit a cycle cut first, so two threads can legitimately reach different answers for
            // one key. Routing this case silently around the check would hide the one failure the check
            // was built to catch.
            if (floor.Unbounded)
            {
                if (_bounds.TryGetValue(key, out var earlier))
                {
                    Interlocked.Increment(ref _boundsDisagreed);

                    logger.Warning(
                        $"Quest Tracker: the proven minimum for '{weapon}' came back as UNPROVABLE after coming back " +
                        $"as {earlier} earlier in this boot - {floor.Reason}. A bound is a property of the item data " +
                        "and cannot depend on who asked.");
                }
                else if (AddUnbounded(key))
                {
                    // Once per key per boot. Proven is re-entered on every Shrink for a key that never
                    // becomes proven, so logging unconditionally would put hundreds of identical lines in a
                    // training run's log.
                    logger.Warning(
                        $"Quest Tracker: no lower bound could be proven for '{weapon}' - {floor.Reason}. This build " +
                        "is not called minimal, and the falsifier is turned loose on it below.");
                }

                return null;
            }

            Crosscheck(key, floor.Parts, weapon);

            // Written down rather than recomputed from nothing next time, and with its own history: a bound
            // that has not moved in fifty sessions is a different object from one that improved last
            // session.
            weaponBuildCache.Bound(key, floor.Parts);

            return floor.Parts;
        }

        /// <summary>Records a requirement as unprovable, answering whether this boot had not already.
        /// Under _proven's lock, because the two sets are read together and a torn answer would either
        /// duplicate a Warning or drop a falsification.</summary>
        private bool AddUnbounded(string key)
        {
            lock (_proven) return _unbounded.Add(key);
        }

        /// <summary>Asserts that the proven lower bound for one requirement does not change within a boot.
        ///
        /// A bound is a property of the item data, so the same question must give the same answer however
        /// many threads asked it and in whatever order they filled the shared tables. That is exactly the
        /// property concurrency breaks, and breaking it does not crash or log: it silently proves a build
        /// minimal that is not. So it is checked rather than argued, on every call, for the life of the
        /// process - the cost is one dictionary lookup against a proof the whole feature rests on.
        ///
        /// A count of how many were checked is reported beside the count of disagreements, because zero
        /// disagreements from a check nobody ran looks identical to zero from a check that passed.</summary>
        private void Crosscheck(string key, int bound, MongoId weapon)
        {
            var first = _bounds.GetOrAdd(key, bound);

            // Comparisons, not distinct requirements. "60 cross-checked" could mean each was computed once
            // and compared against nothing, which is the same blank-reads-as-zero trap the gate was hardened
            // against; this says how many times an answer was held against an earlier one.
            Interlocked.Increment(ref _boundsChecked);

            if (first == bound) return;

            Interlocked.Increment(ref _boundsDisagreed);

            logger.Warning(
                $"Quest Tracker: the proven minimum for '{weapon}' came back as {bound} after coming back as " +
                $"{first} earlier in this boot. A bound is a property of the item data and cannot depend on who " +
                "asked - the proof tables are not order-independent, and no build should be called minimal " +
                "until that is fixed.");
        }

        /// <summary>Counts the builds that name a part the player cannot get.
        ///
        /// THE MEASUREMENT THAT COMES BEFORE THE OPTIMISATION. The search chooses from every part that
        /// exists, so it can recommend something a profile cannot obtain at any price, and until now nothing
        /// looked. A build that cannot be assembled is worse than an expensive one, so the honest first step
        /// is to find out how often it happens rather than to start pricing things.
        ///
        /// Every profile on the install, because this is used with FIKA: a host serves a group, and the
        /// answer differs sharply between a fresh profile and a finished one. Read-only, allocated per
        /// profile, and it runs once at boot after the survey - it touches no shared state and nothing waits
        /// on it.
        ///
        /// The loyalty view is the interesting half. A part this profile cannot buy might be gated behind
        /// trader progress or not sold at all, and those are different problems: one is "your traders are too
        /// low", the other is "nobody sells this". The same data answers what a player with every trader at
        /// level one would be missing, which is the case that matters most - they are the ones doing Gunsmith
        /// early and the most likely to be handed advice they cannot act on.</summary>
        private void AuditAvailability()
        {
            List<(string Quest, WeaponBuildDto Build)> requirements;
            lock (_questBuilds) requirements = _questBuilds.ToList();

            if (requirements.Count == 0) return;

            Dictionary<MongoId, SPTarkov.Server.Core.Models.Eft.Profile.SptProfile> profiles;

            try
            {
                profiles = saveServer.GetProfiles();
            }
            catch (Exception ex)
            {
                logger.Info($"Quest Tracker: no profiles to check part availability against ({ex.Message}).");
                return;
            }

            if (profiles.Count == 0)
            {
                logger.Info("Quest Tracker: no profiles on this install, so part availability cannot be checked.");
                return;
            }

            foreach (var (id, _) in profiles)
            {
                SPTarkov.Server.Core.Models.Eft.Common.PmcData? pmc = null;

                try
                {
                    pmc = profileHelper.GetPmcProfile(id);
                }
                catch (Exception)
                {
                    continue;
                }

                if (pmc is null) continue;

                var sources = partAvailability.For(id, pmc);

                if (sources == null) continue;

                var unbuildable = 0;      // builds naming at least one part this profile cannot get
                var missing = new HashSet<MongoId>();
                var gatedOnly = new HashSet<MongoId>();   // sold, but behind trader progress
                var unsold = new HashSet<MongoId>();      // no trader offers it at any level
                var earlyBlocked = 0;     // builds a profile with every trader at level one could not build
                var affected = new List<string>();
                var owned = 0;            // parts held loose - genuinely free
                var fittedStored = 0;     // parts held, but fitted to a stored weapon
                var fittedEquipped = 0;   // parts held, but fitted to an equipped weapon
                var inPlace = 0;          // parts already on a copy of the quest's own weapon
                var priced = 0L;          // what the rest would cost at trader prices
                var withFlea = 0;         // builds still blocked once the flea market is counted as a source
                var fleaOnly = new HashSet<MongoId>();    // parts no trader sells that the flea lists

                foreach (var (_, build) in requirements)
                {
                    if (!build.WeaponTemplate.TryParseMongoId(out var weapon)) continue;

                    var thresholds = build.Thresholds.Select(t => (t.Field, t.Compare, t.Value)).ToList();

                    var mustInclude = new List<MongoId>();
                    foreach (var named in build.RequiredItemIds)
                        if (named.TryParseMongoId(out var parsed)) mustInclude.Add(parsed);

                    var mustIncludeCategories = new List<MongoId>();
                    foreach (var category in build.RequiredCategoryIds)
                        if (category.TryParseMongoId(out var parsed)) mustIncludeCategories.Add(parsed);

                    var key = WeaponBuildCache.KeyFor(weapon, thresholds, mustInclude, mustIncludeCategories);
                    var remembered = weaponBuildCache.Get(key);

                    if (remembered == null) continue;

                    var blocked = false;
                    var blockedWithFlea = false;
                    var earlyBlockedHere = false;
                    var defaults = weaponPresets.For(weapon);

                    foreach (var part in remembered.Parts)
                    {
                        if (!part.Template.TryParseMongoId(out var template)) continue;

                        // The trader-only view, kept as the headline so the figure stays comparable with the
                        // ones taken before the flea market was a tier. The flea view is counted beside it.
                        var (tier, price) = sources.Classify(template, defaults, weapon);

                        // Held but not loose: counted apart, because the ledger's "179 already in the stash"
                        // could not tell a spare from a part bolted to the gun they raid with.
                        if (tier is not (PartAvailability.Tier.Fitted or PartAvailability.Tier.InPlace or PartAvailability.Tier.Owned)
                            && sources.Holdings.TryGetValue(template, out var holding) && holding.FittedAnywhere)
                        {
                            if (holding.FittedToEquipped.Count > 0) fittedEquipped++;
                            else fittedStored++;
                        }

                        switch (tier)
                        {
                            case PartAvailability.Tier.Fitted:
                                continue;

                            case PartAvailability.Tier.InPlace:
                                inPlace++;
                                continue;

                            case PartAvailability.Tier.Owned:
                                owned++;
                                continue;

                            case PartAvailability.Tier.Buyable:
                                priced += price ?? 0;
                                if (sources.Gated.TryGetValue(template, out var gate) && gate.Level > 1) earlyBlockedHere = true;
                                continue;

                            case PartAvailability.Tier.Barter:
                                if (sources.Gated.TryGetValue(template, out var barterGate) && barterGate.Level > 1) earlyBlockedHere = true;
                                continue;

                        }

                        // Not obtainable from a trader. Whether the flea would supply it is counted as a
                        // HYPOTHETICAL for every profile, access or not: the question being answered is
                        // whether the advice has any acquisition route at all.
                        blocked = true;
                        missing.Add(template);
                        earlyBlockedHere = true;

                        if (sources.Gated.ContainsKey(template)) gatedOnly.Add(template);
                        else unsold.Add(template);

                        if (sources.Flea.ContainsKey(template)) fleaOnly.Add(template);
                        else blockedWithFlea = true;
                    }

                    if (blocked)
                    {
                        unbuildable++;
                        affected.Add(build.WeaponName);
                    }
                    if (blockedWithFlea) withFlea++;
                    if (earlyBlockedHere) earlyBlocked++;
                }

                logger.Info(
                    $"Quest Tracker: part availability for profile {id} (level {pmc.Info?.Level}, " +
                    $"{sources.Traders} trader(s) read) - {unbuildable} of {requirements.Count} build(s) name a " +
                    $"part this profile cannot get, {missing.Count} distinct part(s): {gatedOnly.Count} sold but " +
                    $"locked behind trader progress, {unsold.Count} not sold by any trader at any level. " +
                    $"{owned} part instance(s) are held loose (free), {inPlace} already on a copy of the quest's " +
                    $"weapon, {fittedStored} held but fitted to a stored weapon and {fittedEquipped} fitted to an " +
                    $"equipped one (both priced as purchases), and the rest would cost {priced:N0} " +
                    $"roubles at this profile's trader prices. With every trader at loyalty 1, " +
                    $"{earlyBlocked} of {requirements.Count} build(s) would be out of reach.");

                // The flea question, answered in process against the merged database. Of the parts no trader
                // sells this profile: listable with a price, refused by the game's flea rules, or with no
                // route at all - and of those, which ship with the game and which a mod injected.
                var fleaBanned = missing.Count(template => sources.FleaBanned.Contains(template));
                var noRoute = missing.Where(template => !sources.Flea.ContainsKey(template)).ToList();
                var noRouteModded = noRoute.Count(template => !sources.IsVanilla(template));

                logger.Info(
                    $"Quest Tracker: the flea market for profile {id} - " +
                    $"{(sources.FleaAccess ? "OPEN" : $"CLOSED until level {sources.FleaLevel}")} at level {pmc.Info?.Level}. " +
                    $"Of the {missing.Count} part(s) no trader sells this profile, {fleaOnly.Count} are flea-listable " +
                    $"with a price, {fleaBanned} are refused by the game's flea rules, and {noRoute.Count} have no " +
                    $"route at all ({noRouteModded} mod-injected, {noRoute.Count - noRouteModded} vanilla). If the flea " +
                    $"counted as a source, {unbuildable - withFlea} of the {unbuildable} blocked build(s) would become " +
                    $"buildable and {withFlea} would stay blocked regardless.");

                if (missing.Count > 0)
                    logger.Info(
                        "Quest Tracker: template ids no trader offers this profile - " +
                        string.Join(", ", missing.Take(12).Select(template => template.ToString())) +
                        (missing.Count > 12 ? $" and {missing.Count - 12} more" : "") +
                        ". Affected: " + string.Join("; ", affected.Take(8)) + ".");
            }

            // And then the answer for each profile is prepared, off the boot path: the shared build where
            // every part is obtainable, a build searched within reach where it is not.
            var handoff = new List<ProfileBuilds.Requirement>(requirements.Count);

            foreach (var (questName, build) in requirements)
            {
                if (!build.WeaponTemplate.TryParseMongoId(out var weapon) || string.IsNullOrEmpty(build.Key)) continue;

                IReadOnlyList<WeaponSolver.FittedPart>? baseline = null;

                // What THIS boot serves, which in training mode can differ from the file.
                lock (_solved)
                    if (_solved.TryGetValue(build.Key, out var served) && served.Found) baseline = served.Parts;

                if (baseline == null)
                {
                    var remembered = weaponBuildCache.Get(build.Key);
                    if (remembered != null) baseline = Restore(remembered);
                }

                handoff.Add(new ProfileBuilds.Requirement
                {
                    Quest = questName,
                    Key = build.Key,
                    Build = build,
                    Baseline = baseline
                });
            }

            profileBuilds.Refresh(handoff);
        }

        /// <summary>Whether the VERIFIER agrees this build satisfies the requirement. Nothing reaches the
        /// history without passing through here.
        ///
        /// The history is the one artifact of this that outlives the process and gets published, so a build
        /// the search believes in and the verifier rejects must never enter it. One did: training found a
        /// five-wide MP-133 for a four-wide limit, believed it, wrote it down because it was one part smaller
        /// than the valid build it replaced, and every later boot rejected it and paid a re-solve. The
        /// training path was the hole - it skipped the audit entirely, so the only code that would have
        /// caught this ran after the write.
        ///
        /// A disagreement is a WARNING, not a shrug. The two of them agreeing is the only reason to believe
        /// either, and when they do not, the verifier is right by construction: it reads the item data
        /// independently and has no stake in the answer.</summary>
        private bool Sound(
            MongoId weapon,
            IReadOnlyList<WeaponSolver.FittedPart> parts,
            List<(string Field, string Compare, double Value)> thresholds,
            List<MongoId> mustInclude,
            List<MongoId> mustIncludeCategories,
            string what)
        {
            var audited = weaponBuildVerifier.Verify(weapon, parts, thresholds, mustInclude, mustIncludeCategories);

            if (audited.Verified) return true;

            logger.Warning(
                $"Quest Tracker: the solver and the verifier DISAGREE about {what} for '{weapon}' - the solver " +
                $"says it satisfies the requirement, the verifier says [{string.Join("; ", audited.Failures.Take(3))}]. " +
                "It is NOT remembered. The verifier is right.");

            return false;
        }

        /// <summary>One sweep over every requirement from a fresh set of starting points, and how many
        /// builds it managed to shrink.
        ///
        /// Spread across half the machine's cores. The sixty requirements are independent problems and this
        /// was solving them one at a time - 330 rounds in 34 minutes on sixteen cores, using one of them.
        /// Half rather than all, because the rest belongs to whoever is playing.</summary>


        /// <summary>Looks for a smaller build for one requirement, and returns whether it found one.
        ///
        /// Called from several threads at once, so everything it touches is either allocated per call or
        /// guarded: the solver keeps its whole state in a SearchState it allocates itself, the cache and the
        /// verifier's knapsack tables have their own locks, and the two collections this class owns are
        /// locked here.</summary>
        private bool Shrink(WeaponBuildDto build, bool wander, int seed)
        {
            if (!build.WeaponTemplate.TryParseMongoId(out var weapon)) return false;

            var thresholds = build.Thresholds.Select(t => (t.Field, t.Compare, t.Value)).ToList();

            var mustInclude = new List<MongoId>();
            foreach (var id in build.RequiredItemIds)
                if (id.TryParseMongoId(out var parsed)) mustInclude.Add(parsed);

            var mustIncludeCategories = new List<MongoId>();
            foreach (var id in build.RequiredCategoryIds)
                if (id.TryParseMongoId(out var parsed)) mustIncludeCategories.Add(parsed);

            var key = WeaponBuildCache.KeyFor(weapon, thresholds, mustInclude, mustIncludeCategories);
            var remembered = weaponBuildCache.Get(key);

            if (remembered == null) return false;

            // Provably minimal ON PART COUNT already - which is no longer a reason to stop.
            var proven = false;
            lock (_proven) proven = _proven.Contains(key);

            // "is at most the bound" AND "there is a bound". The second half is new and is the whole
            // point: Proven used to hand back int.MaxValue when it could prove nothing, and every build
            // compares as at most that - so the requirements nothing could bound were exactly the ones
            // marked minimal, and the falsifier below was switched off for them.
            if (!proven &&
                Proven(weapon, thresholds, mustInclude, mustIncludeCategories) is { } bound &&
                remembered.Parts.Count <= bound)
            {
                lock (_proven) _proven.Add(key);
                proven = true;
            }

            // AND THEN IT KEEPS SEARCHING, which is the change the new objective forces. A build at its
            // part-count bound cannot get smaller; it can still get cheaper to assemble, and cheaper is what
            // is being minimised. The early return that used to live here would have frozen 21 of the 60 at
            // whatever they happened to cost.
            //
            // Effort follows IGNORANCE where it does spend: a bound already attacked this hard has all the
            // evidence another search would add, and one never attacked has none.
            //
            // "proven OR unprovable", and the second half is the correction to a fix that got this exactly
            // backwards. Falsify searches adversarially for a SMALLER build, so the requirements with no
            // bound at all are the ones it is most worth pointing at - there is no proof standing between
            // them and a smaller answer. Gating on `proven` alone meant that making the absence of a bound
            // honest also made it unfalsified, which is the opposite of what this comment says the policy
            // is. It happened to be masked before, because an unprovable bound arrived as int.MaxValue,
            // compared as satisfied, and so entered this branch by accident.
            bool unprovable;
            lock (_proven) unprovable = _unbounded.Contains(key);

            if ((proven || unprovable) && Falsifying && remembered.Falsifications < FalsifyEnough)
                Falsify(build, weapon, thresholds, mustInclude, mustIncludeCategories, remembered, seed);

            // Counted HERE and not in the loop, so an attempt means a search that happened. A settled
            // requirement costs a dictionary lookup and is not an attempt at anything.
            Interlocked.Increment(ref _attempts);

            List<WeaponSolver.FittedPart>? working;
            lock (_working) working = _working.GetValueOrDefault(key);

            working ??= Restore(remembered);
            if (working == null) return false;

            // Effort follows resistance. A build that has failed forty rounds gets a wider search than one
            // nobody has looked at twice, because a forty-first identical attempt is not a search.
            var restarts = Math.Min(MaxRestarts, BaseRestarts + remembered.Attempts);

            // The hint the last measurement of this build left behind: the thresholds it meets with
            // nothing to spare. The climb keeps buying slack on those after they are met, which is the only
            // currency a removal can be paid for in.
            var binding = remembered.Binding;

            var result = wander
                ? weaponSolver.Solve(
                    weapon, thresholds, mustInclude, mustIncludeCategories,
                    allowed: null, knownGood: null, seed: seed, restarts: restarts, ceiling: working.Count,
                    binding: binding, pricing: Handbook)
                : weaponSolver.Solve(
                    weapon, thresholds, mustInclude, mustIncludeCategories,
                    allowed: null, knownGood: working, seed: seed, restarts: restarts,
                    binding: binding, pricing: Handbook);

            weaponBuildCache.Cost(key, result.NodesOpened);

            if (!result.Found)
            {
                weaponBuildCache.Held(key);
                return false;
            }

            // THE INCUMBENT'S COST IS MEASURED, NEVER READ. A stored zero once read as a perfect score and
            // blocked every improvement for a whole training run (ledger, defect 7); describing the remembered
            // build is a handful of dictionary lookups and cannot be stale.
            var incumbent = Restore(remembered);
            var standing = incumbent == null
                ? null
                : weaponSolver.Describe(weapon, thresholds, mustInclude, mustIncludeCategories, incumbent, Handbook);

            if (standing == null)
            {
                weaponBuildCache.Held(key);
                return false;
            }

            weaponBuildCache.Changed(key, standing.Changes, standing.Cost, Handbook.PerPurchase);

            // Somewhere new that is no cheaper and no leaner: worth searching from, not worth serving.
            //
            // LEXICOGRAPHIC ON (cost, parts), and the loosening from "strictly fewer parts" is deliberate
            // rather than a weakening. Both components can only ever improve, so the guarantee that matters -
            // a build never gets more expensive and never grows - holds exactly as before. What it admits is
            // the build that costs less and happens to carry one part more, which under the old rule could
            // never be written down at all.
            if (!Cheaper(result.Cost, result.Parts.Count, standing.Cost, remembered.Parts.Count))
            {
                if (result.Parts.Count <= working.Count)
                    lock (_working) _working[key] = result.Parts;

                weaponBuildCache.Held(key);
                return false;
            }

            // Smaller AND legal, in that order. Smaller alone is what put an unassemblable build in the file
            // for two hundred rounds of training to keep and every later boot to reject.
            if (!Sound(weapon, result.Parts, thresholds, mustInclude, mustIncludeCategories, "a smaller build"))
            {
                weaponBuildCache.Held(key);
                return false;
            }

            weaponBuildCache.Put(key, result.Parts, result.Floor, result.Binding, result.Changes, result.Cost,
                Handbook.PerPurchase);

            lock (_working) _working[key] = result.Parts;

            // So the payload rebuild and the next round both see the better one.
            lock (_solved) _solved[key] = result;

            return true;
        }

        /// <summary>Tries to find a build with fewer PARTS than one the part-count bound called minimal, and
        /// says so loudly if it succeeds.
        ///
        /// Still about part count after the objective moved to changes, deliberately: the bound it attacks is
        /// a bound on part count, the 5,022 failed attacks already recorded are evidence about part count,
        /// and relabelling either to match the new objective would turn true evidence into a claim nobody
        /// tested. A falsifier for the changes objective arrives with the changes bound it would attack.
        ///
        /// The only check here that tests the proof against reality instead of against another part of this
        /// code. The bound is argued from the item data; this goes looking for a counterexample with the
        /// widest search the solver has - every restart it will take, a part ceiling one BELOW the build that
        /// is supposed to be minimal - and anything it finds has to pass the verifier before it counts, so a
        /// counterexample cannot be a solver bug dressed up as a proof failure.
        ///
        /// A find is not a curiosity. It means a build was called provably minimal when a smaller legal one
        /// exists, which is the worst failure this code can have: it is wrong in the direction that reads as
        /// success, and it would have been reported as an achievement. So it is logged as an error naming the
        /// weapon, both sizes and every part of the smaller build, and the counterexample is written to the
        /// history - because a smaller build IS the better answer, whatever it says about the proof.</summary>
        private void Falsify(
            WeaponBuildDto build,
            MongoId weapon,
            List<(string Field, string Compare, double Value)> thresholds,
            List<MongoId> mustInclude,
            List<MongoId> mustIncludeCategories,
            WeaponBuildCache.CachedBuild remembered,
            int seed)
        {
            Interlocked.Increment(ref _falsifyTries);

            var smaller = weaponSolver.Solve(
                weapon, thresholds, mustInclude, mustIncludeCategories,
                allowed: null, knownGood: null, seed: seed, restarts: MaxRestarts,
                ceiling: remembered.Parts.Count - 1,
                binding: remembered.Binding, pricing: Handbook);

            var key = WeaponBuildCache.KeyFor(weapon, thresholds, mustInclude, mustIncludeCategories);

            if (!smaller.Found || smaller.Parts.Count >= remembered.Parts.Count)
            {
                // A FAILED falsification is the expensive half of this and it used to vanish. Recorded, so
                // the evidence accumulates across sessions instead of being re-bought every run.
                weaponBuildCache.Falsified(key, smaller.NodesOpened, seed);
                return;
            }

            // The verifier decides, exactly as everywhere else. A "counterexample" the verifier rejects is a
            // search bug and says nothing about the bound.
            if (!Sound(weapon, smaller.Parts, thresholds, mustInclude, mustIncludeCategories,
                    "a build smaller than one it called provably minimal"))
                return;

            Interlocked.Increment(ref _falsified);

            logger.Error(
                $"Quest Tracker: THE MINIMALITY PROOF IS WRONG for '{build.WeaponName}'. It was called provably " +
                $"minimal at {remembered.Parts.Count} parts and a verified build exists at " +
                $"{smaller.Parts.Count}: " +
                string.Join(" ", smaller.Parts.Select(part => $"{part.SlotName}={part.Template}")) +
                ". The bound understates what is reachable, so no build should be reported as minimal until " +
                "that is found and fixed.");

            weaponBuildCache.Put(key, smaller.Parts, smaller.Floor, smaller.Binding, smaller.Changes, smaller.Cost,
                Handbook.PerPurchase);

            weaponBuildCache.Flush();
        }

        /// <summary>The build for one requirement, solved once per boot and remembered across boots.
        ///
        /// The cache is not a shortcut past the search, it is the INCUMBENT. Every boot hands the build it
        /// remembers back to the solver and asks for a smaller one, so the answer improves over time and
        /// can never get worse: a boot that finds nothing better has spent one round of restarts proving
        /// the size is hard to beat, and a boot that finds something better writes it down.
        ///
        /// That is why this is safe in a way a plain cache would not be. A plain cache freezes whatever
        /// the search managed the first time, including whatever it managed badly.</summary>
        private WeaponSolver.Result Solve(
            MongoId weapon,
            List<(string Field, string Compare, double Value)> thresholds,
            List<MongoId> mustInclude,
            List<MongoId> mustIncludeCategories)
        {
            var key = WeaponBuildCache.KeyFor(weapon, thresholds, mustInclude, mustIncludeCategories);

            lock (_solved)
                if (_solved.TryGetValue(key, out var already)) return already;

            var remembered = weaponBuildCache.Get(key);
            var incumbent = remembered == null ? null : Restore(remembered);
            var rejected = false;

            // A remembered build is DESCRIBED rather than re-derived. Searching again would cost seconds to
            // arrive back where it started, and the verifier is what decides whether the remembered answer
            // is good - not another search.
            //
            // Training is the exception and the reason the search still exists here: that mode is trying to
            // beat the build, so it has to run.
            // The audit of a remembered build happens on EVERY path, training included. Training used to skip
            // it and search from whatever it found, which is how an invalid entry survived two hundred rounds
            // that were all looking at it: nothing in that mode ever asked whether the build it was trying to
            // beat was legal in the first place.
            if (incumbent != null
                && !Sound(weapon, incumbent, thresholds, mustInclude, mustIncludeCategories, "a remembered build"))
            {
                incumbent = null;
                rejected = true;
            }

            // THE INCUMBENT IS DESCRIBED ON EVERY PATH, training included, and its cost is what a candidate has
            // to beat. Reading the stored figure instead is defect 7 in the ledger: every entry carried a zero
            // that nothing in training ever wrote, and zero was unbeatable.
            WeaponSolver.Result? standing = null;

            if (incumbent != null)
            {
                standing = weaponSolver.Describe(
                    weapon, thresholds, mustInclude, mustIncludeCategories, incumbent, Handbook);

                // BOTH have to agree before a remembered build is served, and they still do: the verifier has
                // just passed these parts above - seated legally, nothing claimed twice, every threshold met -
                // and this adds the solver's own reading of the same build, which is what catches one that
                // describes differently from how it was searched.
                //
                // This is the one failure mode a shipped history introduces, so it is the one the cold path is
                // not allowed to take on trust. A rejected entry costs a search. It never reaches a panel.
                if (standing.Found)
                {
                    // Every boot describes every remembered build, so this is where the hint comes from for
                    // the builds that never change - which is most of them, and precisely the ones a directed
                    // search is for. And what it costs, so the file says what the objective is for it.
                    weaponBuildCache.Note(key, standing.Binding);
                    weaponBuildCache.Changed(key, standing.Changes, standing.Cost, Handbook.PerPurchase);

                    if (!weaponBuildCache.Training)
                    {
                        lock (_solved) _solved[key] = standing;
                        return standing;
                    }
                }
                else
                {
                    logger.Info(
                        $"Quest Tracker: a remembered weapon build for '{weapon}' does not hold up on this install, " +
                        "so it is being solved again - " +
                        string.Join("; ", standing.Unmet.Distinct().Take(3)) + ".");

                    incumbent = null;
                    standing = null;
                    rejected = true;
                }
            }

            var result = weaponSolver.Solve(
                weapon, thresholds, mustInclude, mustIncludeCategories,
                allowed: null, knownGood: incumbent, seed: _seed,
                binding: remembered?.Binding, pricing: Handbook);

            // A remembered entry that FAILED verification counts as absent, and that word "rejected" is
            // load-bearing. Without it an invalid entry that happens to be small blocks its own replacement
            // forever: the MP-133 was 5 wide against a limit of 4, was rejected and re-solved on EVERY boot,
            // and the valid build was never written because it was not SMALLER than the broken one. One
            // wasted re-solve per launch, for the life of the install.
            if (result.Found
                && (remembered == null || rejected || standing == null
                    || Cheaper(result.Cost, result.Parts.Count, standing.Cost, remembered.Parts.Count))
                && Sound(weapon, result.Parts, thresholds, mustInclude, mustIncludeCategories, "a freshly solved build"))
            {
                weaponBuildCache.Put(key, result.Parts, result.Floor, result.Binding, result.Changes, result.Cost,
                    Handbook.PerPurchase);

                // Interlocked, like the other writer of this counter. Rebuild() reaches Solve() from the
                // zone-harvest POST thread while the training workers are running, so a plain ++ could lose
                // an increment - only in the boot log's "N improvements found" line, but that line is the
                // evidence the training did anything.
                Interlocked.Increment(ref _improved);
            }
            else if (remembered != null)
            {
                weaponBuildCache.Held(key);
                weaponBuildCache.Note(key, result.Binding);
            }

            lock (_solved) _solved[key] = result;

            return result;
        }

        /// <summary>A remembered build in the shape the solver takes. Anything unparseable is dropped and
        /// the solver is handed nothing, which costs a search rather than a wrong build.</summary>
        private static List<WeaponSolver.FittedPart>? Restore(WeaponBuildCache.CachedBuild remembered)
        {
            var parts = new List<WeaponSolver.FittedPart>(remembered.Parts.Count);

            foreach (var part in remembered.Parts)
            {
                if (!part.Template.TryParseMongoId(out var template)) return null;

                parts.Add(new WeaponSolver.FittedPart
                {
                    SlotName = part.Slot,
                    Template = template,
                    Depth = part.Depth,
                    Parent = part.Parent
                });
            }

            return parts.Count > 0 ? parts : null;
        }

        /// <summary>Scores the quest's own named parts on the quest's own weapon.
        ///
        /// This is the gate WeaponStatModel was written for and never received. The model shipped a
        /// release before any solver precisely so it could be proven against the running game first
        /// - and then nothing called it, so the proof never happened and the solver stayed unwritten
        /// for two releases.
        ///
        /// Partial by nature, and that is worth being plain about: a condition's ContainsItems names
        /// a few leaf parts, not a whole gun. What it can catch is a wrong COMBINING RULE - summing
        /// where the game multiplies, summing where it selects - and that is the failure that would
        /// otherwise reach a hand-in.</summary>
        private WeaponModelCheckDto? CheckModel(WeaponBuildDto build)
        {
            if (build.RequiredItemIds.Count == 0) return null;

            // TryParseMongoId, not the constructor: a modded condition can name something that is
            // not an id at all, and MongoId's constructor does not politely decline.
            var parts = new List<MongoId>();
            foreach (var id in build.RequiredItemIds)
                if (id.TryParseMongoId(out var parsed)) parts.Add(parsed);

            if (!build.WeaponTemplate.TryParseMongoId(out var weapon)) return null;

            var stats = weaponStatModel.Score(weapon, parts);
            if (stats == null) return null;

            var unfilled = weaponGraph.UnfilledRequiredSlots(weapon, parts, out var requiredSlots);

            return new WeaponModelCheckDto
            {
                UnfilledRequiredSlots = unfilled,
                RequiredSlots = requiredSlots,
                Ergonomics = stats.Ergonomics,
                Recoil = stats.Recoil,
                Weight = stats.Weight,
                MagazineCapacity = stats.MagazineCapacity,
                EffectiveDistance = stats.EffectiveDistance,
                PartsNamed = build.RequiredItemIds.Count,
                PartsScored = parts.Count,
                Clamped = stats.Clamped.ToList()
            };
        }

        /// <summary>Adds a threshold, unless it is the unconstrained default.
        ///
        /// Skipped on the VALUE being zero, never on the field name. In the first vanilla condition
        /// effectiveDistance, weight, baseAccuracy and muzzleVelocity are all "&gt;= 0" and pure noise
        /// on screen - but height and width are "&lt;= 1" and "&lt;= 4" and entirely real, in 5 quests
        /// each. Writing those two off by name, which an earlier reading of one example suggested,
        /// would have dropped a genuine constraint.</summary>
        private static void AddThreshold(WeaponBuildDto build, string field, ValueCompare? compare)
        {
            var value = compare?.Value ?? 0d;

            if (value == 0d)
            {
                // Recorded rather than silently dropped. Once the row is gone, "unconstrained noise"
                // and "genuinely constrained to zero" look identical, and a solver has to tell them
                // apart - the field is only named here when the condition named it at all.
                if (compare != null) build.ZeroThresholdFields.Add(field);
                return;
            }

            build.Thresholds.Add(new WeaponBuildThresholdDto
            {
                Field = field,
                Compare = compare!.CompareMethod ?? ">=",
                Value = value
            });
        }

        /// <summary>Reward types that name their trader in Target rather than TraderId.</summary>
        private static readonly HashSet<string> TraderInTarget = new(StringComparer.OrdinalIgnoreCase)
        {
            "TraderStanding", "TraderUnlock", "TraderStandingRestore"
        };

        /// <summary>Reward types whose TraderId is NOT a trader, so nothing may read it as one.
        ///
        /// SPT's own model documents Reward.TraderId as "Hideout area id", and for ProductionScheme that
        /// is exactly what it holds: all 31 vanilla ProductionScheme rewards carry 10, 2, 7 or 11 - area
        /// numbers, not MongoIds. AssortmentUnlock is the type the field really does mean a trader for,
        /// and its 231 rewards carry real trader ids, which is why one comment covered both and was only
        /// half right.
        ///
        /// The cost of getting it wrong was silent and entirely in the client: QuestScore.ReachableLoyalty
        /// matches the value against the profile's traders, never found one, and scored every
        /// hideout-craft unlock 0.08 where it should have been 0.25. The "Do next" order was wrong for
        /// those quests with nothing logged anywhere.</summary>
        private static readonly HashSet<string> TraderIdIsNotATrader = new(StringComparer.OrdinalIgnoreCase)
        {
            "ProductionScheme"
        };

        private List<RewardDto> MapRewards(Quest quest, Dictionary<string, string> locale)
        {
            var rewards = new List<RewardDto>();

            if (quest.Rewards == null) return rewards;
            if (!quest.Rewards.TryGetValue(SuccessRewardKey, out var successRewards) || successRewards == null)
                return rewards;

            foreach (var reward in successRewards)
            {
                if (reward == null) continue;
                if (reward.IsHidden == true) continue;

                var template = reward.Items?.FirstOrDefault()?.Template.ToString() ?? "";
                var type = reward.Type?.ToString() ?? "";
                var value = reward.Value ?? 0d;
                var currency = Currencies.All.Contains(template);

                rewards.Add(new RewardDto
                {
                    Type = type,
                    Value = value,
                    Name = ResolveRewardName(reward, locale),
                    ShortName = ResolveShortName(template, locale),
                    Template = template,
                    // Trader names are deliberately left to the client, which resolves them from
                    // the live session and so gets modded traders right for free.
                    TraderId = ResolveRewardTrader(reward, type),
                    IsCurrency = currency,
                    RoubleValue = RewardWorth(template, value, currency),
                    LoyaltyLevel = reward.LoyaltyLevel ?? 0,
                    FoundInRaid = reward.FindInRaid ?? false
                });
            }

            return rewards;
        }

        /// <summary>Which field holds this reward's trader, which depends on the type.
        ///
        /// Standing and trader unlocks put it in Target; an assort unlock puts it in TraderId. Reading
        /// one field for both was the first bug here.
        ///
        /// And some rewards have no trader at all, which is the second. ProductionScheme's TraderId is a
        /// hideout AREA id - SPT's own model says so - so passing it on left the client matching "10"
        /// against a trader list forever. Empty is the honest answer, and the client already treats an
        /// empty trader as "this reward does not come from one".</summary>
        private static string ResolveRewardTrader(Reward reward, string type)
        {
            if (TraderInTarget.Contains(type)) return reward.Target ?? "";
            if (TraderIdIsNotATrader.Contains(type)) return "";

            return reward.TraderId?.ToString() ?? "";
        }

        /// <summary>What a reward is worth in roubles, or null when it cannot be priced.
        ///
        /// Cash is worth its face value. An item is worth its handbook price times the quantity -
        /// PartPrices returns null rather than zero for an item it has no price for, and that null is
        /// carried through deliberately so the client can tell "worth nothing" from "worth unknown".
        /// Everything that is not an item - experience, standing, a skill - has no rouble value at all
        /// and is scored on its own terms.</summary>
        private long? RewardWorth(string template, double value, bool currency)
        {
            if (currency) return (long)Math.Max(0d, value);
            if (string.IsNullOrWhiteSpace(template)) return null;
            if (!template.TryParseMongoId(out var parsed)) return null;

            var unit = partPrices.Of(parsed);
            if (unit == null) return null;

            var count = (long)Math.Max(1d, value);

            return unit.Value * count;
        }

        /// <summary>Item rewards are the only ones whose display name the client cannot work out for
        /// itself, so the item name is resolved here. Everything else is a number, a trader the
        /// client already knows, or carries its own target id.</summary>
        private static string ResolveRewardName(Reward reward, Dictionary<string, string> locale)
        {
            var template = reward.Items?.FirstOrDefault()?.Template.ToString();

            if (!string.IsNullOrWhiteSpace(template) &&
                locale.TryGetValue($"{template} Name", out var itemName) &&
                !string.IsNullOrWhiteSpace(itemName))
            {
                return itemName;
            }

            return reward.Target ?? "";
        }

        /// <summary>The item's short name, which is a separate locale key from its name. Empty
        /// rather than falling back, so the client can tell "there is no short name" from "the short
        /// name happens to equal the long one" and choose per context.</summary>
        private static string ResolveShortName(string template, Dictionary<string, string> locale)
        {
            if (string.IsNullOrWhiteSpace(template)) return "";

            return locale.TryGetValue($"{template} ShortName", out var shortName) &&
                   !string.IsNullOrWhiteSpace(shortName)
                ? shortName
                : "";
        }

        /// <summary>Condition types that TAKE an item from you - handed to a trader, or found in
        /// raid and turned in. These and only these are what the Collector hand-in checklist is
        /// built from, so the set must stay narrow: an item you plant and leave behind is not an
        /// item Collector will accept.</summary>
        internal static bool IsItemCondition(string? conditionType) =>
            string.Equals(conditionType, "HandoverItem", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(conditionType, "FindItem", StringComparison.OrdinalIgnoreCase);

        /// <summary>Condition types that have you CARRY an item into a raid and leave it somewhere -
        /// a marker on a trading post, a beacon in a warehouse. The template id is in the same
        /// Target field, but the item never reaches a trader, which is why these are kept apart
        /// from IsItemCondition above.</summary>
        internal static bool IsCarriedItemCondition(string? conditionType) =>
            string.Equals(conditionType, "LeaveItemAtLocation", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(conditionType, "PlaceBeacon", StringComparison.OrdinalIgnoreCase);

        /// <summary>Every condition whose Target is item template ids rather than quest ids or zone
        /// names. This is the set an objective's TargetItems is filled from, and the set the client
        /// asks "what do I need to bring" of - which is a wider question than "what does a trader
        /// want", and getting it wrong is what left markers and beacons off that list.</summary>
        internal static bool IsAnyItemCondition(string? conditionType) =>
            IsItemCondition(conditionType) || IsCarriedItemCondition(conditionType);

        /// <summary>An item's display name from the locale table, falling back to the template id.
        /// Was a private copy in three builders.</summary>
        internal static string ResolveItemName(string template, Dictionary<string, string> locale) =>
            locale.TryGetValue($"{template} Name", out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : template;

        internal static IEnumerable<string> TargetIds(ListOrT<string>? target)
        {
            if (target == null) yield break;

            if (target.IsList)
            {
                foreach (var value in target.List ?? Enumerable.Empty<string>())
                    yield return value;

                yield break;
            }

            if (target.Item != null) yield return target.Item;
        }

        /// <summary>
        /// Every zone id a condition can point at. The shape follows DynamicMaps' own condition
        /// walk (QuestUtils.GetPositionsForCondition), which is the one known to line up with what
        /// the scene contains: the condition's own zoneId (LeaveItemAtLocation, PlaceBeacon), and
        /// inside a CounterCreator each sub-condition's VisitPlace target or zoneIds list (InZone,
        /// and the zoned Kills/Shots/LaunchFlare counters).
        /// </summary>
        internal static IEnumerable<string> ZoneIdsOf(QuestCondition condition)
        {
            if (!string.IsNullOrWhiteSpace(condition.ZoneId)) yield return condition.ZoneId!;

            var counters = condition.Counter?.Conditions;
            if (counters == null) yield break;

            foreach (var sub in counters)
            {
                if (sub == null) continue;

                if (string.Equals(sub.ConditionType, "VisitPlace", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var id in TargetIds(sub.Target))
                        if (!string.IsNullOrWhiteSpace(id)) yield return id;
                }

                // The JSON field is "zoneIds"; SPT's model names the property Zones.
                if (sub.Zones == null) continue;

                foreach (var id in sub.Zones)
                    if (!string.IsNullOrWhiteSpace(id)) yield return id;
            }
        }
    }
}












