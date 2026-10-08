# Hybrid enhancement and fresh statistics

Status: complete. The selected hybrid is installed, and all 2,500 new statistics games have finished, reconciled, and been published.

## Completed experiments

Collected 800 independent, balanced-hero hybrid self-play games with the real headless engine. The frozen teacher made 238,614 actual decisions; 44,163 public positions were retained. Training used 640 whole games and validation used 160 whole games (9,000 positions). Exact repeated visible positions across the split were excluded from training. Each training game had equal total sampling weight.

The existing generic effect layout was preserved. Six candidates tested head-only action learning, head-only terminal-outcome value calibration, their combination, broader semantic action learning, broader semantic value learning, and outcome-advantage-weighted action learning. Search choices were supervised targets, not misrepresented as samples from the network for PPO. Value calibration used actual completed-game outcomes in the acting seat's perspective. Every candidate retained legal masks and reference-policy regularization.

| Pilot candidate | Wins / 160 against the frozen two-world hybrid | Decision |
|---|---:|---|
| Action heads | 77 | Reject |
| Value head | 76 | Reject |
| Both heads | 76 | Reject |
| Semantic action update | 76 | Reject |
| Semantic value update | 74 | Reject |
| Outcome-weighted action update | 74 | Reject |
| Continue long turns up to 64 steps | 78 | Reject |
| Six leading action groups | 82 | No convincing gain |
| Stronger policy prior, 0.04 | 72 | Reject |
| Four hidden-state samples | 85 | Reject after fresh confirmation |

The fresh confirmation finished **404–396 (50.5%)**, with a **47.625–53.375%** paired-bootstrap 95% interval. The predeclared promotion threshold was not met. **The selected setup retains generation 19062 and the two-world hybrid: four leading action groups plus the sampled fallback, depth 24, 512 finishing-search nodes, eight CPU workers.** Extra turn-depth extension remains disabled.

All pilots were exploratory; selecting their highest score introduces selection bias. The confirmation used a separate seed range and a fixed 800-game budget. Promotion required the paired-bootstrap 95% lower confidence bound to exceed 50%. It did not, so the two-world hybrid was retained.

The identical-model control produced 20–20 with all 20 paired game histories identical. Tests alternate the treatment seat, preserve each paired seed and hero assignment, and use the same random stream for each physical seat. Confidence resamples intact seed pairs. All six trained candidates scored below 50% in their pilots and are kept isolated; this does not establish a statistically significant population regression for every candidate.

## Correctness

Both the four-world candidate and the final two-world setup passed fresh 4,608-trial tactical batteries across all five heroes, including private-information invariance checks. It avoided all eight previously reviewed premature End Turn actions. Passive replay probes preserved the original eight game histories and outcomes. Resource-macro invariants passed on 116 positions / 348 symmetry comparisons; 64 forced-step differential checks passed. The headless engine suite passed 258/258 tests.

Only public observations were recorded. Outcomes are training labels, never inference inputs. Hypothetical branches do not enter actual game statistics. Training, pilots, and confirmation games are excluded from the final statistics view. No Unity tests or artificial UI delays were used. The selected native build passed the initial 64-decision compatibility check and a complete 252-decision game: synchronous and background planning produced identical states throughout, with no background mutation of the source. Nine statistics/dashboard tests passed, including headless-browser checks.

## Files

- `hybrid-enhancement-pilots-2026-09-27.json`: complete pilot scores and paired confidence intervals.
- `hybrid-enhancement-manual-probes-2026-09-27.json`: the eight preserved replay positions.
- `/home/lva/.local/share/shards-training/2026-09-27/hybrid-enhance-experience-v1`: frozen collection runtime and binary public experience.
- `hybrid-enhance-training-v1`, `hybrid-enhance-semantic-learning-v2`, `hybrid-enhance-value-learning-v3`, `hybrid-enhance-advantage-learning-v4`: isolated training checkpoints and validation curves under the same run root.

The existing generation-19062 weights remain unchanged: `f15c9d40644f6ef7e84ebf44d325ec4d6f3bae771e3f6ac7f7904a52baf4a177`.

## Game deployment

The hybrid is now the source default and is installed at `E:\Bureau\pascension-windows-v1.0.4`. Only the AI assembly was replaced; its installed Core/Engine/Content dependencies were verified against the binaries used by the headless compatibility test. The old assembly is retained under `ai-backups\before-hybrid-20260928-000957`. No Unity instance or periodic console process was launched. The English/French source changelog documents the hybrid planner.

Installed AI SHA-256: `15854886f9d77d22277a1399fe00a3130a6e0570b77c6a78e75664e77607e12a`.

## Statistics cohort

Run directory: `/home/lva/.local/share/shards-training/2026-09-28/hybrid-statistics-2500`.

Exactly 2,500 new independent engine seeds, 125 games per ordered distinct-hero pair, both seats using identical frozen weights and search. Each hero played 500 games in each seat. The statistics window is pinned to this cohort; all training and strength tests are excluded. Final strategy and victory-cause reconciliation is complete.

Simulation took **1,928.70 seconds (32 minutes 9 seconds), 1.296 games/second**, using the RTX 5090 and eight CPU cores. All 2,500 engine outcomes matched their strategy traces and published seat totals. There were zero draws, censored games, discarded unfinished games, or unattributed victories. No model updates occurred. The policy and installed assembly hashes still match their selected versions.

| Hero | Wins / 1,000 games | Win rate |
|---|---:|---:|
| Tetra | 613 | 61.3% |
| Volos | 522 | 52.2% |
| Ko Syn Wu | 503 | 50.3% |
| Decima | 439 | 43.9% |
| Rez | 423 | 42.3% |

Seat 0 won **1,403 / 2,500 (56.12%)**; seat 1 won 1,097 (43.88%). Mean ending round was **10.5452**. Victory causes were **862 mastery**, **16 Comet**, **1,572 normal damage**, and **50 other health loss**. These describe this fixed AI and current rules, not proven optimal human balance. The dashboard includes 469 observed strategy rows; these overlap and must not be summed as independent wins.

[Open the statistics window](http://localhost:8768/statistics). Validation evidence is in `hybrid-statistics-validation-2026-09-28.json`; final snapshot ID is `39c5f2e31bab5d13`.

The training experiments did not demonstrate a stronger checkpoint. This result does not establish that further improvements are impossible. The shipped change makes the previously opt-in hybrid the game default while avoiding unvalidated weight or search-budget changes.
