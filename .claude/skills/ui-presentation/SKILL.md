---
name: ui-presentation
description: The Unity presentation layer shared by both game tables — runtime-built no-prefab UI, PresentationQueue/FlightLayer/CardShowcase/FloatingNumberLayer, HandView diff rendering and drag-to-play, the speculative fast-play pump, the 3-ring glow system, play log, decision modals, and how SoiGameScreen reuses the whole stack. Use before touching Assets/Scripts/Game (View/Presentation/UI/Soi) or debugging animations, hand behavior, layout, or UI regressions.
---

# UI & Presentation (Pascension.Game — shared by both tables)

Everything renders `ClientGameView`/snapshots + filtered `GameEvent`s; every intent is a `PlayerAction` from `PendingSnap.LegalActions`. **Zero rules logic in this layer.**

## Architecture
- **No prefabs.** `CardViewFactory` + `UiFactory` build all dynamic UI in code from `UiTheme` (builtin sprites + TMP default font + CardArtIndex). `SceneConstruction` (in the Game asmdef under `#if UNITY_EDITOR` — Pascension.Editor doesn't reference uGUI/TMP) creates the static hierarchy; `Editor/SceneBuilder` is a thin `[MenuItem]` wrapper (`Pascension/Setup/Build All Scenes`). `MainMenu` builds its panels at runtime in `Start()`.
- **View `Init()` must run at RUNTIME** (`GameScreen.Bind`), never in SceneConstruction — private refs don't survive scene serialization.
- Tweens are hand-rolled coroutines in `Game/Presentation/Tween.cs` (LitMotion is referenced in the asmdef but this layer doesn't depend on it).
- TMP essentials import is two-step: first Build All Scenes run imports the package and asks you to run the menu item again.
- Glyph budget: ASCII plus `·` and `—` (present in the default LiberationSans SDF atlas). No emoji.
- Decision options referring to hidden cards render as label buttons (`DecisionOption.Label` trusted); options referencing visible cards must carry DefId/InstanceId and render the CARD (rule pinned in shards-cards).
- `StackItemSnap` order is bottom→top (matches `GameStack.Items`); the stack panel renders reversed.

## Layout
- Vertical serpentine board (boss on top) on a dedicated right panel ("THE ROAD", `MapSidebar`, 252×1064 at anchor (1,0.5)); glow/table 1260 wide; discard/exile column, action bar and stack-panel rest X at −268. Hero passive text at (14,−164) 282×78 (clear of the DMG crystal column). 400×550 character sheet (left) with passives + tweened XP bar + icon crystals; StS pile corners (draw+played left, discard+exile right; all piles browsable alphabetically incl. opponents'); compact market (scale 0.5); play-history bar (last 10, hover preview); opponent sheets clickable → full detail modal. Board bottom-edge nodes sit behind the hand fan, so movement is also exposed as "+N" quick buttons.
- **16:9 lock**: canvas scaler = Expand; all game UI lives under a fixed 1920×1080 `UiRoot` with **RectMask2D** (clips parked panels on ultrawide). Battery-verified at 4:3/16:9/21:9 via ScreenSpaceCamera render trick (scratchpad `shot_wh.cs`). Runtime-created UI (StackArrows, preview) must parent to `GameScreen.UiRootRect`, NOT the canvas.
- **Hover preview is FIXED top-left** at (118,−12) anchor/pivot (0,1) — right of the history bar, never follows the card.

## Animation dispatch
- `GameScreen.PlayEvent` is the SINGLE animation dispatcher — pace ONLY via `Queue.Wait`/`FastForwarding` (`PresentationQueue`; click fast-forwards the batch). Event batches queue; hand+stats+piles render LIVE per snapshot, board zones re-render on drain; responses/decisions wait for drain.
- Every play showcased center-screen (`CardShowcase`: player-color glow, click-to-skip); zone-flight proxies via `FlightLayer` (draw/buy/discard/exile/reshuffle/refill); floating +AP/+DMG/+XP and −N numbers with inline TMP icon sprites (`FloatingNumberLayer`); `GlowBurstLayer` UI particles; attack impact (punch/flash/directional burst); stack-target arrows; live monster HP (green buffed/red damaged) and viewer-effective costs.
- Icons: `Pascension/Setup/Build Icon Sprite Asset` (TMP m_Version reflection fix); AP icon = raw render + luminance-keyed alpha (rembg soft-mask pattern).

## ⚠ Interaction invariants (hard-won — do not regress)
- **Drag-to-play (StS style)**: `HandCardDrag` → `HandView` — release above `PlayLineY=390` (container-local) to play, or DOUBLE-CLICK (`OnPointerClick` clickCount≥2); below the line, horizontal drags REORDER the fan with a realtime gap (visual order in `_order`, client-side only). Single-click-to-play was removed. **The play line must stay above the hover/reorder band ~250+96** (a 330 line caused accidental plays — verified via UiLog).
- **Live hand pipeline**: `GameScreen.RefreshHandLive()` runs on EVERY snapshot (not drain-gated) — the hand re-fans instantly on plays (optimistic `RemoveCardOptimistic`) and play affordances work DURING animations. Drawn cards render hidden (`_pendingReveal`) and pop in one-by-one as each draw-flight lands (`FlightThenReveal`; `CoalescedDrawEvent.InstanceIds`).
- **HandView.Render is a DIFF, never a rebuild**: snapshots stream constantly during play chains, so Render keeps existing views alive (an active drag survives untouched — view, position, `_draggingId`), destroys only removed cards, creates only new ones, re-applies poses only when membership changed. Rebuilding here breaks fast-play (every snapshot killed the drag).
- **Persistent-view tween ratchet guard**: `Tween.Punch`/`Flash` capture start state as "base" — overlapping calls on PERSISTENT views (pile badges, market slots) ratchet forever (the badge-growing bug). Pattern: stop the previous coroutine and restore the true base before restarting (`PileWidget.Pulse`, `MarketView.PunchSlot/FlashSlot`). Views rebuilt each render don't need this.
- **Market refills reveal per-flight**: slots start inactive and the full `Market.Render` waits for drain, so `PlayRefill` calls `MarketView.RevealSlot(tier,slot,snap,viewerLevel)` as each flight lands (bind + activate + locked-tier grey + punch) — otherwise the whole market pops in at batch end.
- **Hand hover jitter fix**: an invisible per-card HoverPad below the card, raycast-enabled only while hovered — keeps the pointer inside the card's hierarchy after the hover lift (resting at the unlifted bottom edge can't enter/exit-oscillate). Pad disabled on drag start.
- **UiLog**: verbose `[UI:Area]` console logging (Hand/Drag/Play/Draw/Flight/Showcase/Queue/Live/Stage) — grep `read_console` to verify sequencing in automated tests. Toggle `UiLog.Enabled`.

## Speculative fast-play (client-side staging — Pascension)
- `GameScreen`: `_stagedPlays`/`PumpStagedPlays`/`RollbackStaged`. During your own main phase, plays are staged CLIENT-SIDE (opponents see nothing; "QUEUED n" widget above the played pile, click = take all back) and submitted one by one — a card's effect applies, and it is revealed, only after the previous play VALIDATED. If anyone responds, the queue rolls back to hand (toast + log). END TURN/PASS while staged = rollback, never a pass.
- **Pump invariants**: local submits cascade snapshots SYNCHRONOUSLY inside `SubmitAction`, so the pump (a) is re-entrancy-guarded via `_pumping`, (b) removes the queue head BEFORE submitting, (c) loops until the queue empties or others must act. **Rollback detection needs BOTH checks**: opponent item in the snapshot stack (pump) AND opponent `StackPushedEvent` in the event batch (`OnEvents`) — host fast-pass can resolve an entire counter interaction inside one submit, so the transient stack state never reaches a snapshot.
- **Auto-pass own spells**: `RefreshAll` auto-passes when the stack holds only YOUR items (Arena behavior; full-control keeps the window). The response window only shows for opponents' stack items. (An additional EventSeq-guarded auto-pass exists for the full-control-off path — belt and braces.)
- `RenderPiles` runs in `RefreshHandLive` (every snapshot) — pile badges track every change immediately.

## SoI table (`Assets/Scripts/Game/Soi/`)
- `SoiGameScreen` builds ALL its UI at runtime in Bind (LobbyScreen pattern) **on the shared presentation stack**: real CardViews via `CardView.ExternalFaceResolver` (`SoiCardFaces` plugs the Shards DB in; resolver fires only when CardDatabase misses — Pascension rendering untouched), the REAL `HandView` fed **synthetic CardSnaps** {DefId, InstanceId, EffectiveCost=-1} (same trick makes PileWidget/CardListModal work unchanged), 5 PileWidgets (draw/played/discard/banish/center-deck, browsable), FlightLayer zone flights (buy→discard, refill per-slot reveal+punch, cleanup, shuffle, merc→center-deck, banish, champion death), CardShowcase on every play/fast-play/destiny/relic, FloatingNumberLayer for every stat delta + damage + shield blocks, PresentationQueue pacing. Tap-to-exhaust: click portrait to Focus (optimistic SetTapped + burst + punch), champions/destinies likewise. `PresentationQueue.Coalesce` has a ShardsCardDrawnEvent branch (draw-5 coalescing).
- **`GameScreenBase` deliberately NOT extracted** — GameScreen untouched by the SoI work.
- `SoiDecisionModal`: list mode, `"soi.split"` stepper mode (cards at 0.6; assigned amount big gold + 0/−/+/MAX buttons ON the hero portrait in card-local units, face raycast off so buttons win; champions carry an outlined HP pill; row compresses on overflow; autosized button labels — 8px insets or tiny buttons wrap), and a reveal mode for Context `"soi.defiant"` (revealed card big with red mercenary glow + one immediate-submit button per option, no confirm/skip).
- `SoiOpponentDetailModal`: opponent panels (portrait + stat lines + champions 0.30 + destinies 0.22, compress-not-drop) clickable → full sheet, browsable discard/played, re-bound live each snapshot via `ShownIndex`. Decision card-grids carry a source-zone caption (`FindZoneName`).
- Menu: SoI solo setup panel — character grid expanding per enabled DLC (+ a RANDOM button; row spacing compresses when Rez pushes past 1200px), 3 DLC toggles, 1-3 bots. `SoiBootstrap` (solo via module; networked binds SessionProvider; shuffles seat order for a random first player — mirror of GameBootstrap).
- Gotchas: `MainMenu.ShowPanel` must list EVERY panel; `CardListModal` needs its own Container child rect (Init deactivates it — attaching to the root hides the whole UI).
- Champion click = explanatory toast (champions die only in the end-turn split — see shards-engine).
- Verified live via MCP-over-HTTP driving REAL UI events end-to-end incl. mid-animation screenshots.

## 3-ring glow system (SoI, `CardView`)
- **INNER glow** = killable red (wins) else condition-met faction color (hand via `HandView.GlowResolver` applied in ApplyPulse; river falls back to the red merc marker; ready champions/destinies/Ingeminex). Hints computed host-side — see shards-engine.
- **OUTER halo** (`CardView.OuterGlow`, second image) = affordable river slot, gold pulse (`OuterGlowPulseLoop`).
- **HOVER halo** is a third RING (`CardView.HoverGlow` + `SetHoverGlow`), stacking outside the others.
- `ApplyGlowLayout` (called from all three setters): each active ring steps one 5px inset out — 4/9/14 with all three lit; inner −4 alone, outer −9 with inner else −4. Halved sizes + real stacking are deliberate, on every screen.
- Outer rings apply LIVE on every snapshot (RefreshHandLive) and the **END TURN click clears them optimistically** before the submit (no network-round-trip linger; engine-side BuyableSlots gate pinned by test — see shards-engine).
- `SoiGameScreen.LateUpdate` re-resolves the hovered instance to its current view each frame (`_hoverGlowView` cleared when the target changes/dies — board views rebuild constantly).
- **Hover broadcast**: `GameNetBridge.SendCardHover`/`CardHoverChanged` (client→host→everyone; host relays local) — SoiGameScreen broadcasts the TURN player's hovered PUBLIC board card (hand never leaves the client), peers draw a pulsing player-color overlay re-resolved per frame in LateUpdate. The active player sees their own marker too (local echo in `BroadcastLocalHover`, works solo). Solo/despawned bridge = no-op.

## Play log (`PlayHistoryBar` — SoI-extended)
- Entries carry a dim note line + attachable flag; `Push(defId, player, note, attachable)` + `AttachAffected(player, defId)` (deduped, newest attachable entry of that player) → hovering an entry with affected cards pops "→ [cards]" at `AffectedAnchor` (SoI sets it right of the fixed preview).
- Entries: recruits ("recruited"), activations (exhaust events, attachable — e.g. the defiant reveal/banish attaches), champion destroyed (effect kills attach to the causing card — power kills detected by the immediately-preceding ChampionDamaged event), Ingeminex appears/attacks/defeated (playerIndex −1 → red stripe), player health LOSSES with old→new ("−4 · 29→25", portrait tile), shield blocks, eliminations, banishes (attach, standalone fallback). Free (cost-0) acquisitions attach to their cause BEFORE pushing their own entry. Draws never logged.
- **Full game log**: unbounded `_all` record (bar shows 10); ALL button (top of RECENT) opens a scrollable GAME LOG window (dimmer-dismiss + X), rows share `BuildRow`; `AttachAffected` searches `_all` so attaches land after entries scroll off.
- **Hover spotlight**: hovering a BAR entry raises a grey mask (raycast-transparent) over everything, then the bar above it; preview + affected panel raise themselves above both (order in `HistoryHover.OnPointerEnter`: spotlight FIRST, then RaiseHover). Container sibling index backed up/restored on exit; window rows suppress the spotlight (own dimmer).
- Banish pile = two badges (`PileWidget` badge2): gold total + green "yours" (Owner==viewer; defiant-banished center cards belong to the market).

## Known gaps
- Reachable-node highlight ignores move bonuses (Wren's Pathfinder +1) — engine still moves the bonus distance.
- Monster HP shown is base HP (no `EffectiveHp` in CardSnap — backlog, see project-map). Marked damage IS shown.
- Player name input: menu has no name field; `MatchSetup.PlayerName` defaults to "You".
- 4:3/21:9 screenshot pass for the SoI table not yet done (Pascension table verified).
