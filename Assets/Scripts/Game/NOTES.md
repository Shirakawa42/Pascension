# Presentation layer (M5+M6) — implementation notes

Written blind against documented Unity 6 APIs (no compile available in this session).
Everything renders from `ClientSnapshot` + filtered `GameEvent`s; every intent is a
`PlayerAction` picked from `PendingSnap.LegalActions`. Zero rules logic in this layer.

## Deviation from "new files only" (deliberate, required to compile)

- **`Pascension.Game.asmdef`: added `"UnityEngine.UI"` to `references`.** uGUI is an
  asmdef-based package in Unity 6 (`UnityEngine.UI.asmdef` inside com.unity.ugui); custom
  assemblies do NOT get it implicitly, and every view here uses `Image`/`Button`/layout
  groups. Without this single line nothing in Assets/Scripts/Game compiles. No other
  existing file was modified.

## Architecture decisions

- **No LitMotion.** The manifest lists `com.annulusgames.lit-motion`, but at the time of
  writing it is **not resolved** (absent from `Packages/packages-lock.json` and
  `Library/PackageCache`), as is `com.unity.netcode.gameobjects`. Name-based asmdef
  references to missing assemblies are skipped, but code using the API would not compile.
  Tweens are hand-rolled coroutines in `Game/Presentation/Tween.cs` (the brief's "safe
  option"). Nothing in this layer touches NGO either.
- **Editor-only scene construction lives in the Game assembly**
  (`Game/EditorSupport/SceneConstruction.cs`, wrapped in `#if UNITY_EDITOR`), because
  `Pascension.Editor.asmdef` does not reference uGUI/TMP/InputSystem and I was not
  allowed to edit it. `Editor/SceneBuilder/SceneBuilder.cs` is a thin `[MenuItem]`
  wrapper. Menu path: **Pascension/Setup/Build All Scenes**.
- **No prefabs.** `CardViewFactory` + `UiFactory` build all dynamic UI in code from
  `UiTheme` (builtin UISprite/Knob/Background sprites + TMP default font + CardArtIndex).
  SceneBuilder creates the static hierarchy/anchors and wires all public fields;
  views construct their inner content in `Init(...)` called from `GameScreen.Bind`.
  `MainMenu` builds its panels at runtime in `Start()` (so the hero picker always matches
  registered content).
- **Snapshot-after-animation:** event batches queue in `PresentationQueue`; when it
  drains, `GameScreen.RefreshAll()` re-renders every view from the latest snapshot.
  Click anywhere fast-forwards the current batch (`PresentationQueue.Wait`).

## Known gaps / uncertainties (ranked)

1. **TMP essentials import is two-step.** First run of Build All Scenes imports the TMP
   Essential Resources package and asks you to run the menu item again
   (`AssetDatabase.ImportPackage` is not reliably synchronous).
2. **Monster HP shown is base HP** (`CardDefinition.MonsterHp`) minus nothing; the
   snapshot does not carry continuous-modifier-adjusted HP (e.g. Nyx's Hex −2). Marked
   damage IS shown as the red counter. If effective HP should display, `CardSnap` needs
   an `EffectiveHp` field (engine-side change, not mine to make).
3. **Reachable-node highlight ignores move bonuses** (Wren's Pathfinder +1): highlights
   are drawn at `position + paidSteps`. The engine still moves you the bonus distance.
4. **Board bottom-edge nodes sit behind the hand fan**, so movement is also exposed as
   quick "+N" buttons next to END TURN (and clicking the boss moves you to step 50 when
   that's the only sensible action). Node clicks work for anything not covered by UI.
5. **Player name input**: the menu does not offer a name field (TMP_InputField built in
   code was judged too fiddly); `MatchSetup.PlayerName` defaults to "You".
6. **`TextWrappingModes.Normal`** (uGUI 2.0 TMP API) is used instead of the obsolete
   `enableWordWrapping`; verified against the package source in `Library/PackageCache`.
7. **Fixed 1920×1080 design space.** The board path uses constants (±912, ±494); at
   extreme aspect ratios edge nodes may clip (CanvasScaler match 0.5 mitigates).
8. **Decision options referring to hidden cards** (e.g. cards in the deck) render as
   label buttons, not card fronts — the snapshot can't see them; the engine's
   `DecisionOption.Label` is trusted instead.
9. **Auto-pass UX** fires only when full-control is OFF (host fast-pass already skips
   those priorities engine-side, so this is a belt-and-braces no-op in practice) and is
   guarded by `EventSeq` to be un-loopable.
10. **Multiplayer button** is present and disabled with the "via Lobby — see multiplayer
    notes" note for the net agent to wire later.
11. **Glyph coverage**: text sticks to ASCII plus `·` and `—` (present in the default
    LiberationSans SDF atlas). No emoji.
12. **CardArtIndex auto-population**: Build All Scenes adds entries for any
    `Assets/Art/Cards/*.png` / `Assets/Art/Heroes/*.png` whose id (file name) is missing,
    fixing the importer to Sprite where needed. It never overwrites an existing non-null
    entry — the art tool agent owns this asset; `Entry {id, sprite}` API kept verbatim.
13. **StackItemSnap order** assumed bottom→top (matches `GameStack.Items`); the panel
    renders it reversed so the top of stack is at the top.

## Manual test checklist (after Unity import)

1. Run **Pascension → Setup → Build All Scenes** (twice if it reports TMP import).
   Expect `Assets/Scenes/MainMenu.unity`, `Assets/Scenes/Game.unity`,
   `Assets/Art/CardArtIndex.asset` (with ~30 card entries), both scenes in Build Settings.
2. Open MainMenu, press Play: title screen shows; Multiplayer disabled; Settings sliders
   + full-control toggle persist across restarts (PlayerPrefs).
3. Solo Game → pick each hero card (gold outline follows), set 1–3 bots, Start.
4. Game scene: opening hands drawn (log lines), market 3×5 filled with pile counts,
   Advanced/Elite rows show "Lv N req." and greyed cards, board ring drawn with inns at
   10/20/30/40, boss card top-center with "20 HP", your sheet bottom-left, opponents top.
5. Your turn: playable cards glow-free but not greyed; click a card → it resolves via
   the stack; AP/damage crystals update after the queue drains.
6. Buy: gold-glowing market card click → moves to discard (counter increments).
7. Move: "+N" buttons and gold board nodes both submit; pawn animates node-to-node;
   click during animation fast-forwards.
8. Kill: with enough damage pool, monsters glow red; click → damage goes on the stack;
   a Protective Barrier bot response can deny it.
9. Response window: when a bot plays something and you hold an instant, banner + timer +
   PASS shows, instants pulse; timing out auto-passes (host).
10. Decisions: play a card with targets → target labels modal at left + blue glow on
    actual monsters/stack items + targeting arrow; clicking the real target works.
    Reach an inn → 3 big option buttons. Ordered decisions show the ^ / v reorder list.
11. Deck/Discard/Exile/Played buttons open the card-list modal (deck shows count only).
12. Hero active/ultimate buttons: locked note below L3/L9, "used this turn" state after
    use, AP-gated.
13. End: kill the boss on step 50 (or concede path via bots) → GameOverPanel with winner
    hero art → Back to menu loads MainMenu.
14. Full-control ON in settings: verify you now receive stack priorities where before
    the engine fast-passed you, and END TURN button reads PASS there.
