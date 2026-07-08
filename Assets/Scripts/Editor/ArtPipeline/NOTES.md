# ComfyUI art pipeline — implementation notes (M9)

## IMPORTANT — compile dependency
`CardArtIndexBuilder.cs` references `Pascension.Game.CardArtIndex`, which the UI agent owns
and which **did not exist when this was written**. Until that class lands, the
`Pascension.Editor` assembly will not compile. The builder is written against exactly the
agreed API:

```csharp
namespace Pascension.Game
{
    public sealed class CardArtIndex : ScriptableObject
    {
        [Serializable] public class Entry { public string Id; public Sprite Sprite; }
        public List<Entry> Entries = new();
        public Sprite GetSprite(string id) { ... }
    }
}
```

Assumed PascalCase members (`Entries`, `Entry.Id`, `Entry.Sprite`) and a public
parameterless `Entry`. If the UI agent used different casing, only
`CardArtIndexBuilder.Rebuild()` needs touching.

## Uncertainties / decisions
1. **No Unity compile this session** (no Unity MCP): all editor code written against
   documented Unity 6 APIs (`AssetPostprocessor.OnPreprocessTexture`, `TextureImporter`,
   `EditorUtility.DisplayCancelableProgressBar`, `EditorWindow`). Not executed.
2. **Template kept verbatim** (`anima_card_workflow_api.json`, steps 35 vs the skill's
   stated 40) — brief said reuse, don't recreate. Only nodes 4 (positive), 5 (negative),
   6 (width/height), 7 (seed), 9 (filename_prefix) are patched.
3. **Seed** = 32-bit FNV-1a of the id (non-negative, fits KSampler's range) + re-roll salt.
   Salts persist per-machine in `EditorPrefs` under `Pascension.ArtSeedSalt.{id}` — they are
   not committed, so another machine re-rolls from salt 0.
4. **Blocking pipeline**: synchronous HttpClient + `Thread.Sleep(2s)` polling on the main
   thread inside the progress loop (accepted in the brief for an editor tool). Cancel is
   checked every poll via the cancelable progress bar; a best-effort `POST /interrupt` is
   sent on cancel. 5-minute per-image timeout.
5. **`/history/{id}` response shape** assumed standard ComfyUI:
   `{ "<prompt_id>": { "outputs": { "<node>": { "images": [{filename, subfolder, type}] } } } }`;
   the first image of the first node with images is downloaded via `/view`.
6. **"Compression: Automatic"** mapped to `TextureImporterCompression.Compressed`
   (inspector "Normal Quality", automatic format selection) — the modern importer enum has
   no literal "Automatic".
7. Menu items are `Pascension/Card Art Generator` and `Pascension/Rebuild Card Art Index`
   per the brief; the card-art skill says "Window > Pascension > Card Art Generator" —
   skill text should be updated to match (skill not modified: outside my write scope).
8. Cards with an empty `ArtPrompt` are listed but flagged `NO PROMPT` and skipped by
   batches / disabled for re-roll.
9. Boss art goes to `Assets/Art/Cards/the_gatekeeper.png` at 880x1232 (boss is a
   CardDatabase entry). Heroes render at 832x1216 into `Assets/Art/Heroes/`.
