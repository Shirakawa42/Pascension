# V10 Reactor cleanup correction

A Reactor Drone (Duel) temporarily obtained through Deadly Recruits at Mastery 20 could choose its three-gem mode, then return to the center deck at end of turn with `BanishAtCleanup` still set. This contradicted the mode's explicit end-of-turn banish and could carry the stale flag to a later owner. The original cleanup checked `FastPlayed` before `BanishAtCleanup`.

The V10-only engine clone checks the explicit banish first and clears both temporary flags when banishing. Ordinary temporary two-gem play still returns to the bottom of the center deck. Ordinary kept or hand-played cards preserve their prior cleanup behavior. The existing banish event, owner retention and per-turn banish counter increment remain; the normal subsequent turn reset clears that counter. [Corrected cleanup](../experiments/HostV10/Engine/ShardsEngine.cs#L1477)

This is a narrowly scoped rules bug fix, not balance tuning. The canonical engine and frozen older training engines were not edited for this correction. The complete diff against the canonical engine consists of cleanup branch precedence, clearing `FastPlayed` in the banish branch, and explanatory comments.

## Regression evidence

The final coordinated HostV10 build passed **82 Reactor cleanup checks**. Six paths start from deterministic constructed positions and then use accepted engine actions: Deadly Recruits at Mastery 20, followed by keep or decline, followed by two or three gems, plus ordinary hand-play controls for both modes. No fixture manually supplies the temporary or banish flags. Assertions cover gem gain, flags set by effects, final banished/center/permanent membership, exactly the expected public event, owner/zone, turn advance, and the new actor's encoded public collection and banished histogram. [Regression source](../experiments/HostV10/ReactorCleanupV10SelfTest.cs)

On the same final build, the information regression passed **2422 checks** and effect descriptor tests passed **12 checks**, as recorded by the coordinated build/test owner. Executable sources were frozen after these results.

## Comparison contract

The earlier V9/V10 observation-prefix trajectory probe preceded this rule correction. It establishes unchanged feature semantics for the representation-only extension, not universal trajectory equality between the old engine and this corrected fork. Compare old and new policy weights on the **same corrected V10 engine**. The runtime/migration identity must explicitly distinguish this fork; a model migration must not silently pretend the rules are identical.
