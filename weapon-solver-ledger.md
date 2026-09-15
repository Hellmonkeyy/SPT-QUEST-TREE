# Weapon solver ledger

Every change tried against the weapon-build search, what it measured, and - especially - what it did
not. The negative results are the expensive half: each one below cost a build, a 200-round run, or a
retraction, and without them written down the next iteration retries them.

**How a figure gets into this file.** One change at a time, against a fixed baseline at the same
budget. Commit, build, measure - in that order, so every number is attributable to a commit rather
than to a working tree. The score is the *verifier's*, never the solver's opinion of itself. Cold
means no history file (a stranger's first boot); warm means the trained history is present.

**Stop rule.** If three consecutive changes measure no improvement, stop and say so rather than
grinding. Report the negative result; do not keep a change out of momentum.

**Stopped 2026-09-13 for release readiness**, by decision of the coordinator and the user, after item 3 landed
and the sweep answered. Not started, deliberately: the price lower bound (item 4, drafted in the scratchpad
as `edit_bound.py`, unapplied), the joint bound, and anything aimed at 60-of-60 provably minimal. The
measurement that mattered most today - 23 and 38 of 60 builds naming a part the player could not buy - cost
minutes; the hours went on minimality. The solver list is unbounded and the release list is short.

## The unit

**An attempt is one search of one requirement.** Until 2026-09-12 the unit was a ROUND - one attempt for
each build not already settled, 39 of the 60 - so **one old round is about 39 attempts**, and the 200-round
figure every earlier measurement is quoted in is about 7,800 attempts. The regression gate still spends
exactly that, so old and new numbers compare directly.

## Where it stands

Measured on `de6a36d`/`0c638ed`, 2026-09-12, one install (SPT 4.1.5 plus WTT weapon mods, 6,567
templates), 16 logical processors on 8 cores:

| | cold | warm (trained) |
|---|---|---|
| requirements satisfied, verifier-scored | 60 of 60 | 60 of 60 |
| parts across all sixty builds | 588 | 572 |
| proven minimal (verifier's lower bound reached) | 19 | 21 |
| proven irreducible (no part removable) | 60 | 60 |
| parts proven necessary | 503 of 588 | 503 of 572 |

Training rate: **~2,500 attempts/minute**, against **~1,180/minute** before the round barrier came out
(30 rounds/min x 39 unsettled builds). Same machine, same thread count. With the falsifier on and the
durable records being written it is ~1,760/min - the cost of remembering what each check established.

Durable evidence in the history after one five-minute falsification run: every one of the 60 builds has a
recorded bound, 21 are at it, those 21 have survived **5,022 adversarial searches** between them (the
least-tested of them 237), **566 million nodes** have been spent trying to beat them, and the bounds have
stood for 5 sessions. The worst single build costs 844,088 nodes to search.

The seven-condition gate (200 training rounds, two warm boots, one cold) passed in full on `e875ce2`.
It last failed on `6f9d894` - 20 proven instead of 21 and 573 parts instead of 572 - and both failures
were one unassemblable MP-133 entry that training had written unverified. **Zero disagreements over
200 rounds is not proof the search can no longer propose such a build**; it means it did not in that
run. The guard in `6a2f455` is what makes it harmless if it does.

## What was tried

| # | change | measured | kept |
|---|---|---|---|
| 1 | Three-stage search (PLAN locked required parts / DRESS greedy / CLIMB against the stat model), restarted from several dressings | 18 -> 55 of 60 | yes |
| 2 | `Potential` look-behind heuristic - a part scored including what mounts behind it | 55 -> 57 | yes |
| 3 | `Polish` - a second look at a trial's own sub-slots, chosen against the model | 57 -> 59 | yes |
| 4 | Required slots and required categories enforced as structural gaps rather than reported | 59 -> 60, and 60 -> 33 -> 60 when enforcement first counted as failure | yes |
| 5 | Pruning passes: `Shed` (leaves), `Bypass` (pass-through intermediates), `Condense` (two-for-one) | 14.2 -> 9.87 parts per build | yes, except see N2 |
| 6 | Independent verifier reading `TemplateTable` directly, scoring in place of the solver | score fell 60 -> 59; the missing one was real (MP-133 width) | yes |
| 7 | Grid footprint taught to the *search*, not only the verifier | 59 -> 60 cold | yes |
| 8 | `WeaponBuildCache` - the history as incumbent, generation-seeded, fingerprint-invalidated | 592 cold -> 572 warm over 358 generations | yes |
| 9 | Parallel training, half the cores | ~5 rounds/min -> ~31 rounds/min | yes |
| 10 | Verifier lower bound: tree knapsack, Lagrangian grid over ergonomics/recoil, forced required slots | 0 -> 19 cold / 21 warm proven minimal | yes |
| 11 | Never remember a build the verifier rejects (`6a2f455`) | 200 rounds: no change to 572 parts or 21 proven, and the two gate conditions it explains went from FAIL to PASS | yes |
| 12 | Record the binding threshold per build and let the climb keep buying slack on it (`81396c7`) | 200 rounds: **no change at all** - 572 parts, 21 proven. See N7: inert by construction | kept, see N7 |
| 13 | Proof tables made thread-safe, plus a per-build bound cross-check (`7590b14`) | 13,070 comparisons against the single-threaded survey, **0 disagreements**; no throughput change | yes |
| 14 | Training threads 8 -> 15 | **0** - 30 rounds/min either way. See N8 | superseded by 16 |
| 15 | Round barrier deleted for a per-build work queue (`de6a36d`) | ~1,180 -> **~2,600 attempts/min**, 2.2x, on the same threads | yes |
| 16 | Training default to half the LOGICAL processors, `QUESTTREE_TRAIN_THREADS` to override (`0c638ed`) | 15 threads on 8 cores pinned every processor at 100%; 8 is the polite default | yes |
| 17 | `QUESTTREE_FALSIFY`: attack every build called minimal with the widest search there is (`de6a36d`) | 6,994 adversarial searches, **0 counterexamples** | yes |
| 18 | Every expensive check leaves a durable record - bound, its stability, failed falsifications, nodes spent, search cost - invalidated by the item fingerprint (`94e74d1`, `447c84d`) | evidence accumulates across sessions; ~30% slower with the falsifier on | yes |
| 19 | Per-profile filter-and-repair over the shared baseline (`c991646`): six availability tiers, three ownership states, the planner's `Extend`/`Nearest` made to honour `Allowed`, verifier on every repair, three-way diagnosis of a blocked build, `/questtree/builds` | see "Availability" below: a fresh profile goes from 6 of 60 reachable to 14 usable + 9 repaired (verifier 9 of 9), 37 blocked by trader level with the trader and level named; both real profiles 59 and 58 of 60 usable as shipped | yes |
| 20 | Client reads `/questtree/builds` and lays it over the shared build; mirror catches up with `ed3e44e`, schema 9 -> 10 (`1c8a56c`) | compiles and degrades to the old panel on every missing input; **not yet seen on screen** | yes, unverified |
| 21 | Objective `price + PerPurchase x purchases`, handbook-priced for the shared baseline and trader/flea/stash-priced per profile; the incumbent's cost measured live on every path (`7cff9fc`) | 10,554,732 roubles across 60 at handbook + 10k/purchase (175,912 per build), 0 unpriced purchases, 6,117 templates priced; the launch search found cheaper builds for 18 of 60 in its first 390 attempts; gate 8 of 9 (6a failed for defect 8, not the objective) | yes |
| 22 | PerPurchase sweep, five cold solves compared per build against the 10k default | 0: 19 of 60 differ (614 parts, 435 changes); 5k: 11 (606); 10k: - (603, 421); 25k: 11 (600, 415); 50k: 13 (600, 413). Not theatre: the knob moves 11-19 builds and trades parts for purchases monotonically. 10k kept | measurement |

## Negative results - do not retry these without new information

- **N1. Shape variation (`wander`) found nothing in 160 rounds.** Re-solving from scratch under a part
  ceiling instead of from the incumbent. It is the "search harder" kind of change: linear in effort and
  forgotten at process exit. Kept only at 1-in-4 rounds because it costs nothing there.
- **N2. Two-for-one substitution (`Condense`) finds nothing at either width tried** - 8 candidates or
  96. It is not removed, but no future round should be spent widening it again.
- **N3. The solver's own `Floor` is not a lower bound.** It is the size of one particular mandatory
  skeleton. Gunsmith 18 comes in at 9 parts against a floor of 10, which settles it. Only the
  verifier's `LowestPossible` may be reported as a bound.
- **N4. `items.json` is not the item database.** Vanilla has 4,673 templates; this install's merged
  database has 6,567. Two python Pareto enumerators were written against the file and their
  feasibility conclusions were withdrawn. Anything reasoning about what parts exist must read the
  live database, not the file on disk.
- **N5. "All 60 are unreachable" was wrong.** Asserted from those same enumerators, retracted. All 60
  are satisfied.
- **N7. Mechanism 2 - "buy slack on the binding threshold" - measured exactly zero, and was inert by
  construction.** 56 of the 60 builds do have a binding threshold recorded (18 of them ergonomics and
  magazine capacity together), so the mechanism had a target and the data is persisted. It changed nothing
  because `Climb` exits the moment the build is `Done` - gaps closed and shortfall zero - so for a build
  that already satisfies its quest the headroom term is never consulted at all, whatever its cap. The idea
  is not refuted; the place it was applied cannot act on it. The directed version has to live where parts
  are actually removed (the pruning passes), not in the climb.
- **N8. Raising the thread count while a round is a barrier does nothing.** 8 threads to 15: 30 rounds a
  minute either way. A round was a `Parallel.ForEach` over all sixty requirements, so it ended when the
  SLOWEST build ended - fourteen threads idle behind one straggler spending its full two-second ceiling on
  376,000 nodes, three times a round. The fix was deleting the barrier, not adding workers, and it was the
  non-result that identified it.
- **N9. Per-move no-good lists about REMOVALS are already subsumed by the irreducibility proof.** All 60
  builds are proven irreducible on every boot - the verifier removes each part with its subtree and
  re-checks - so "removing part X fails" is already known and proven for every part of every build.
  Recording those failures as learned facts would re-derive what is measured. What is not covered is swaps
  and multi-part restructures.
- **N10. A zero is not evidence without its denominator, and this bit twice in one day.** The regression
  gate reported 20 solver/verifier disagreements that never happened, because `Select-String` is
  case-insensitive by default and the progress line's own "0 disagreement(s)" matched the pattern
  `DISAGREE`. And the first evidence line printed "0 adversarial searches, 0 never attacked, 0 nodes" -
  not a wrong answer but an empty denominator, because the bound was only recorded from the training path
  and a normal launch trains nothing. Every count reported now carries what it is a count out of.
- **N6. A 200-round training gate cannot prove hash-order determinism.** It boots a handful of times,
  not fifty. Determinism (ordinal-sorted candidate arrays, seeded `Random`) last had a full 50-of-50
  run on `c34a5bc` and has not been re-proven since.

## Defects found, and the shape they share

Every one of these failed in the direction that *looks like success*, which is why each needed an
independent check rather than a closer reading.

1. **The cache hid the width failure.** A warm boot served the remembered MP-133 build and scored 60;
   the build was 5 wide against a limit of 4. Found by scoring from the verifier instead.
2. **An invalid entry blocked its own replacement** (`6f9d894`). The write path required a *smaller*
   build, so a broken 5-wide entry was rejected and re-solved on every boot forever and the valid
   build was never written, because it was one part larger.
3. **A gate check could not tell "passed" from "not run".** A blank rendered identically to a zero, so
   a mistyped pattern would have read as a pass. The harness that replaced it found the same defect in
   itself on its first run (`exit 2` inside a function does not stop a script; `History` is an alias
   for `Get-History` and shadows a function of that name).
5. **The proof tables were shared across threads with no synchronisation** (`7590b14`), while the comment
   beside the parallel loop said "the verifier's knapsack tables have their own locks". They had none. A
   plain Dictionary written from fifteen threads can lose entries, spin, or throw - and a corrupted bound
   proves builds minimal that are not, with no crash and no log line.
6. **The training banner said "using a core"** (`e40d8a1`), which was true when training was
   single-threaded and had been wrong ever since. A reader believed the log and concluded training had
   reverted to one core while it was running on eight. A message that drifts from the code is the same
   defect as a check that cannot fail: it reads as authoritative and it is not. Hence the rule that
   user-facing output reports state rather than asserting it.
4. **Training wrote builds the verifier never saw** (`6a2f455`). The audit lived only on the path that
   serves a build, which runs after the write and not in training at all. Two hundred rounds kept an
   unassemblable MP-133 because it was one part smaller than the legal build it replaced.
7. **`Changes: 0` read as a perfect score** (found 2026-09-13 on `132e430`). Every history entry carried
   `Changes: 0` - the field was added by `ed3e44e` and only the non-training served path ever records it
   (`132e430`), and training never calls `Describe`. The write rule `Cheaper(changes, parts, 0, parts)` can
   only be satisfied by a zero-change build with fewer parts, so the 8,219-attempt training phase of the
   gate wrote nothing, and condition 5 (monotone) passed as "0 -> 0 changes, 572 -> 572 parts" - a check
   whose inputs were structurally constant. The first non-training boot recorded real values, the launch
   search then legitimately wrote cheaper, larger builds (572 -> 583 -> 587 parts, proven-on-part-count
   21 -> 15), and condition 6 failed for the right reason measured the wrong way (below). **Still open for
   the carried-over case**: a history from another install has its `Changes` reset to zero and training
   compares against that zero until a normal boot describes it. The fix is to measure the incumbent's cost
   live rather than trust the file, which lands with the price objective.

8. **A replacement discarded the requirement's evidence** (`535a160`, caught by the restated condition 6 on
   `7cff9fc`). `Put` built a fresh entry, so the bound, its session count, the search cost and the
   falsification evidence restarted at zero whenever a cheaper build replaced the remembered one; the next
   survey re-derived the bound and the gate reported it moving "0 -> 6". Rare under the changes objective
   (late improvements were few and the next attempt on the key re-recorded the bound before the snapshot),
   constant under cost while the history was being rewritten - and it had already cost evidence: 5,022
   recorded falsifications on the shipped file, 2,392 on the install's. The first defect the restated check
   caught rather than let pass. The bound, its stability and the search cost now follow the requirement;
   falsification evidence follows a replacement only of the same part count. Gate on `535a160` (clean
   stamp): PASSED, all nine - 7,805 attempts, 0 disagreements, 0 rejections, 5,930 bound comparisons / 0
   disagreements, 10,027,850 -> 9,986,014 roubles at 607 -> 609 parts, 60 bounds compared and 0 moved on
   both transitions, file predicts 15 and both boots reported 15, cold 60 of 60.

**The shipped history** (`Source/Tarkov-QuestTree-Server/weapon-builds.json`, copied from the install after
that gate): generation 102,546, solver 9, 60 entries, 609 parts, 9,986,014 roubles at handbook prices plus
10,000 per purchase, 409 changes, no entry with a zero cost or change count, 60 bounds recorded (15 at the
part-count bound, the shortest-standing for 6 sessions), 2,392 failed falsification attacks over 67,951,402
nodes, item fingerprint `7F63D6257473D0B58F7CF6C02358EFCB9E4C008745A1AAAEE7F30FCAC0C360E1`. On an install
with a different item set it is carried over: every build re-verified before use, every proof reopened.

**The standing rule these eight share (2026-09-13):** any condition whose inputs can be structurally
constant must be proven able to fail before it counts as evidence. A field some code path never writes, a
count with no denominator, a comparison against a value that is zero by default - each reads as a pass.
Sweep candidates, not yet swept: every check in `regression.ps1` and `target.ps1` that compares a figure
some launch mode does not produce.

## Queued, with what each is expected to be worth

Ordered by expected value per unit of work. The governing principle: prefer mechanisms that
permanently shrink the problem over mechanisms that search harder. Shrinking compounds across rounds
and survives process exit; searching harder is linear and forgotten.

1. **Record the binding constraint per build.** One or two thresholds sit at zero slack on a build that
   cannot shrink, and that is already computed and discarded. Storing it turns uniform exploration
   into moves aimed at the binding number. Expected: the largest single lever, because it costs no new
   information.
2. **A no-good list at move granularity, with reasons.** Not "this build resisted N times" but "removing
   the gas block empties a required slot". Permanent facts about a build under one database, so each
   round is strictly cheaper than the last.
3. **Credit assignment per move class.** Spend effort in proportion to how often each move type has
   ever paid. Automates the judgement made by hand in N1 and N2.
4. **Transfer across similar problems.** 60 builds over 46 weapons sharing slots and parts; a learned
   best-known occupant per slot lets a new build start from accumulated knowledge.
5. **Persist bounds, not just builds.** Bounds only tighten, so the settled set only grows and the
   searchable problem shrinks irreversibly across sessions.
6. **Settle and never revisit.** Partly in: a build whose size equals its bound is finished forever.

**The rule that protects all six.** Every one of them is a way to stop looking at something, and a
wrong entry in a no-good list caps build quality with no symptom - defect shape number 5. So: a
no-good entry may only be written from a *verified* failure, never a heuristic's opinion; no-good
lists and bounds are invalidated by the same database fingerprint as the builds; and training
periodically re-tests a random sample of stored no-goods and asserts they still fail. A sampled
no-good that now succeeds is a reason to stop and report, not to work around.

## The objective changed twice on 2026-09-12, and once more is pending

Recorded because part-count bounds sitting beside a different objective read as drift otherwise.

1. **Fewest parts.** What the search was built for. Reached 572 parts across 60 builds, 21 of them
   proven minimal, with 566 million nodes of falsification evidence behind those proofs.
2. **Fewest changes from the weapon's default preset** (`ed3e44e`) - swaps plus additions, with part count
   kept as the tiebreak below it. The MP-133 evidence: the default ships a 510mm barrel and the search
   picked the 510mm barrel *with rib* - same part count, same to every threshold, one already on the gun.
   Current state: **488 changes across the 60 builds, 8.13 per build**, all 60 weapons have a preset.
3. **Landed (`7cff9fc`): `price + PerPurchase x purchases`**, with parts on the default preset free, loose
   stash parts free in the per-profile pass, and unobtainable parts excluded there (`c991646`).
   The MP-133 evidence again: the quest wants a combined tactical device, the search fitted a Zenit
   Klesch-2P at 15,676 roubles, and the cheapest qualifying device is an NcSTAR laser at 4,100 - which is
   what every community guide recommends. Price was invisible and headroom broke the tie.

**Everything proven under objective 1 stays in the file, labelled as being about part count.** It is still
true about part count. The count proven minimal over the new objective is reported as **zero**, because the
knapsack bound answers a different question, and carrying 21 forward under a new meaning would be the
clearest case yet of a number that reads as evidence and is not.

**What a changes/price bound looks like, as a first estimate.** Much easier than the part-count bound. For
each slot that must be filled, take the cheapest legal occupant and sum: that is a valid lower bound with
no knapsack, no Lagrangian grid and no subset DP - and a kept default contributes zero. The part-count bound
needed a subset-constrained DP over roughly 9.6 billion steps and stalled at 21 of 60. This is the next
thing to measure.

## Availability - measured 2026-09-12, restricted 2026-09-13

Before any pricing work: **how often does the mod recommend a part the player cannot get?** First measured
on `187bd20` (stamp `d48f6ac-dirty`) and reproduced exactly on a clean build of `132e430`, against live
trader assorts with loyalty and quest locks already applied by the server, on both profiles of this install,
history at 572 parts:

| profile | builds naming an unobtainable part | distinct parts | gated behind trader progress | sold by nobody |
|---|---|---|---|---|
| level 51 | **23 of 60** | 19 | 7 | 12 |
| level 69 | **38 of 60** | 30 | 14 | 16 |

With every trader at loyalty 1 - the player actually doing Gunsmith - **54 and 57 of 60** were out of trader
reach. The figures move with the history: as the changes objective keeps more default parts, fewer have to
be bought, and on the 587-part history of `c991646` they read 26 and 30 of 60 (loyalty 1: 54 and 57).

**"Owned" was three things** (`c991646`). The level-51 profile looked better off because it "owned 179
parts". Walked from the inventory roots and split: on the 585-part history, 64 loose in the stash (free),
33 already fitted to a copy of the quest's own weapon (in place), **51 fitted to a stored weapon and 7 to an
equipped one** - and those 58 are priced as purchases, because stripping a working gun is the player's call.
Counted that way its blocked figure went from 17 to 26 of 60. The level-69 profile: 4 loose, 2 in place, 4
stored, 10 equipped.

**The flea, answered in process** (the file has 4,673 of the 6,567 templates and cannot). 4,822 templates are
flea-listable with a price, 17 listable but unpriced, 1,728 refused by the game's own rule
(`RagfairServerHelper.IsItemValidRagfairItem`, which applies the blacklist). `GetFleaPriceForItem` returns 1
rouble for an unpriced item; 1 is treated as unpriced. Of the parts no trader sells the level-51 profile, 16
of 17 are on the flea and 1 - mod-injected - has no route at all; level-69: 21 of 23, 2 with no route (1
mod-injected, 1 vanilla). Both profiles are past `RagFair.MinUserLevel`, so with the flea as a tier **59 and
58 of the 60 shared builds serve as they are** and the rest are repaired and verified (1 and 2 of 2).

**The fresh profile** - the one neither real profile is - as a hypothetical derived from the locked-inclusive
trader read (every trader at loyalty 1, empty stash, no flea; quest-locked offers count by loyalty only, so
it reads slightly MORE obtainable than a real fresh profile): on `c991646`, **14 of 60 shared builds usable
as they are, 9 repaired within reach (verifier 9 of 9, 0 rejected), 37 blocked by trader level, 0 by the
flea, 0 that nothing sells.** 2.7 million nodes in 4.2 s on one thread. A quest-named part the profile
cannot buy does not block a build - no search can avoid it - and its row says the quest names it.

**The cost of restriction**, over the 9 repaired fresh builds: 10.33 parts and 14,202 priced roubles per
shared build (36 parts absent or barter, unpriceable, so the shared cost is understated) against 12.11 parts
and 30,000-odd roubles per repaired build. Roughly two parts and 15,000 roubles per build is the price of
advice that can be followed.

**What a blocked build says** (the wording the coordinator asked to check): *'Gunsmith - Part 1' (MP-133):
blocked - trader level - Delta-Tek Sprut mount for pump-action shotguns from Jaeger at loyalty 2; closest
attempt missed: width 5, needs <= 4 (short by 1)*. And *'Gunsmith - Part 8' (AKS-74N): trader level -
AKS-74/AKS-74U Zenit PT Lock from Skier at loyalty 2; closest attempt missed: recoil 280.67, needs <= 275
(short by 5.67); could not fit the required part AK Zenit PT-3 "Klassika" stock (reachable, not placed)* -
the named stock mounts on the gated lock, and the diagnosis names the lock.

**Two holes closed on the way:** the planner's `Extend` and the category chooser `Nearest` never consulted
`Allowed`, so a restricted search could route a named part through an unobtainable intermediate or plan a
suppressor nobody sells and report the quest solved. With `Allowed` null both are no-ops, so the shared
baseline is unaffected by construction.

**Expect the shared baseline's numbers to move when restriction is on**, and read it as the filter working:
the search gravitates to mod-injected parts with better stats, which are often loot-only, and a restricted
search is pushed back toward vanilla purchasable parts.

## The two gates

They have different jobs and are kept separate. Losing the ability to detect a regression while reaching
for a number is how the width defect survived as long as it did.

**The regression gate** must always pass. 7,800 attempts of training (the old 200 rounds), then two warm
boots and one cold one: the attempt budget completes, zero solver/verifier disagreements, zero rejected
history entries, every bound identical under concurrency, the objective never worsens, **condition 6 (below)**,
the history serves cleanly twice, all 60 satisfied and irreducible, and a cold solve scores what a warm
one does.

**Condition 6, restated 2026-09-13.** It read: *training said N settled; both warm boots must report N
proven.* Under the changes objective a build legitimately grows past its part-count bound between boots,
so on `132e430` it failed as "training said 21; boots said 21 and 15" - two different questions being
compared, not a bound moving. It now reads: *(a) every recorded bound identical per build across the
training run and both warm boots, naming any that moved; (b) each warm boot's reported proven count equals
what the file it read predicts (parts <= bound).* Both are about the bound alone, which is a property of
the item data whatever the objective, and (b) is the check that would have caught a served figure
disagreeing with the file. Both were made to fail before they replaced the old form: `control6.ps1`
corrupts one entry's bound to its part count and boots once - (a) fails naming the entry, (b) fails with
the file predicting one more proven than the boot reported. Result on `c991646`: the gate PASSED in full - 8,284 attempts, 0 disagreements, 0 rejections, 6,151 bound comparisons with 0 disagreements, 412 -> 407 changes at 587 parts, (a) 60 compared and 0 moved on both transitions, (b) file predicts 16 and both boots reported 16, cold 60 of 60. `control6.ps1` on `1c8a56c`: (a) FAILED naming the corrupted entry (6 -> 4), (b) FAILED with the file predicting 17 against 16 reported - both checks can fail, which is what makes their passes evidence. The control's first run aborted on a stamp mismatch AFTER corrupting the file and booting, and left both behind; it now restores in a finally block, and the abort was the harness working.

**The target gate** is the definition of done and fails today: five minutes of training, passing only at 60
of 60 provably minimal. Its other five conditions exist because a gate that rewards a higher proven count
is an incentive to loosen the prover, and that is the one direction a prover may never err:

1. 60 of 60 provably minimal - **21 of 60 today**.
2. 60 of 60 satisfied by the independent verifier - passing.
3. 60 of 60 proven irreducible - passing.
4. Every bound identical between the parallel run and the single-threaded survey, per build, not by total -
   13,070 comparisons, 0 disagreements.
5. Zero order-independence warnings - none in any run so far.
6. **Falsification**: for every build called minimal, the widest search the solver has, asking for a
   strictly smaller one, with any find having to pass the verifier before it counts. 6,994 adversarial
   searches, **0 counterexamples**. This is the only condition that tests the proof against reality rather
   than against something else this code believes, and it is what makes 1 to 5 worth anything.

Route to 60 is bound work, not build work: 572 parts against 503 proven necessary, and the gap is
thresholds bounded independently of each other (Gunsmith 10 sits at 9 parts against a bound of 2). The
subset-constrained joint bound is the next real lever.


---

## The hand-in, 14 September 2026 - PASSED

A build this solver produced was assembled in game and **accepted by the trader**.

Every gate in this ledger tested the solver against other code that shares its assumptions: the
verifier re-reads the item data but reads the same item data; the irreducibility proof removes parts
and re-derives the same requirements; the falsifier attacks the bound with the same search. All of it
could have been consistently wrong together, and no amount of it could have told us.

A trader accepting the gun is the only test outside that circle, and it is the one that was missing
from the day the solver was written. It passes.

**What it does not settle**, because one hand-in is one data point: the extended-versus-collapsed
question for grid size is only answered for a build that did not turn on it, and the other 59 builds
have not been through a trader. The `WeaponStatModel` remains the thing to leave alone - it is now
verified by hand at the workbench twice over, once by arithmetic and once by outcome.
