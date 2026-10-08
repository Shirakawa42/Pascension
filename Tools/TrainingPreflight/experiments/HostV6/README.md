# V6: hero decisions and revealed center-deck knowledge

This is a separate host. V5 binaries, source identities, checkpoints and game rules remain intact.

Ko's AI first previews the existing optional hand/discard banish menu, without submitting the real ability. Selecting a card commits two actual legal engine actions atomically: activate Sacrifice, then banish the selected card. Cancelling submits neither action and costs no health. Empty target sets do not offer a preview. A declined target set cannot be reopened until its cards/zones change or a new turn starts, preventing a zero-cost infinite loop. The policy may still pay 3 health for a useful banish; there is no blanket health-loss reward penalty.

The preview uses the existing banish context and card features, plus an explicit preview flag. Wrapper/PPO actions remain the sampled preview and selection decisions. Real engine-submission counters count both committed actions. C# acquisition statistics observe actual engine events, not preview intentions. Other banish effects retain their original rules.

Each seat stores up to four legitimately revealed top center-deck cards, captured only from its own Scry/reorder decision options. The encoder exposes their IDs, costs and factions, five hero indicators, the preview flag, and the hero's health/gem costs in twenty previously unused padding slots. Schema is `shards-observation-v4`; dimensions and card ordering stay unchanged. Migration zeros the new first-layer columns in all retained policies, verifies the corresponding Adam moments are already zero, and preserves optimizer, RNG, archives, counters and charged time.

Public row-refill and monster-reveal events consume known top cards. Known bottom returns preserve the remaining top knowledge. Private opposing deck manipulation, Doom Gate center-deck shuffles, broad reveal events and unaccounted deck-size changes conservatively invalidate it. The ledger never reads center-deck entries or opposing hidden zones. It intentionally does not reconstruct every form of player knowledge, e.g. the bottom deck order or every Shard Defiant/Longshot sequence.

`SHARDS_HERO_FIX_SEATS` exists solely for frozen comparisons. The Python training factory always forces mask 3 (both seats), overriding inherited environment values. Evaluation can explicitly select one seat or mask 0. Mask 0 preserves the V5 action/observation behavior; new feature slots are zero. A 600-step, eight-lane differential check against V5 matched every observation, candidate, mask, seat, terminal flag and reward exactly.

Checks:

```sh
/home/lva/.dotnet/dotnet bin/Release/net8.0/TrainingHostV6.dll v6selftest
/home/lva/.dotnet/dotnet bin/Release/net8.0/TrainingHostV6.dll selftest
/home/lva/.dotnet/dotnet bin/Release/net8.0/TrainingHostV6.dll hero-selftest
```

The separate `probe_v6.py` uses a frozen V5 checkpoint, prepares an in-memory migration, and compares V6 behavior with the legacy control on balanced seat-swapped hero assignments. `smoke_hero_fixes.py` requires an inactive campaign ledger and exclusive GPU scheduling; it runs a real 256-game collector/behavior-likelihood check without optimizer updates or budget mutation. Actual training resumes only from a published strict V6 identity.
