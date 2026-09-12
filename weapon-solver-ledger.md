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

## The two gates

They have different jobs and are kept separate. Losing the ability to detect a regression while reaching
for a number is how the width defect survived as long as it did.

**The regression gate** must always pass. 7,800 attempts of training (the old 200 rounds), then two warm
boots and one cold one: the attempt budget completes, zero solver/verifier disagreements, zero rejected
history entries, every bound identical under concurrency, the part count never rises, the proof count
survives the parallelism, the history serves cleanly twice, all 60 satisfied and irreducible, and a cold
solve scores what a warm one does.

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
