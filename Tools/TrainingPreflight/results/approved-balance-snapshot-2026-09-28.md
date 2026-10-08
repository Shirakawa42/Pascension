# Approved September 28 patch and follow-up snapshot

All nine changes below were explicitly approved, one at a time. No additional balance changes or training are included.

| Rule | Approved change |
|---|---|
| Terminal Crescents (Duel) | Gain 1 mastery instead of 2; damage formula and M20 threshold unchanged |
| Second seat in 1v1 | Open with 5 cards, 1 mastery and 1 crystal; crystal expires normally and is not granted again |
| Decima | At M5, first purchase each turn costs 2 less, floored at zero |
| Deadly Recruits (Duel) | Choose free fast-play OR free recruitment; limit 2, increasing to 4 at M20 |
| Swyft (Duel) | Defense 5 instead of 3 |
| Ferrata Guard (Duel) | Exhaust: 1 crystal plus 1 per Homodeus champion controlled |
| Torian Commandos | Gain 3 crystals instead of 2; power and shield unchanged |
| Le’shai Knight | Base power 4; Unify total remains 6 |
| Advanced Medicine | Heal 6 instead of 4; same positive even-cost condition |

## Verification

- 270 headless engine tests passed, including opening resources, one-time purchase discount, exclusive Deadly Recruits modes, and modified card behavior.
- 20 headless GPU smoke games completed, one per ordered distinct-hero pair. No censored or unfinished games. All 20 completed-game strategy records persisted; statistics provenance and outcomes passed final validation.
- Complete game detail records now flush after each completed game to avoid buffered records being lost during an early stop.
- French card/hero text updated for the approved changes.
- Repository callback descriptor manifest refreshed and verified. Its effect matrix equals the isolated snapshot exporter.

## Controlled evaluation

The accepted learned policy and search settings remain fixed. The seven approved card effect rows were refreshed; all 26 other model arrays are byte-for-byte unchanged. No optimizer updates occurred. A pre-existing, unrelated Oblivion Gatekeeper descriptor difference is deliberately preserved from the accepted policy, so it does not confound this balance experiment.

The simulator was built from the accepted source archive with only the approved rules, Decima’s planning cost constant, and completed-game trace flushing overlaid. Unpromoted search experiments are excluded.

Target: exactly 1,500 new games, 75 per ordered distinct-hero pair; each hero plays 300 games per seat. Identical frozen AI on both seats, GPU inference, at most eight CPU cores, no Unity or UI delay. Seed range starts at 9108000000000000000. Smoke games and earlier snapshots are excluded from the displayed cohort.

Monitor: http://localhost:8768/statistics

Artifacts: `/home/lva/.local/share/shards-training/2026-09-28/approved-balance-snapshot-1500`.

Original policy SHA-256: `c9d751fc2987199ab77ef4b38facc77cad54278822b14dd8493f672d36eb3102`.
Updated effect-buffer policy SHA-256: `9e67f97968eb36e5e66fa63aab90fb008c7438e812faf3c14fb9afd63492d157`.

The snapshot estimates performance under this fixed AI. It does not establish optimal play after changed rules; interpret acquisition win scores as associations rather than causal card strength.
