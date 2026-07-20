---
name: localization
description: Display-only French localization for both games — Loc.T keyed by English source strings, DecisionTitle regex templates, LocFrench UI dict, SoiFrenchCards (all 125 SoI defs, official IELLO terminology and faction renames), FR iconization rules. Engine/wire strings STAY English (goldens pinned). Use when adding or changing any user-facing string, adding SoI cards (French entries are mandatory), or touching the language toggle.
---

# Localization (French, display-only)

**Hard rule: engine/wire strings stay English** — localization happens at render time only; wire goldens are pinned and untouched.

## Architecture
- `Game/UI/Loc.cs`: `T(english)` keyed by the English source string · `DecisionTitle` regex-template table + faction/card-name post-pass · `OptionLabel` · `De()` elision · `CardName/CardText`.
- `Game/UI/LocFrench.cs`: the UI dict + decision-title patterns.
- Language toggle bottom-right of the main menu; persists via `SceneFlow.PrefLanguage`; switching reloads the menu scene.
- Version-gate wire strings are English ("Update required: …"); LobbyScreen localizes by prefix (see networking skill).

## SoI cards (`Game/Soi/SoiFrenchCards.cs`)
- ALL 125 defs, **official IELLO terminology**: maîtrise / cristaux / puissance / santé, la rivière, pioche commune, "Activez :", bannir, recruter, fast-play = **enrôler**; factions RENAMED Undergrowth→**Maquis**, Wraethe→**Spectra**; official card names incl. all 30 destinies.
- ⚠ **Mandatory per SoI card change**: any card added/renamed/retexted needs its French entry updated in the same change (mirrored in the shards-cards checklist). A new card without a FR name/text is a bug.
- `SoiCardFaces.Iconize` handles FR resource words + `Activez` (colon AND em-dash forms); threshold pills accept `M10:`, `M10 — ` (destiny gate) and `M20 Unify:`.

## Known gap
Pascension's in-table strings (EventText etc.) are NOT yet translated — menus/lobby/pause/game-over/SoI table are.
