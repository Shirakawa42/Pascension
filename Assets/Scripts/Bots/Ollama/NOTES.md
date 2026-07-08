# Ollama bot — implementation notes (M8)

## Verified
- All sources here are pure C# (no UnityEngine) and compile + pass under
  `Tools/EngineVerify` (`dotnet test` green, includes `Assets/Tests/EngineTests/OllamaTests.cs`).
- `AsyncBotSeat` (Assets/Scripts/Net/Host/AsyncBotSeat.cs) is also compiled by EngineVerify.

## Uncertainties / decisions
1. **Ollama `think` flag**: sent as a top-level boolean on `/api/chat` per current Ollama API.
   Non-reasoning models may return an HTTP error when `think:true` — that error path falls
   into `SnapshotFallbackPolicy`, so the seat never stalls. Could not be tested live
   (no network calls allowed in tests).
2. **`System.Net.Http.HttpClient` under Unity**: available in the .NET Standard 2.1 profile
   used by the player/editor; NOT compiled Unity-side this session (no Unity MCP). If the
   Bots asmdef ever sets an API compatibility level that drops it, OllamaClient breaks.
3. **Prompt constants**: `PromptBuilder.SystemPrompt` hardcodes rules facts (50-step board,
   20 HP boss, tier gates L4/L8, inns 10/20/30/40, draw 5). `ClientSnapshot` carries no
   `GameRules`, only `BossHp`. If GameRules knobs are tuned, update the system prompt text.
4. **Monster HP / buy cost shown are base values** (`CardDefinition.MonsterHp` / `.Cost`):
   continuous modifiers and buy-cost discounts are not in the snapshot. Marked damage IS
   shown, and the legal-action menu itself is always accurate (engine-generated amounts).
5. **Fallback priority policy** (per brief): first `BuyCard`/`AssignDamage`/`PlayCard` in the
   legal list, else Pass. It deliberately never Moves or uses hero abilities — safety net,
   not strategy. Playing a card may trigger follow-up decisions; those get default answers
   via the decision branch (mirror of `GameHost.DefaultActionFor`).
6. **Stale-submit safety is double-layered**: OllamaBot's per-request token stops a
   superseded task from submitting, and `GameHost.Tick` drops queued async submissions
   whose player no longer holds the pending input.
7. **Pre-existing asmdef gap (not mine, not touched)**: `Pascension.Engine.Tests.asmdef`
   does not reference `Pascension.Net`, yet the existing `SerializationTests.cs` uses
   `Pascension.Net`. Unity-side test compilation likely already fails for that file;
   EngineVerify compiles everything into one assembly so dotnet tests pass. `OllamaTests.cs`
   needs only Engine + Bots, so it adds no new requirement.
8. `OllamaBot` decision answers: unknown option ids or a count outside Min..Max → full
   fallback (defaults). Duplicated ids from the model are silently de-duplicated first.
