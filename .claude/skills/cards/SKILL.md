---
name: cards
description: The Pascension card registry — every card's exact rules text, cost, tier, copies, art prompt, and implementation status. Use when adding, changing, removing, or looking up any card, hero, or the boss. MUST be updated on every card change.
---

# Pascension Card Registry

⚠️ **This file is the single source of truth for the card list. Whenever a card is added, changed, or removed — in code, in balance numbers, or in rules text — update this file in the same change.** Status legend: `planned` → `coded` → `tested` → `art` (has generated art).

## How to add a card (checklist)
1. If the effect can't be expressed with existing effects in `Assets/Scripts/Engine/Effects/Common/`, add a new `IEffect` class in `Assets/Scripts/Content/Effects/` (~30 lines, iterator style; see rules-engine skill).
2. Add one builder entry in the matching set file: `Assets/Scripts/Content/Sets/{DefaultCards,BasicCards,AdvancedCards,EliteCards,Boss}.cs` — include `.Art("…")` prompt (see card-art skill for prompt style).
3. Add/extend a test in `Assets/Tests/EngineTests/` exercising the new effect.
4. Run the art tool (Window > Pascension > Card Art Generator) — it detects the missing PNG by card id.
5. **Update this file** (row + status).

Card ids are `snake_case` of the English name (e.g. `random_bullshit_go`). Art files: `Assets/Art/Cards/{id}.png`.

## Default deck (10 cards per player — not buyable)
| Id | Name | Type | Text | Status |
|---|---|---|---|---|
| move | Move | Action | +1 AP | planned |
| redbull | Redbull | Action | Lvl 1-4: +2 AP · lvl 5-9: +3 AP · lvl 10: +5 AP | planned |
| fire_bolt | Fire bolt | Action | +1 damage | planned |
| pyroblast | Pyroblast | Action | Lvl 1-4: +2 dmg · lvl 5-9: +3 dmg · lvl 10: +5 dmg | planned |

Deck composition: 7× move, 1× redbull, 1× fire_bolt, 1× pyroblast.

## Basic pile (36 cards, no level requirement)
| Id | Name | Type | Cost | Copies | Text | Status |
|---|---|---|---|---|---|---|
| run | Run | Action | 2 | 4 | +2 AP | planned |
| fireball | Fireball | Action | 2 | 4 | +2 damage | planned |
| clarity | Clarity | Action | 3 | 4 | Draw 2 cards | planned |
| ban | Ban | Action | 3 | 3 | Ethereal. Exile a card from your hand | planned |
| protective_barrier | Protective Barrier | Instant | 3 | 3 | Target monster gets +3 HP until end of turn. Draw a card | planned |
| short_sword | Short Sword | Equipment (weapon) | 4 | 3 | Tap: +1 damage | planned |
| cloth_armor | Cloth Armor | Equipment (armor) | 4 | 3 | Tap: +1 AP | planned |
| stone_totem | Stone Totem | Equipment (trinket) | 3 | 3 | Killing a monster grants +1 XP | planned |
| goblin | Goblin | Monster (HP 2) | — | 5 | Reward: 1 XP | planned |
| hobgoblin | Hobgoblin | Monster (HP 4) | — | 4 | Reward: 2 XP, draw a card | planned |

## Advanced pile (48 cards, requires level ≥4)
| Id | Name | Type | Cost | Copies | Text | Status |
|---|---|---|---|---|---|---|
| counterspell | Counterspell | Instant | 5 | 3 | Counter target spell | planned |
| random_bullshit_go | Random Bullshit Go | Instant | 5 | 3 | Exile cards from the top of the advanced pile until you exile 2 instants not named Random Bullshit Go. You may cast them for free. Put the rest on the bottom of the pile in any order | planned |
| sprint | Sprint | Action | 5 | 3 | +4 AP | planned |
| meteor | Meteor | Action | 5 | 3 | +4 damage | planned |
| adrenaline_shot | Adrenaline Shot | Action | 6 | 3 | +2 AP. Draw a card | planned |
| fireworks | Fireworks | Action | 6 | 3 | +3 damage. Draw a card | planned |
| reflexes | Reflexes | Instant | 5 | 3 | Draw 2 cards | planned |
| sabotage | Sabotage | Instant | 6 | 3 | Target player discards a random card | planned |
| longsword | Longsword | Equipment (weapon) | 6 | 3 | Tap: +2 damage | planned |
| tower_shield | Tower Shield | Equipment (armor) | 6 | 3 | Tap: +2 AP | planned |
| lucky_charm | Lucky Charm | Equipment (trinket) | 6 | 3 | At the start of your turn, gain 1 AP | planned |
| merchant_stall | Merchant Stall | Relic | 6 | 3 | The first card you buy each turn costs 1 less | planned |
| war_banner | War Banner | Relic | 7 | 3 | At the start of your main phase, gain 1 damage | planned |
| loot_goblin | Loot Goblin | Monster (HP 6) | — | 3 | Reward: 2 XP, draw 2 cards | planned |
| mimic | Mimic | Monster (HP 7) | — | 3 | Reward: 3 XP, put the top card of the advanced pile into your discard for free | planned |
| ogre | Ogre | Monster (HP 8) | — | 3 | Reward: 4 XP | planned |

## Elite pile (32 cards, requires level ≥8)
| Id | Name | Type | Cost | Copies | Text | Status |
|---|---|---|---|---|---|---|
| time_warp | Time Warp | Action | 12 | 2 | Take an extra turn after this one. Exile Time Warp | planned |
| firestorm | Firestorm | Action | 9 | 2 | +7 damage | planned |
| cataclysm | Cataclysm | Action | 8 | 2 | Exile all face-up market cards and refill each slot. Gain 1 XP per monster exiled this way | planned |
| divine_shield | Divine Shield | Instant | 8 | 2 | Target monster gets +8 HP until end of turn. Draw 2 cards | planned |
| mind_steal | Mind Steal | Instant | 9 | 2 | Counter target spell. If it was an instant, you may cast a copy of it for free | planned |
| blink | Blink | Instant | 8 | 2 | Return target relic or equipment to its owner's discard pile | planned |
| excalibur | Excalibur | Equipment (weapon) | 10 | 2 | Tap: +4 damage | planned |
| dragonscale_armor | Dragonscale Armor | Equipment (armor) | 10 | 2 | Tap: +3 AP | planned |
| philosophers_stone | Philosopher's Stone | Equipment (trinket) | 9 | 2 | Whenever you gain XP, gain 1 more | planned |
| portal_stone | Portal Stone | Equipment (trinket) | 10 | 2 | Tap: move 2 steps | planned |
| throne_of_ambition | Throne of Ambition | Relic | 10 | 2 | At the start of your turn, gain 1 XP | planned |
| travelers_map | Traveler's Map | Relic | 9 | 2 | At the start of your turn, move 1 step | planned |
| arcane_library | Arcane Library | Relic | 10 | 2 | At the end of your turn, draw 6 cards instead of 5 | planned |
| dragon | Dragon | Monster (HP 15) | — | 2 | Reward: 8 XP, draw 2 cards | planned |
| lich | Lich | Monster (HP 12) | — | 2 | Reward: 5 XP, exile up to 3 cards from your discard | planned |
| treasure_golem | Treasure Golem | Monster (HP 10) | — | 2 | Reward: 4 XP, gain 4 AP | planned |

## Boss
| Id | Name | HP | Rule | Status |
|---|---|---|---|---|
| the_gatekeeper | The Gatekeeper | 20 | On step 50. Attackable only while standing on step 50. Damage on it resets at end of turn. Killing it wins the game | planned |

## Heroes
| Id | Name | Archetype | L1 passive | L3 active | L6 upgrade | L9 ultimate | Status |
|---|---|---|---|---|---|---|---|
| ignis | Ignis the Pyromancer | damage | Kindle: first card each turn that gives you damage gives +1 more | Flame Lash (1 AP, 1/turn): +2 damage | Kindle gives +2 | Inferno (3 AP, 1/turn): +6 damage | planned |
| wren | Wren the Scout | movement | Pathfinder: your first move each turn goes 1 extra step | Dash (1 AP, 1/turn): move 2 steps | Trailblazer: choose 2 inn rewards | Blitz (4 AP, 1/turn): move 4 steps, draw a card | planned |
| cornelius | Cornelius the Merchant | economy | Haggle: first card you buy each turn costs 1 less | Trade (0 AP, 1/turn): discard a card: gain 2 AP | Haggle on first 2 buys | Express Delivery (2 AP, 1/turn): next buy this turn goes to your hand | planned |
| nyx | Nyx the Trickster | control | Up the Sleeve: at end of turn keep 1 unplayed card (draw up to 5 total) | Hex (2 AP, 1/turn): target monster −2 HP until EOT | keep up to 2 cards | Master Plan (3 AP, 1/turn): draw 3 cards | planned |

## XP curve
XP to advance: L1→2: 2, →3: 2, →4: 3, →5: 3, →6: 4, →7: 4, →8: 5, →9: 5, →10: 6 (cumulative 34). Tunable in `GameRules`.
