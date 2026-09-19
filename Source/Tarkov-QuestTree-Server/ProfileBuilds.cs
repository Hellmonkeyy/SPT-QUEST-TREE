using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.Locales;
using SPTarkov.Server.Core.Utils;

namespace QuestTreeServer
{
    /// <summary>
    /// The sixty builds as THIS player can assemble them: what they already have, what they can buy and
    /// for how much, and - where the shared build names something they cannot get - a build that avoids
    /// it, or the plain statement that no build within their reach exists and why.
    ///
    /// FILTER AND REPAIR, not a solve per player. The shared baseline is solved once, trained for hours and
    /// shipped; it is the best build known over every part that exists. For one profile the only question
    /// is whether every part in it is obtainable, which is a dictionary lookup per part. Only a build that
    /// fails that question is solved again, with the search confined to what the profile can get. So the
    /// cost scales with how restricted the player is - a finished profile pays sixty lookups, a fresh one
    /// pays a search per blocked build - and not with how many players there are.
    ///
    /// THAT MATTERS BECAUSE OF FIKA. Several profiles share one server and the host is playing while
    /// serving their friends. So there is ONE worker for every profile, on the lowest priority, and it
    /// exists only while the queue is non-empty. Six players are six entries in a queue, never six times
    /// the cores. The thread budget is global by construction rather than by policy.
    ///
    /// EVERY REPAIRED BUILD GOES THROUGH THE VERIFIER, exactly as the shared baseline does. A restricted
    /// search is the same search with fewer candidates, and the same search has produced an unassemblable
    /// build before. A repair the verifier rejects is reported as a disagreement and the build as blocked,
    /// never served.
    ///
    /// A BLOCKED BUILD SAYS WHY. "Your trader levels are too low" and "this quest is hard" are different
    /// problems with different remedies, and the search can tell them apart by asking again with the parts
    /// trader progress would unlock: if that solves, the answer names the trader and the level; if it does
    /// not, no trader at any level sells what the quest needs. Either way the thresholds the closest
    /// attempt missed are reported with the margin, so the player sees a number rather than a shrug.
    ///
    /// WHEN AN ANSWER GOES STALE. Trader progress changes what is buyable, so the answer is keyed on the
    /// profile's trader fingerprint and recomputed when it moves - a quality change, since the old build
    /// is still legal. Selling a part the build counted on is a correctness change, so on every request
    /// each served build is re-checked part by part against the stash and the traders as they are now,
    /// and a build that no longer holds up is queued again and served as stale until it is.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class ProfileBuilds(
        ISptLogger<ProfileBuilds> logger,
        PartAvailability availability,
        WeaponSolver solver,
        WeaponBuildVerifier verifier,
        WeaponPresets presets,
        ProfileHelper profileHelper,
        SaveServer saveServer,
        LocaleService localeService,
        PartPrices partPrices,
        WeaponPresetWriter presetWriter,
        JsonUtil jsonUtil)
    {
        /// <summary>One build requirement as the payload builder states it, with the shared build it
        /// currently serves for it.</summary>
        public sealed class Requirement
        {
            public string Quest { get; init; } = "";
            public string Key { get; init; } = "";
            public WeaponBuildDto Build { get; init; } = new();
            public IReadOnlyList<WeaponSolver.FittedPart>? Baseline { get; init; }
        }

        private sealed class Answer
        {
            public string Fingerprint = "";
            public List<ProfileBuildDto> Builds = new();
            public int Level;
            public bool FleaAccess;
            public DateTime At;
        }

        private readonly object _lock = new();
        private List<Requirement> _requirements = new();
        private readonly ConcurrentDictionary<MongoId, Answer> _answers = new();

        private readonly Queue<(MongoId Profile, bool Fresh)> _queue = new();
        private readonly HashSet<(MongoId, bool)> _queued = new();
        private bool _working;

        /// <summary>How many repairs the verifier has passed and rejected, over the life of the process.
        /// Reported together, because "0 rejected" from a check that never ran reads as a pass.</summary>
        private int _verified;
        private int _rejected;

        /// <summary>The requirements and their shared builds, as of this boot. Every known profile is queued
        /// for a pass, off the boot path.</summary>
        public void Refresh(IReadOnlyList<Requirement> requirements)
        {
            lock (_lock) _requirements = requirements.ToList();

            Dictionary<MongoId, SPTarkov.Server.Core.Models.Eft.Profile.SptProfile> profiles;

            try
            {
                profiles = saveServer.GetProfiles();
            }
            catch (Exception ex)
            {
                logger.Info($"Quest Tracker: no profiles to prepare builds for ({ex.Message}).");
                return;
            }

            foreach (var id in profiles.Keys) Enqueue(id);

            // And the hypothetical that no profile on this install is: a fresh one. Queued last, behind
            // every real player, and derived from the first real profile's trader read.
            var first = profiles.Keys.FirstOrDefault();

            if (!string.IsNullOrEmpty(first.ToString())) Enqueue(first, fresh: true);
        }

        public string GetPayloadJson(MongoId sessionId) =>
            JsonSerializer.Serialize(Build(sessionId), WireJson.Options);

        /// <summary>This profile's answer for one requirement, tree included, or null when it has not
        /// been worked out yet. The preset writer's way in.</summary>
        public ProfileBuildDto? Find(MongoId sessionId, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (!_answers.TryGetValue(sessionId, out var answer) || answer == null) return null;

            return answer.Builds.FirstOrDefault(b => b != null && b.Key == key);
        }

        private ProfileBuildsDto Build(MongoId sessionId)
        {
            var payload = new ProfileBuildsDto();

            var pmc = TryGetProfile(sessionId);

            if (pmc == null) return payload;

            payload.HasProfile = true;
            payload.Level = pmc.Info?.Level ?? 0;

            var sources = availability.For(sessionId, pmc);

            if (sources == null) return payload;

            payload.FleaAccess = sources.FleaAccess;
            payload.FleaLevel = sources.FleaLevel == int.MaxValue ? 0 : sources.FleaLevel;

            _answers.TryGetValue(sessionId, out var answer);

            // Stale for a QUALITY reason (trader progress moved) or a CORRECTNESS reason (a part the answer
            // counted on is no longer obtainable). Both queue a recompute; only the second is a build the
            // player should not act on, and the flag says which.
            var moved = answer == null || answer.Fingerprint != sources.Fingerprint;
            var broken = answer != null && !StillObtainable(answer, sources);

            if (moved || broken) Enqueue(sessionId);

            if (answer == null)
            {
                payload.Ready = false;
                return payload;
            }

            payload.Ready = !moved && !broken;
            payload.Stale = moved || broken;
            payload.Builds = answer.Builds;

            // Filled once per build object, on the first request that serves it: the DTOs live in
            // the answer and are replaced, not mutated, when a compute lands. The writer's own
            // flattening, so what the gate check sees is what "Save as preset" would write.
            foreach (var dto in payload.Builds)
            {
                if (dto == null || dto.Tree == null || dto.Tree.Count == 0) continue;
                // Only builds the client will ask the gate about; a blocked build's items would be
                // a third of the payload for a line nothing draws.
                if (dto.Status != "ok" && dto.Status != "repaired") continue;
                if (!dto.WeaponTemplate.TryParseMongoId(out var weapon)) continue;

                // One filler at a time per DTO: two requests for the same session can arrive
                // together, and each ItemsFor call makes fresh ids, so two unsynchronised fills
                // could publish one call's items with the other's root. Root is written first,
                // and readers only trust ItemsJson once it is non-empty.
                lock (dto)
                {
                    if (dto.ItemsJson.Length > 0) continue;

                    var items = presetWriter.ItemsFor(weapon, dto.Tree, out _);
                    if (items == null || items.Count == 0) continue;

                    dto.Root = items[0].Id.ToString();
                    dto.ItemsJson = jsonUtil.Serialize(items) ?? "";
                }
            }

            return payload;
        }

        /// <summary>Whether every part an answer relies on is still obtainable as things stand now.
        ///
        /// The three tiers skipped are ones Sources.Has cannot speak for, and skipping only "fitted" made
        /// this answer false on ordinary profiles. Has covers Owned, Buyable, Barter and Flea; it does NOT
        /// cover a part already on a weapon you own (InPlace), which the repair path deliberately allows -
        /// it adds OwnedWeapons[weapon] to the permitted set - nor a part the QUEST names that no trader
        /// stocks yet (Absent with Named), which JudgeQuietly deliberately does not treat as blocking.
        ///
        /// So a level-12 profile doing an early Gunsmith - a non-default handguard already on its M4A1, a
        /// suppressor Peacekeeper only sells at loyalty 3 - had every request answer "broken". Ready was
        /// always false, Stale always true, and Enqueue fired on every single request, so the panel never
        /// left the stale state while a background thread re-solved all sixty requirements in a loop. The
        /// player this feature is most for was the one it worked worst for.
        ///
        /// Unnamed Absent is still treated as broken, deliberately. It should not occur - the restricted
        /// search only draws from tiers Classify recognises - and if it ever does, saying so is the point.
        ///
        /// InPlace is RE-CHECKED rather than skipped, and the difference matters more than it looks. This is
        /// the only thing in the request path that reads the inventory at all: the availability fingerprint
        /// is built from trader assorts and flea access only - Hold() runs before the stamp exists - so
        /// `moved` cannot notice a gun leaving the stash. Skipping the row outright would mean a player who
        /// took that M4A1 into Labs and did not come back with it kept being told the build was Ready, with
        /// a part they no longer own priced at zero, until some unrelated trader level-up happened. Losing a
        /// gun in a raid is an ordinary Tuesday.</summary>
        private bool StillObtainable(Answer answer, PartAvailability.Sources sources)
        {
            foreach (var build in answer.Builds)
            {
                if (build.Status is not ("ok" or "repaired")) continue;

                var onOwnedWeapon =
                    build.WeaponTemplate.TryParseMongoId(out var weapon) &&
                    sources.OwnedWeapons.TryGetValue(weapon, out var fitted)
                        ? fitted
                        : null;

                foreach (var part in build.Parts)
                {
                    // On the weapon's default preset: nothing to go and get, ever.
                    if (part.Tier == "fitted") continue;

                    // Named by the quest and stocked by nobody. The build is allowed to contain it and is
                    // not wrong for doing so, so its absence is not a change in circumstances.
                    if (part.Tier == "absent" && part.Named) continue;

                    if (!part.Template.TryParseMongoId(out var template)) return false;

                    // Still on a weapon this profile owns is still in place. Falling through to Has for an
                    // InPlace part is what made this answer false on ordinary profiles: Has covers Owned,
                    // Buyable, Barter and Flea, and a part bolted to a gun you already have is in none of
                    // them.
                    if (part.Tier == "inplace" && onOwnedWeapon != null && onOwnedWeapon.Contains(template))
                        continue;

                    if (!sources.Has(template)) return false;
                }
            }

            return true;
        }

        private void Enqueue(MongoId profile, bool fresh = false)
        {
            lock (_lock)
            {
                if (!_queued.Add((profile, fresh))) return;

                _queue.Enqueue((profile, fresh));

                if (_working) return;

                _working = true;
                _ = Task.Run(Work);
            }
        }

        /// <summary>The one worker. Drains the queue and exits; the next enqueue starts another.</summary>
        private void Work()
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.Lowest; }
            catch (Exception) { /* not a reason to skip the work */ }

            while (true)
            {
                (MongoId Profile, bool Fresh) item;

                lock (_lock)
                {
                    if (_queue.Count == 0)
                    {
                        _working = false;
                        return;
                    }

                    item = _queue.Dequeue();

                    // Dropped from the in-flight set HERE, before the work rather than after it. Removing
                    // it in the finally below meant a request arriving DURING a compute was refused by
                    // Enqueue's Add and then forgotten, so the answer stayed one generation stale until
                    // something else happened to ask. Releasing the key first can cost one redundant
                    // compute back to back, which is the right side of that trade.
                    _queued.Remove(item);
                }

                try
                {
                    Compute(item.Profile, item.Fresh);
                }
                catch (Exception ex)
                {
                    logger.Warning($"Quest Tracker: could not prepare builds for profile {item.Profile} ({ex.Message}).");
                }
            }
        }

        private void Compute(MongoId profileId, bool fresh)
        {
            List<Requirement> requirements;
            lock (_lock) requirements = _requirements;

            if (requirements.Count == 0) return;

            var pmc = TryGetProfile(profileId);
            if (pmc == null) return;

            var sources = availability.For(profileId, pmc);
            if (sources == null) return;

            if (fresh) sources = availability.Fresh(sources);

            var who = fresh
                ? "a FRESH profile (hypothetical: every trader at loyalty 1, empty stash, no flea market)"
                : $"profile {profileId} (level {pmc.Info?.Level}, flea {(sources.FleaAccess ? "open" : "closed")})";

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var locale = localeService.GetLocaleDb();
            var answer = new Answer
            {
                Fingerprint = sources.Fingerprint,
                Level = pmc.Info?.Level ?? 0,
                FleaAccess = sources.FleaAccess,
                At = DateTime.UtcNow
            };

            var ok = 0;
            var repaired = 0;
            var traderLevel = 0;
            var unsold = 0;
            var needsFlea = 0;
            var unsolved = 0;
            var verifiedHere = 0;
            var rejectedHere = 0;
            var nodes = 0L;
            var cash = 0L;
            var barters = 0;

            // THE COST OF RESTRICTION, over the builds that were repaired: how many more parts and how many
            // more roubles the obtainable build costs than the shared one it replaced. The shared build's
            // absent parts have no price this profile can be quoted, so its priced cost is over the parts
            // that could be priced and the count of those that could not is reported beside it - the
            // comparison understates the shared build's cost, never the repaired one's.
            var sharedParts = 0;
            var repairedParts = 0;
            var sharedCash = 0L;
            var repairedCash = 0L;
            var sharedUnpriced = 0;

            // THE OBJECTIVE AGAINST THE BILL, in three columns over one set of rows.
            //
            //   handbook  - what the shared search USED to minimise. Kept as the control, because it is the
            //               only way to see whether replacing it helped.
            //   objective - what it minimises now: cheapest trader cash price, else handbook x the flea
            //               multiple. See PartPrices.Shared.
            //   paid      - what this profile is actually charged, Cash + FleaEstimate.
            //
            // This measurement is what motivated the change: handbook and paid drifted far enough apart on
            // the flea-only parts that the search was optimising a number nobody is charged, and the
            // objective column is the fix being measured rather than asserted. Only builds served AS SHARED
            // are measured: a repaired build was searched under this profile's own trader and flea prices,
            // so its objective already was the bill and it would only dilute the comparison. Measured like
            // for like: all three figures cover the same rows, the buyable and flea parts of each shared
            // build - a part the handbook does not list is taken off ALL THREE and counted. Owned, in-place
            // and fitted parts are zero everywhere, and barter and absent parts are on none of them (barters
            // are counted). The PerPurchase surcharge is left out of the two objective sides deliberately:
            // this compares prices with prices.
            var shared = 0;
            var nothingToBuy = 0;
            var handbookTotal = 0L;
            var objectiveTotal = 0L;
            var paidTotal = 0L;
            var sharedBarters = 0;
            var handbookUnpriced = 0;
            var disagree = 0;
            var disagreeObjective = 0;
            var spread = new List<(string Quest, long Handbook, long Objective, long Paid, double Ratio)>();

            foreach (var requirement in requirements)
            {
                var judged = Judge(requirement, sources, locale, fresh ? "fresh" : profileId.ToString(),
                    ref verifiedHere, ref rejectedHere);

                answer.Builds.Add(judged);
                nodes += judged.Nodes;
                cash += judged.Cash;
                barters += judged.Barters;

                if (judged.Status == "ok")
                {
                    var (handbook, objective, unpriced, unpricedPaid) = Handbook(judged);
                    // The rows the handbook does not list come off this side too, so both sides cover the
                    // same parts; otherwise a build whose only purchases are unlisted reads as a disagreement.
                    var paid = judged.Cash + judged.FleaEstimate - unpricedPaid;

                    handbookUnpriced += unpriced;

                    if (handbook == 0 && paid == 0)
                    {
                        nothingToBuy++;
                    }
                    else
                    {
                        var ratio = Disagreement(handbook, paid);

                        shared++;
                        handbookTotal += handbook;
                        objectiveTotal += objective;
                        paidTotal += paid;
                        sharedBarters += judged.Barters;
                        if (ratio > 1.25d) disagree++;
                        if (Disagreement(objective, paid) > 1.25d) disagreeObjective++;
                        spread.Add((judged.QuestName, handbook, objective, paid, ratio));
                    }
                }

                switch (judged.Status)
                {
                    case "ok": ok++; break;
                    case "repaired":
                        repaired++;
                        repairedParts += judged.Parts.Count;
                        repairedCash += judged.Cash + judged.FleaEstimate;

                        if (requirement.Baseline != null
                            && requirement.Build.WeaponTemplate.TryParseMongoId(out var repairedWeapon))
                        {
                            var sharedDefaults = presets.For(repairedWeapon);

                            sharedParts += requirement.Baseline.Count;

                            foreach (var part in requirement.Baseline)
                            {
                                var (tier, price) = sources.Classify(part.Template, sharedDefaults);

                                if (tier is PartAvailability.Tier.Buyable or PartAvailability.Tier.Flea) sharedCash += price ?? 0;
                                else if (tier is PartAvailability.Tier.Absent or PartAvailability.Tier.Barter) sharedUnpriced++;
                            }
                        }

                        break;
                    case "unsolved": unsolved++; break;
                    default:
                        if (judged.Why.StartsWith("trader", StringComparison.Ordinal)) traderLevel++;
                        else if (judged.Why.StartsWith("flea", StringComparison.Ordinal)) needsFlea++;
                        else unsold++;
                        break;
                }
            }

            if (!fresh) _answers[profileId] = answer;

            Interlocked.Add(ref _verified, verifiedHere);
            Interlocked.Add(ref _rejected, rejectedHere);

            logger.Info(
                $"Quest Tracker: builds for {who} - {ok} of {requirements.Count} shared build(s) usable as " +
                $"they are, {repaired} repaired within what this profile can get, {traderLevel} blocked by trader " +
                $"level, {needsFlea} blocked until the flea market, {unsold} blocked because no trader sells what " +
                $"they need, {unsolved} with no shared build to start from. {verifiedHere} repaired build(s) " +
                $"passed the verifier and {rejectedHere} were rejected by it. {nodes:N0} node(s) in " +
                $"{clock.Elapsed.TotalSeconds:0.0} s on one thread. Buying everything not owned or fitted would " +
                $"cost {cash:N0} roubles plus {barters} barter(s).");

            if (repaired > 0)
                logger.Info(
                    $"Quest Tracker: the cost of restriction for {who} - over the {repaired} repaired build(s), " +
                    $"{(double)sharedParts / repaired:0.##} parts and {(double)sharedCash / repaired:N0} roubles per shared " +
                    $"build (priced parts only; {sharedUnpriced} absent or barter part(s) carry no price) against " +
                    $"{(double)repairedParts / repaired:0.##} parts and {(double)repairedCash / repaired:N0} roubles " +
                    $"(trader prices plus flea estimates) per repaired build.");

            if (shared + nothingToBuy > 0)
            {
                var widest = spread
                    .OrderByDescending(entry => entry.Ratio)
                    .Take(3)
                    .Select(entry =>
                        $"'{entry.Quest}': handbook {entry.Handbook:N0} / objective {entry.Objective:N0} vs paid {entry.Paid:N0}");

                logger.Info(
                    $"Quest Tracker: the objective against the bill for {who} - over the {shared} build(s) served as " +
                    $"shared with something to buy ({nothingToBuy} with nothing to buy, repaired builds excluded), the " +
                    $"handbook values the parts to buy at {handbookTotal:N0} roubles, the objective the search actually " +
                    $"used prices the same rows at {objectiveTotal:N0}, and it would pay {paidTotal:N0} (trader prices " +
                    $"plus flea estimates), with {sharedBarters} barter(s) on none of the three and " +
                    $"{handbookUnpriced} part(s) the handbook does not list taken off all three. {disagree} of {shared} " +
                    $"build(s) disagree handbook-to-paid by more than 25% either way and {disagreeObjective} " +
                    $"objective-to-paid" +
                    (spread.Count > 0 ? $". Widest by handbook: {string.Join("; ", widest)}." : "."));
            }
        }

        /// <summary>Two valuations of the same rows Total prices - buyable and flea - with how many of them
        /// the handbook does not list and what Total charged for those, so the caller can take them off the
        /// paid side too. Keep the tiers in step with Total, or the three sides of the
        /// objective-against-the-bill line stop covering the same parts.
        ///
        /// HANDBOOK IS THE CONTROL and stays. It is the number the shared builds used to be solved against,
        /// so keeping it beside the new one is what makes the change measurable rather than asserted: if the
        /// objective column does not sit closer to paid than the handbook column does, the change did not do
        /// what it was for.
        ///
        /// OBJECTIVE is PartPrices.Shared over the rows the handbook lists - the same rows, deliberately,
        /// even though Shared can price a row the handbook cannot. Three totals over three different row
        /// sets would not be comparable, which is the whole point of the line. A row the handbook lists
        /// always has a Shared value, so the fallback below never fires; it is there because a silent zero
        /// would flatter the new column.</summary>
        private (long Handbook, long Objective, int Unpriced, long UnpricedPaid) Handbook(ProfileBuildDto dto)
        {
            var handbook = 0L;
            var objective = 0L;
            var unpriced = 0;
            var unpricedPaid = 0L;

            foreach (var part in dto.Parts)
            {
                if (part.Tier is not ("buyable" or "flea")) continue;

                if (part.Template.TryParseMongoId(out var template) && partPrices.Of(template) is { } price)
                {
                    handbook += price;
                    objective += partPrices.Shared(template) ?? price;
                    continue;
                }

                unpriced++;
                unpricedPaid += part.Price ?? 0;
            }

            return (handbook, objective, unpriced, unpricedPaid);
        }

        /// <summary>How far apart two valuations of the same rows are, as a ratio of the larger to the
        /// smaller, with equal reading exactly 1. Symmetric on purpose: an estimate twice too high and one
        /// twice too low are the same size of error.</summary>
        private static double Disagreement(long left, long right) =>
            left == right ? 1d : Math.Max(left, right) / (double)Math.Max(1L, Math.Min(left, right));

        /// <summary>One requirement for one profile: the shared build if every part is obtainable, else a
        /// build searched from what is, else the reason there is none.</summary>
        private ProfileBuildDto Judge(
            Requirement requirement,
            PartAvailability.Sources sources,
            Dictionary<string, string> locale,
            string who,
            ref int verified,
            ref int rejected)
        {
            var dto = JudgeQuietly(requirement, sources, locale, ref verified, ref rejected);

            // Every build that is not simply the shared one, named, so the wording can be read against
            // what a player would do with it. The ok ones are the majority and say nothing new.
            if (dto.Status == "ok") return dto;

            var defaults = requirement.Build.WeaponTemplate.TryParseMongoId(out var weapon) ? presets.For(weapon) : null;

            var namedParts = new HashSet<MongoId>();
            foreach (var id in requirement.Build.RequiredItemIds)
                if (id.TryParseMongoId(out var parsed)) namedParts.Add(parsed);

            var avoided = requirement.Baseline == null
                ? new List<string>()
                : requirement.Baseline
                    .Where(part => !namedParts.Contains(part.Template)
                                   && sources.Classify(part.Template, defaults, weapon).Tier == PartAvailability.Tier.Absent)
                    .Select(part => QuestPayloadBuilder.ResolveItemName(part.Template.ToString(), locale))
                    .Distinct().ToList();

            logger.Info(
                $"Quest Tracker: {who} - '{dto.QuestName}' ({dto.WeaponName}): {dto.Status}" +
                (dto.Status == "repaired"
                    ? $" - {dto.Parts.Count} part(s), {dto.Cash:N0} roubles + {dto.Barters} barter(s)" +
                      (dto.FleaEstimate > 0 ? $" + about {dto.FleaEstimate:N0} on the flea" : "") +
                      $", {dto.Nodes:N0} nodes; the shared build needed " +
                      string.Join(", ", avoided.Take(4)) + (avoided.Count > 4 ? $" and {avoided.Count - 4} more" : "")
                    : dto.Status == "blocked"
                        ? $" - {dto.Why}" +
                          (dto.Unmet.Count > 0 ? $"; closest attempt missed: {string.Join("; ", dto.Unmet.Take(3))}" : "") +
                          $"; the shared build needed {string.Join(", ", avoided.Take(4))}"
                        : ""));

            return dto;
        }

        private ProfileBuildDto JudgeQuietly(
            Requirement requirement,
            PartAvailability.Sources sources,
            Dictionary<string, string> locale,
            ref int verified,
            ref int rejected)
        {
            var build = requirement.Build;
            var dto = new ProfileBuildDto
            {
                Key = requirement.Key,
                QuestName = requirement.Quest,
                WeaponTemplate = build.WeaponTemplate,
                WeaponName = build.WeaponName
            };

            if (!build.WeaponTemplate.TryParseMongoId(out var weapon) || requirement.Baseline == null)
            {
                dto.Status = "unsolved";
                return dto;
            }

            var defaults = presets.For(weapon);

            var thresholds = build.Thresholds.Select(t => (t.Field, t.Compare, t.Value)).ToList();

            var mustInclude = new List<MongoId>();
            foreach (var id in build.RequiredItemIds)
                if (id.TryParseMongoId(out var parsed)) mustInclude.Add(parsed);

            var mustIncludeCategories = new List<MongoId>();
            foreach (var id in build.RequiredCategoryIds)
                if (id.TryParseMongoId(out var parsed)) mustIncludeCategories.Add(parsed);

            var named = new HashSet<MongoId>(mustInclude);

            // THE FILTER. Every part of the shared build, classified. Obtainable throughout means the shared
            // build is this player's build too, and nothing is searched. A part the QUEST names is not a
            // reason to search: no build can avoid it, so its row says what it is and the build stands.
            var blocked = false;

            foreach (var part in requirement.Baseline)
            {
                dto.Tree ??= requirement.Baseline;

                var row = Row(part, sources, defaults, locale, weapon, named);

                if (row.Tier == "absent" && !row.Named) blocked = true;

                dto.Parts.Add(row);
            }

            if (!blocked)
            {
                dto.Status = "ok";
                Total(dto);
                return dto;
            }

            // THE REPAIR. Everything the profile can get, plus what the weapon already wears, plus what the
            // quest itself names - a named part is a fact about the quest, not a choice, and if the player
            // cannot get it the report says so rather than searching around it. Owned means LOOSE: a part
            // fitted to a gun they use is never assumed strippable.
            var allowed = new HashSet<MongoId>(sources.Owned);
            if (sources.OwnedWeapons.TryGetValue(weapon, out var inPlace)) allowed.UnionWith(inPlace);

            // THIS PROFILE'S PRICES. A trader's cash price is a fact; a flea price is an estimate; a barter
            // and anything else is valued at the handbook for the objective only - the search needs a
            // comparable number and the handbook is the game's own valuation, but no row ever shows it as
            // a price. Loose in the stash or already on the quest weapon is free.
            var free = new HashSet<MongoId>(sources.Owned);
            if (inPlace != null) free.UnionWith(inPlace);

            var pricing = new WeaponSolver.Pricing
            {
                PerPurchase = partPrices.PerPurchase,
                Free = free,
                Price = template =>
                {
                    var (tier, price) = sources.Classify(template, defaults, weapon);

                    return tier switch
                    {
                        PartAvailability.Tier.Buyable => price,
                        PartAvailability.Tier.Flea => price,
                        _ => partPrices.Of(template)
                    };
                }
            };
            allowed.UnionWith(sources.Buyable.Keys);
            allowed.UnionWith(sources.Barter);
            if (sources.FleaAccess) allowed.UnionWith(sources.Flea.Keys);
            if (defaults != null) allowed.UnionWith(defaults.Occupants.Values);
            allowed.UnionWith(mustInclude);

            var result = solver.Solve(weapon, thresholds, mustInclude, mustIncludeCategories, allowed, pricing: pricing);

            dto.Nodes = result.NodesOpened;

            if (result.Found)
            {
                var verdict = verifier.Verify(weapon, result.Parts, thresholds, mustInclude, mustIncludeCategories);

                if (verdict.Verified)
                {
                    verified++;

                    dto.Status = "repaired";
                    dto.Verified = true;
                    dto.Parts.Clear();
                    dto.Tree = result.Parts;

                    foreach (var part in result.Parts) dto.Parts.Add(Row(part, sources, defaults, locale, weapon, named));

                    // A repaired build made only of obtainable parts, by construction, the quest's own named
                    // parts aside; said out loud if that ever stops being true, rather than trusted.
                    //
                    // And it cannot currently fire, which is worth writing down rather than leaving as a
                    // guard a reader will trust. `allowed` is built from exactly the six sets Classify
                    // recognises, so a part that came through the search is never Absent; the only Absent
                    // rows are the quest's own named parts, which this predicate excludes. To make it
                    // reachable, `allowed` would have to gain something Classify does not know about - which
                    // is precisely what the two hypothetical searches below do, and neither of their results
                    // is passed through here. Kept because that is a plausible future edit and this is where
                    // it would show up, not because it is watching anything today.
                    if (dto.Parts.Any(row => row.Tier == "absent" && !row.Named))
                        logger.Warning(
                            $"Quest Tracker: a repaired build for '{build.WeaponName}' names a part the profile " +
                            "cannot get. The restricted search let one through.");

                    Total(dto);
                    return dto;
                }

                rejected++;

                logger.Warning(
                    $"Quest Tracker: the solver and the verifier DISAGREE about a repaired build for " +
                    $"'{build.WeaponName}' - the solver says it satisfies the requirement, the verifier says " +
                    $"[{string.Join("; ", verdict.Failures.Take(3))}]. It is NOT served. The verifier is right.");
            }

            // BLOCKED. The closest attempt from what the player can get, with the thresholds it missed and
            // by how much - a number rather than a shrug - and then why: would trader progress fix it,
            // would the flea market, or does nobody sell it.
            dto.Status = "blocked";
            dto.Unmet.AddRange(result.Unmet.Select(line => Named(line, locale)));
            dto.Parts.Clear();
            dto.Tree = result.Parts;

            foreach (var part in result.Parts) dto.Parts.Add(Row(part, sources, defaults, locale, weapon, named));

            Total(dto);

            var withGated = new HashSet<MongoId>(allowed);
            withGated.UnionWith(sources.Gated.Keys);

            var gated = solver.Solve(weapon, thresholds, mustInclude, mustIncludeCategories, withGated, pricing: pricing);

            dto.Nodes += gated.NodesOpened;

            if (gated.Found
                && verifier.Verify(weapon, gated.Parts, thresholds, mustInclude, mustIncludeCategories).Verified)
            {
                var needs = new List<string>();

                foreach (var part in gated.Parts)
                {
                    if (allowed.Contains(part.Template)) continue;
                    if (!sources.Gated.TryGetValue(part.Template, out var gate)) continue;

                    needs.Add(
                        $"{QuestPayloadBuilder.ResolveItemName(part.Template.ToString(), locale)} from " +
                        $"{TraderName(gate.Trader, locale)} at loyalty {gate.Level}");
                }

                dto.Why = "trader level - " + string.Join("; ", needs.Distinct().Take(6));

                return dto;
            }

            if (!sources.FleaAccess && sources.Flea.Count > 0)
            {
                var withFlea = new HashSet<MongoId>(withGated);
                withFlea.UnionWith(sources.Flea.Keys);

                var flea = solver.Solve(weapon, thresholds, mustInclude, mustIncludeCategories, withFlea, pricing: pricing);

                dto.Nodes += flea.NodesOpened;

                if (flea.Found
                    && verifier.Verify(weapon, flea.Parts, thresholds, mustInclude, mustIncludeCategories).Verified)
                {
                    dto.Why = $"flea market - reachable once the flea market unlocks at level {sources.FleaLevel}";
                    return dto;
                }
            }

            dto.Why = "not sold - no trader at any level sells what this build needs";

            return dto;
        }

        private static ProfilePartDto Row(
            WeaponSolver.FittedPart part,
            PartAvailability.Sources sources,
            WeaponPresets.Defaults? defaults,
            Dictionary<string, string> locale,
            MongoId questWeapon,
            HashSet<MongoId> named)
        {
            var (tier, price) = sources.Classify(part.Template, defaults, questWeapon);

            var row = new ProfilePartDto
            {
                Slot = part.SlotName,
                Template = part.Template.ToString(),
                Name = QuestPayloadBuilder.ResolveItemName(part.Template.ToString(), locale),
                Tier = tier.ToString().ToLowerInvariant(),
                Price = price,
                Named = named.Contains(part.Template)
            };

            if (tier == PartAvailability.Tier.Absent && sources.Gated.TryGetValue(part.Template, out var gate))
                row.Gate = $"{TraderName(gate.Trader, locale)} at loyalty {gate.Level}";

            // A copy they hold that is not loose: said, with the weapon, beside whatever the part costs to
            // buy - so "strip it or buy another" is a decision they can make from the row.
            if (tier is not (PartAvailability.Tier.Fitted or PartAvailability.Tier.InPlace or PartAvailability.Tier.Owned)
                && sources.Holdings.TryGetValue(part.Template, out var holding) && holding.FittedAnywhere)
            {
                var equipped = holding.FittedToEquipped.FirstOrDefault();
                var stored = holding.FittedToStored.FirstOrDefault();

                row.Where = holding.FittedToEquipped.Count > 0
                    ? $"fitted to your equipped {QuestPayloadBuilder.ResolveItemName(equipped.ToString(), locale)}"
                    : $"fitted to your {QuestPayloadBuilder.ResolveItemName(stored.ToString(), locale)}";
            }

            return row;
        }

        /// <summary>A solver line with every template id in it replaced by the part's name. The search
        /// reports ids; a player reads names.</summary>
        private static string Named(string line, Dictionary<string, string> locale) =>
            System.Text.RegularExpressions.Regex.Replace(line, "[0-9a-f]{24}",
                match => QuestPayloadBuilder.ResolveItemName(match.Value, locale));

        /// <summary>Cash, barters and flea estimates summed over what the player would have to obtain.</summary>
        private static void Total(ProfileBuildDto dto)
        {
            dto.Cash = 0;
            dto.Barters = 0;
            dto.FleaEstimate = 0;

            foreach (var part in dto.Parts)
            {
                switch (part.Tier)
                {
                    case "buyable": dto.Cash += part.Price ?? 0; break;
                    case "barter": dto.Barters++; break;
                    case "flea": dto.FleaEstimate += part.Price ?? 0; break;
                }
            }
        }

        private static string TraderName(MongoId trader, Dictionary<string, string> locale) =>
            locale.TryGetValue($"{trader} Nickname", out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : trader.ToString();

        private PmcData? TryGetProfile(MongoId sessionId)
        {
            try
            {
                if (string.IsNullOrEmpty(sessionId.ToString())) return null;

                return profileHelper.GetPmcProfile(sessionId);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
