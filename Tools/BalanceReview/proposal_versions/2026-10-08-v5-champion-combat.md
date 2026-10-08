# Champion combat proposal — 2026-10-08

Status: **v5 patch design following your v4 review, revision 64; no gameplay implementation**. The requested changes belong to the Duel balance patch. Existing non-Duel rules should not be silently changed while implementing it. The previous shield-protection proposal is preserved in the [v4 archive](proposal_versions/2026-10-08-v4-champion-combat.md); your Testudo redesign replaces that proposal completely.

## Verified official baseline

Stoneblade's [rulebook archive](https://stoneblade.com/pages/rules) links its [Saga Collection rulebook](https://cdn.shopify.com/s/files/1/0691/8805/9412/files/SOI_Saga-Rules-Web.pdf?v=1735833106). Pages 7–8 and 13 permit champion attacks during the play phase, interleaved with other actions. Players receive remaining power together at the end of the turn. Pages 11 and 13 require sufficient power to defeat a champion; damage does not persist across turns. They do **not explicitly resolve arbitrary partial attacks within one turn**. Page 13 allows hand shields to defend players, not champions. Revealed cards stay in hand; the FAQ permits reuse against different opponents. [Publisher rulebook, pp. 7–13 and 32](https://cdn.shopify.com/s/files/1/0691/8805/9412/files/SOI_Saga-Rules-Web.pdf?v=1735833106#page=7)

Zetta's pictured ability protects its owner and other champions from attacks. The FAQ allows Thorn Zealot's destroy effect to remove Li Hin despite attack immunity, and limits Praetorian-02's shield to its in-play zone. [Publisher rulebook, pp. 6, 32–33](https://cdn.shopify.com/s/files/1/0691/8805/9412/files/SOI_Saga-Rules-Web.pdf?v=1735833106#page=32)

The Testudo redesign is your requested custom rule. The detailed trigger timing, temporary-defense duration, duplicate-guard handling and DNA interactions below are **proposed local clarifications**, not official FAQ rulings or previously approved details.

## Existing local implementation and required change

The current engine rejects `ShardsAttackChampionAction` under the former end-turn-only decision. It includes champions in the final damage split; the current Testudo then defers their hits until its owner's shield reaction. Zetta can currently unlock other targets through a lethal assignment in that same split. Ordered mid-turn combat requires a different flow. [ShardsEngine.cs](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs:448)

Current Testudo is cost 4, defense 4, two copies, exhaust for 2 gems. Its existing static ability applies shield reduction to each champion individually. Your latest review replaces that ability with temporary champion defense gained by playing shield cards. The rejected per-champion shield-allowance design must not be implemented alongside the replacement. [ShardsDuelSet.cs](../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs:83)

The engine already stores `DamageThisTurn`, computes defense from live champion/destiny auras, and distinguishes attack permission from direct destruction. It does not currently run a general lethal-damage sweep when an aura source disappears. [Damage application](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs:2050), [defense and targeting](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs:990), [local rules discussion](../ShardsData/rules-notes.md:79)

## Recommended combat rule

Proposed patch wording:

> During your play phase, spend power equal to an enemy champion's remaining effective defense to attack and destroy it. Resolve its removal and all resulting effects before taking another action. You may attack champions between playing cards, recruiting cards and using abilities. Remaining power attacks players together at the end of your turn.

Remaining effective defense means printed defense plus current auras and temporary bonuses, minus any damage already marked this turn. A champion already at lethal marked damage is removed during state resolution rather than offered as a zero-cost attack. Insufficient power makes an attack illegal. The declaration rule follows the official sufficient-power requirement; arbitrary sublethal attacks are not added by this proposal.

Champion attacks need no hand-shield reaction under the redesigned Testudo. A single **Attack for N** action uses the required public amount, spends it once and resolves removal immediately. This avoids unnecessary overpayment choices. Hand and passive shields retain their ordinary player-defense role at turn end. Future effects that actually prevent champion attack damage would need their own explicit rules; this patch does not preemptively add such a system.

End turn leads only to player damage, with no second unordered champion split. If Zetta protects every remaining opponent, blocked power expires instead of forcing an impossible allocation. Destruction effects and any resulting triggers finish before the attacker regains control.

## Zetta and attack restrictions

Keep ordinary Zetta's card text and stats unchanged unless the duplicate-guard clarification below is adopted. Its ordinary sequencing belongs in the combat rule tile, not a separate unchanged-card entry.

- A living Zetta blocks attacks on its owner and their other champions. Zetta must actually leave play before the next protected target becomes legal. Merely selecting it does not unlock anything early.
- Testudo can have increased Zetta's defense during the defender's preceding turn. The attacker must pay that visible increased amount. Removing Testudo does not remove already granted bonuses under the proposed duration rule below.
- Direct destroy effects remain independent of power attacks: they bypass defense and Zetta's attack gate. Destroying Zetta directly unlocks other targets after its removal resolves; destroying another otherwise protected champion directly remains valid.
- Reevaluate Li Hin, Raidian and Drakonarius restrictions on every action. Gaining mastery can unlock Raidian; removing General Decurion can unlock Drakonarius. Removing Zetta does not bypass another champion's own attack immunity. [Target rules](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs:1006), [destroy effect](../../Assets/Scripts/Shards/Engine/ShardsEffects.cs:735)

**Duplicate Zetta remains an explicit design question.** The ordinary center deck contains one Zetta, but the newly submitted DNA destiny could create another. Literal mutual protection could make two Zettas immune to power attacks. Recommended house ruling: a guard protects its owner and non-guard champions, but guards remain attackable while another guard is present. Every remaining guard continues to protect those other targets. This still respects a guard's separate attack restrictions and gives no permission to attack the protected player early. It is a proposed exception for review; do not claim printed Zetta or the official FAQ already establishes it, and do not silently implement it. If adopted, the rule text and Zetta's description must both explain the exception. [Current quantity](../../Assets/Scripts/Shards/Content/ShardsBaseSet.cs:208)

## Testudo Vanguard: temporary defense from shield-card plays

The final v4 review requests **+1** champion health per shield card played. An earlier saved review said the shield's amount, but the submitted revision 64 replaces that with one. Use the local champion term **defense** for the same quantity.

Recommended card wording:

> Whenever you play a card with Shield, each champion you currently control gets +1 defense until the start of your next turn.
>
> Exhaust: gain 2 gems.

Keep cost 4, printed defense 4, Homodeus faction, champion type and two copies. The fixed +1 does not scale with printed shield magnitude or shield doubling. The exhaust ability is unchanged.

Recommended timing interpretations, to review before implementation:

1. Testudo must be in play when a qualifying card is actually played. Resolve the trigger once for that play, applying +1 to each champion its controller currently controls, including Testudo itself. Actual mercenary fast-plays count as plays. Merely recruiting a card, revealing a shield from hand to defend the player, or maintaining a passive shield source does not trigger it.
2. Copying or replaying a card's effects without an actual card play does not trigger it again. A physical card that leaves and is actually played again can produce another trigger. This follows the existing distinction between play effects and real play events.
3. Shield-card eligibility remains to be resolved: printed Shield only versus a positive shield value when played, including dynamic or externally granted shields such as Phasic Technology. State the chosen rule explicitly before implementation. In either case the grant is exactly +1. Proposed timing: evaluate eligibility once when the play is recorded; later mastery or shield changes do not alter an already resolved grant.
4. The grant belongs to the champions present when the trigger resolves. A champion entering play afterward receives no earlier grants. A champion entering play as the qualifying shield card may receive the grant if it is already in play when the trigger resolves. A champion entering play through recruitment is not itself a shield-card play unless the rules explicitly perform a play.
5. Granted defense persists until the start of that controller's next turn, even if the granting Testudo leaves play. Removing Testudo prevents its future triggers. Each affected champion loses its temporary grants if it leaves play; returning the same physical card does not restore them.
6. Two Testudos each trigger, giving +2 per qualifying play while both are in play. This is the ordinary independent-trigger reading of the proposed text, not an approved extra tuning. Confirm this stacking choice alongside the full card before implementation.
7. These are defense bonuses, not shield prevention. Ru Bo Vai's shield-ignore effect does not bypass them. Effects that destroy a champion directly still bypass its defense. Ordinary player shields and Praetorian-02's shield doubling otherwise keep their existing behavior.

Example: while Testudo and a base-defense-5 champion are in play, their controller plays two qualifying shield cards. They reach effective defenses 6 and 7 before other auras. The opponent then needs 6 power to destroy Testudo, and the other champion retains its +2 until its controller's next turn. A shield revealed only to defend the player produces no additional defense.

There are no per-champion shield budgets, shield reveals during champion attacks, or repeated-hit refresh rules in this replacement design.

## Defense auras and destruction order

Recommended local clarification: recompute effective defense after removing an aura source, changing its condition, granting temporary defense or expiring a grant. If any champion's existing marked damage is now lethal, remove all currently lethal champions as a group, then repeat until stable. Resolve simultaneous removals consistently rather than allowing collection order to determine survival.

For example, a champion with base defense 4 and Ferrata's +2 has 5 marked damage. Removing Ferrata lowers its defense to 4, so it dies immediately. This describes the general marked-damage rule; ordinary sufficient-power attacks in the proposed flow destroy their target immediately. A Testudo grant differs from Ferrata's continuous aura: it remains until its stated expiry after Testudo leaves. Allegiance counts owned cards across zones, so moving Ferrata to discard removes its aura source but does not itself subtract an owned Homodeus card from deck composition. [Ferrata aura](../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs:201)

## DNA: recruitment and duplication details to settle

Your saved draft at card-design revision 10 adds **DNA**, a neutral Destiny with one copy:

> Pay 4 gems, Exhaust: create an additional copy of the next card you recruit this turn. Put the copy in your discard pile.

This preserves the submitted activation cost, next-recruit timing and destination. It is not a catch-up card. A destiny goes into the separate destiny selection pool rather than the center deck; its editor cost of 0 is not a free shop purchase.

Recommended bookkeeping: pay the activation cost when exhausting; the pending copy expires at turn end if unused. The bonus creates a new physical instance rather than removing another copy from the shop or center deck. Its destination is explicitly discard, even when the original goes to hand or directly into play. Creating that bonus instance must not recursively count as another recruitment for DNA.

The draft's broad “next card you recruit” still needs explicit eligibility rulings:

- Ordinary paid recruitment and free recruitment effects are both recruitment in local rules. Fast-play or Warp loans, returning owned cards to hand and merely playing cards are different actions. Retaining a previously fast-played card requires checking the actual acquisition rule rather than assuming every loan became a recruitment.
- Relics are explicitly recruited in the local engine, though their acquisition uses a separate event and normal once-per-game entitlement. The wording does not exclude them. Decide whether DNA can duplicate a relic and whether that copies only the card, leaving the ordinary entitlement unchanged; do not silently narrow the text to center cards.
- Comet states that it can only be acquired through a normal gem purchase. DNA would create a second copy without another purchase, so these texts conflict. The patch needs an explicit choice between making Comet ineligible or allowing DNA as a named exception. Also specify whether recruiting an ineligible card consumes the pending effect or whether the effect waits for the next eligible recruit. Do not quietly turn “next card” into “next eligible card.” [Comet](../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs:163)
- Duplicating Zetta makes the guard interaction above a concrete concern for this patch. Duplicate other unique champions and champion relics also need coverage before the full package is accepted. Copying the card must not accidentally duplicate an acquisition reward or consume a destination-routing allowance twice.

These questions belong in DNA's proposal notes until resolved. They do not authorize replacement catch-up cards, new unrelated mechanics, or gameplay changes during this review update.

## UI, AI and acceptance checks for later implementation

Show **Attack for N** using public remaining effective defense and a visible power balance. Show Testudo's accumulated temporary defense and its expiry separately from printed defense. Recompute legal targets after every resolved action, including loss of Zetta and defense auras. Record qualifying shield-card plays, defense grants, expiry and destruction in public events. Unrevealed opponent hand shields do not influence champion attack costs or hints.

Required focused regressions before gameplay validation or new training:

- Attack, play a card, attack again; insufficient power rejected; required power spent once; champion attacks resolve before the next action; end turn offers player damage only.
- Zetta's destruction unlocks targets immediately; direct destruction bypasses attack protection; other attack restrictions remain; handle an end turn with no legal player target. Add duplicate-Zetta checks once its rule is decided.
- Testudo gives fixed +1 for Shield 2 and Shield 5 alike. Actual fast-play triggers, copied effects and defensive reveals do not. Check cards with dynamic or granted shield using the agreed eligibility definition.
- Grants affect the current champions, persist through Testudo removal, disappear when their recipient leaves play and expire at the owner's next turn. Check reentry, multiple Testudos and newly played shield champions against the agreed timing.
- Ru Bo Vai does not bypass temporary defense; direct destruction does. Removing a live aura or expiring a bonus recomputes defense and resolves any lethal marked damage consistently.
- Destruction and play triggers respect the single-pending-input pump. Save, reconnect, clone and search states preserve temporary defense, owner-specific expiry and pending DNA acquisition effects.
- Both installed AIs and the training host expose each legal champion attack, understand ordered removal and use the same public defense values. Existing end-turn champion-split encoders must change.
- DNA has separate coverage for activation cost, expiry, paid/free recruitment, original-to-hand routing, original-into-play routing and nonrecursive copying. Add Comet, relic, duplicate-guard and fast-play-retention cases after their eligibility rules are resolved.
- Deterministic replay, instance identity, card accounting with generated copies, player shield defense, end-turn cleanup, Infinity victories, hero/relic/DLC combinations and multiplayer remain covered. Replace tests that intentionally assert the former end-turn-only rule rather than preserve contradictory expectations.

No engine code changed and no engine tests were run for this note. The cited baseline and source inspection support the design; the requested rules and proposed clarifications remain to be implemented and validated after review.
