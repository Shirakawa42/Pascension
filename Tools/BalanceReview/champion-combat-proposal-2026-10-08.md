# Champion combat — October 8 implementation

The reviewed v5 design is now implemented in the Duel engine. Detailed current
rulings are in [rules-notes.md](../ShardsData/rules-notes.md#2026-10-08--reviewed-duel-balance-patch).
The complete pre-implementation discussion, official rulebook citations and open
alternatives are retained in the [v5 design archive](proposal_versions/2026-10-08-v5-champion-combat.md).

Implemented choices: exact-cost attacks during the turn; player damage at turn end;
duplicate guards remain attackable; Testudo uses positive live Shield on actual
plays and fixed +1 grants; DNA copies actual recruits, including relics and a
normally purchased Comet, directly to discard without repeating acquisition effects.
Destiny-taking and fast-play loans do not count as recruitment.

Focused engine tests are in `Assets/Tests/EngineTests/ShardsBalancePatchTests.cs`.
The former Testudo shield-sharing/reaction-budget design is superseded.
