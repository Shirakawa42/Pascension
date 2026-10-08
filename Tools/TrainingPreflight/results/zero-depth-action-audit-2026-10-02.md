# Zero-depth action audit

The final C# action audit and integrated Host selftest pass. The audit submits
choices through the real engine; it does not train a policy or change card rules.
It preserves the engine's advertised actions and legal decision answers without
CPU strategy filters. The width-512 campaign can use the frozen Host below.

| Evidence | Passing coverage |
| --- | --- |
| Priority actions | 106 actual submissions; all 12 action families and all pages |
| Selection answers | 395 actual answers, including ordered choices and physical copies |
| Damage/split vectors | 91 integer vectors, including endpoints through 1,000 |
| Card effects | 3,108 cases across all 189 registered definitions |
| Explicit mode branches | 14 branches, including Volos and both Reactor/Deadly Recruits modes |
| Modal contexts | All 24 contexts that require a choice in two-seat games |
| Integrated Host selftest | 1,166 real states / 4 completed games, 14 reveal groups and 8 zone groups |
| Zone conservation oracle | 4,140 real rule states, included in the zone groups |

The [priority fixture](../../ZeroDepthTraining/Host/ActionSelfTest.cs#L123)
compares the complete engine-advertised action list against wrapper candidates,
checks every page, and submits each action from a fresh equivalent state. Duplicate
card definitions retain separate physical IDs. The
[decision-contract fixtures](../../ZeroDepthTraining/Host/ActionSelfTest.cs#L170)
enumerate every distinct ordered sequence over three enabled options for every
Min/Max pair from zero through three, both ordered flags, optional commit,
defaults and a disabled option. Invalid raw answers are rejected without changing
the engine state, pending request or event log. All 200 options of a real Grim
Tutor menu are separately submitted, including options on later pages.

The [ordering fixtures](../../ZeroDepthTraining/Host/ActionSelfTest.cs#L250)
submit all six top-three reorder permutations and all sixteen Scry selections
(including selection order and the empty choice). They compare the resulting
physical deck order with the expected kept top and ordered bottom cards.
[Hero fixtures](../../ZeroDepthTraining/Host/ActionSelfTest.cs#L268) cover all
five hero drafts, mastery thresholds, activation costs, once-per-turn use, the
passive Decima discount, and every affordable Volos mode at gem counts zero
through three. Card mode choices remain available when forced-answer automation
is enabled.

[Specific effect fixtures](../../ZeroDepthTraining/Host/ActionSelfTest.cs#L303)
check both Reactor modes and cleanup disposition, both Deadly Recruits modes,
taunt/shield outcomes and the defending player's actual response, keep-fast,
Maglev top routing, champion reset, Aion repeat and mandatory discard.
The [catalog fixtures](../../ZeroDepthTraining/Host/ActionSelfTest.cs#L403)
exercise each registered definition at mastery 0/5/10/15/20/30 with minimum and
maximum selection policies. Actual play, destiny/relic acquisition, champion
exhaust and monster reward paths are submitted through the wrapper and engine.
Starting states are constructed for coverage; an earned relic is moved from its
actual acquisition discard to a later hand fixture before its actual play.

The context inventory is checked against the encoder's registered contexts.
`soi.target` requires no target menu in this two-seat Host because there is only
one opponent; the Host rejects configurations with more than two seats. These
finite regressions cover every advertised action family and supported modal
interface, all catalog definitions and the stated permutations. They are not a
proof of every possible combination of an arbitrarily long game.

One wrapper defect was demonstrated before correction. A valid engine request
with Min=0/Max=0 and enabled shown options exposed selectable wrapper actions,
although the real engine rejected a selected answer. The
[saved failing run](../../ZeroDepthTraining/results/action-audit-max-zero-red.log)
shows the regression before the fix. The
[candidate loop](../../ZeroDepthTraining/Host/Adapter.cs#L191) now exposes picks
only while the selected count is below Max. The regression verifies raw engine
rejection and the corrected candidate mask. This is a generic decision boundary
fixture; no naturally occurring shipped-card failure is claimed. The only
production change in this action audit is that wrapper guard.

Evidence files:

- [Focused action audit](../../ZeroDepthTraining/results/action-audit-2026-10-02.json)
- [Final integrated Host selftest](../../ZeroDepthTraining/results/action-audit-host-selftest-2026-10-02.json)
- [Action test source](../../ZeroDepthTraining/Host/ActionSelfTest.cs)
- [Integrated selftest](../../ZeroDepthTraining/Host/SelfTest.cs#L91)
- [Prior observation and reveal audit](zero-depth-visibility-followup-2026-10-02.md)

Run with `/home/lva/.dotnet/dotnet
Tools/ZeroDepthTraining/Host/bin/Release/net8.0/ZeroDepthHost.dll action-selftest`
or replace the final argument with `selftest` for the integrated checks.

Final frozen `ZeroDepthHost.dll` SHA256:
`cf7c1a4084bf58acb1e09475199f1205b313456b7f92afca49bc64d1d67ac814`.
No training, policy update or strength comparison was performed by this C# audit.
