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

## Where it stands

Measured on `6f9d894`, 2026-09-12, one install (SPT 4.1.5 plus WTT weapon mods, 6,567 templates):

| | cold | warm (trained) |
|---|---|---|
| requirements satisfied, verifier-scored | 60 of 60 | 60 of 60 |
| parts across all sixty builds | 588 | 572 |
| proven minimal (verifier's lower bound reached) | 19 | 21 |
| proven irreducible (no part removable) | 60 | 60 |
| parts proven necessary | 503 of 588 | 503 of 572 |

Training rate: 200 rounds in 6.5-7.1 minutes, ~28-31 rounds/minute, on half the machine's cores.

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
