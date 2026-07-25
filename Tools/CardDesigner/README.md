# SoI Card Designer

A **local browser tool for designing DLC cards** — it never touches the game.
Open `index.html` in any browser (double-click; no server, no build). Card art is
read straight from `Assets/Art/Shards/Cards/`, so keep the folder in place inside
the repo (moving it elsewhere shows black art boxes, everything else still works).

## What it does

- Shows the **full live pool** (all DLCs enabled, 130 entries incl. the 5 characters)
  as fully rendered cards: art, faction frame, cost gem, shield / defense badges,
  keyword-highlighted rules text with hover tooltips.
- **TappedOut-style browsing**: search, filters (status / set / faction / type /
  cost / keyword) and grouping (faction, type, set, cost, change status, keyword)
  with per-group card + copy counts.
- **Edit anything**: text, cost, copies, defense, shield, set, faction, type, art
  prompt, design notes. Modified cards show a live was→now diff. Remove / restore
  existing cards, add brand-new cards (black art), duplicate cards as variants.
- **Card types are a checklist** — tick any combination (a card can be several types
  at once), or type a **custom type** in the box. Types are stored as an array and
  kept in canonical order. Filtering and *Group by: Type* treat a multi-type card as
  belonging to each of its types; the AI brief flags multi-type cards for the
  implementer. A live "Card reads as" line shows the composed type line.
- **Champions & defense**: champion-ness is *derived* the way the engine does it
  (`ShardsCardDef.IsChampion`) — a **Champion** or **Ingeminex** is always a champion,
  and a **Relic** with **Defense > 0** is a Relic Champion (Praetorian-02). Ticking
  Champion/Ingeminex seeds a defense; the Defense field is enabled only for
  champion-capable types and greyed out otherwise.
- **Keywords panel**: every mechanic keyword with meaning + live usage count;
  add new keywords or reword existing ones — card texts re-highlight instantly.
- **Save session** downloads a readable JSON delta (`soi-design-*.json`) that the
  tool reopens later (Open…). Work in progress also autosaves to localStorage.
- **Export AI brief** downloads a Markdown spec of every change plus the repo
  implementation checklist — hand that file to an AI to implement the DLC.

## Files

| file | role |
|---|---|
| `index.html` | the tool (open this) |
| `app.js` | logic — pure `Core` + browser UI |
| `baseline.js` | GENERATED snapshot of the live pool — never hand-edit |
| `generate-baseline.mjs` | regenerates `baseline.js` from `Tools/ShardsData/cards-table.md` |
| `smoke-test.mjs` | `node Tools/CardDesigner/smoke-test.mjs` — core logic tests |

## After real card changes ship

```
cd Tools/EngineVerify && dotnet test --filter ExportShardsCardTable
node Tools/CardDesigner/generate-baseline.mjs
```

Old session files still open — the tool warns if the pool drifted under a saved
session instead of silently mis-applying it.
