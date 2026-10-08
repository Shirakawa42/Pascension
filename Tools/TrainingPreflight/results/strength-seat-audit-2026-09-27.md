# Strength-test seat audit — 2026-09-27

The 55.27% result compared generation **18,232** against **13,437**, not adjacent checkpoints. The checkpoints were saved at 10:55:35 and 05:49:48 Paris time. They are separated by 4,795 generations and 3h47m of charged training time (about 5h06m of wall time, including evaluations).

The 16,384-game evaluator ran 8,192 engine seeds twice: policy A in seat 0 and then seat 1. Action selection uses `host.seats == seat_a`; terminal scoring reads `host.rewards[ended, seat_a]`. Every lane is stopped after its first terminal outcome. No censored results were included or silently treated as draws. Both policies remain frozen.

| Fresh verification | Games | Overall score | A in seat 0 | A in seat 1 |
|---|---:|---:|---:|---:|
| Generation 18,232 vs itself; normal draft | 4,096 | 48.93% | 38.87% | 58.98% |
| Generation 18,232 vs 13,437; normal draft | 4,096 | 54.16% | 45.75% | 62.57% |
| Generation 18,232 vs 13,437; assigned random heroes | 4,096 | 51.46% | 58.81% | 44.12% |

Each row has exactly 2,048 games per policy-A seat. The original larger normal-draft comparison scored 55.27% (9,054 wins, 3 draws, 7,327 losses); its conservative 95% pair-level bound is 53.77–56.77%. The fresh normal-draft comparison's bound is 51.16–57.16%. The identical-policy control is consistent with 50% (45.92–51.93%).

The random-hero comparison's bound is 48.46–54.47%: it does **not** establish an improvement under assigned heroes. Normal-draft results measure both drafting and subsequent play. The seat effect changes direction when hero drafting is removed; the seat-swapped design protects the aggregate model comparison, but model strength must still be reported with its hero-selection regime.

Generation 18,232 is retained for the final dataset because it beat the previously promoted best in normal play, and its random-hero estimate was positive but inconclusive. This does not prove that it is superior under every hero or matchup.

All verification matches above are excluded from the final 50,000-game statistics. Their artifacts are in the runtime campaign directory: `final-selection.json`, `identical-policy-control-seat-audit.json`, `latest-vs-previous-best-seat-audit.json`, and `latest-vs-best-random-heroes.json`. The instrumented evaluator is `Tools/TrainingPreflight/seat_audit_evaluation.py`; the frozen training evaluator was not modified.
