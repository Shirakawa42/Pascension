---
name: art-pipeline
description: Generate card/hero/board art for BOTH games (Pascension and Shards of Infinity) with ComfyUI + the Anima model — prompting standards, API pipeline, resolutions, deterministic seeds, the editor tools (Card Art Generator, Shards Art Import), CardArtIndex rules, and SoI art legal constraints. Use when generating or regenerating any game art, writing an ArtPrompt for a new card, or touching Assets/Scripts/Editor/ArtPipeline.
---

# Art Pipeline (ComfyUI + Anima, both games)

## Setup facts
- ComfyUI: `F:\comfyuiauto\ComfyUI`, API at `http://127.0.0.1:8188` (no auth). Launch: `F:\comfyuiauto\Windows_Run_GPU_venv_p312_cu130.bat`. Health check: `GET /system_stats`.
- Model (all on disk): UNETLoader `anima-base-v1.0.safetensors` · CLIPLoader `qwen_3_06b_base.safetensors` (type `stable_diffusion`) · VAELoader `qwen_image_vae.safetensors`. Do NOT use `anima-preview3` (weights not on disk).
- Reference user workflow: `F:\comfyuiauto\ComfyUI\user\default\workflows\image_anima_base_rembg.json`. Our API-format template: `Assets/Scripts/Editor/ArtPipeline/anima_card_workflow_api.json` — **the committed template is authoritative (steps 35)**; only nodes 4 (positive), 5 (negative), 6 (width/height), 7 (seed), 9 (filename_prefix) are patched at runtime.
- Sampler (from the template): `er_sde`, scheduler `simple`, CFG 4.5, denoise 1.0.
- **Seeds are deterministic**: 32-bit FNV-1a of the card id (non-negative, fits KSampler's range) + a re-roll salt. Salts persist per-machine in `EditorPrefs` under `Pascension.ArtSeedSalt.{id}` — not committed, so another machine re-rolls from salt 0.

## Resolutions
| Asset | Size | Notes |
|---|---|---|
| Card art (both games, incl. boss) | **880×1232** | ≈63:88 MTG card ratio, full-bleed |
| Hero/character portraits | 832×1216 | portrait (`Assets/Art/Heroes/`; SoI `soichar_*` at card size) |
| Board/backgrounds | 1536×864 | 16:9 |
Keep 1-2 MP total; dimensions divisible by 16.

## Prompting Anima (hybrid: danbooru tags + natural language)
Structure: `<quality/meta/year tags>, <@artist style tags>, <count tag>, <subject description>, <composition/background tags>`.
- **Positive prefix (always)**: `masterpiece, best quality, score_7, safe, year 2025, newest, highres`
- **House style (all game art)**: `painterly, fantasy, detailed illustration, dramatic lighting, trading card game art`
- **Negative (always)**: `worst quality, low quality, score_1, score_2, score_3, blurry, jpeg artifacts, watermark, text, signature, artist name`
- Tags lowercase with spaces (not underscores). Artist styles: `@artist name` near the start. Prompt weighting needs higher values than SDXL (e.g. `(chibi:2)`).
- **Never ask for text/letters/numbers in the image** (Anima is weak at text; the card frame renders all text).
- Natural-language part: 1-3 sentences describing the subject; name colors/materials/pose explicitly.
- `ArtPrompt` in a card definition contains ONLY the subject part; the tool prepends prefix + house style and appends the negative.
- Example ArtPrompt (Fireball): `a blazing sphere of fire hurtling forward, trailing embers and smoke, orange and crimson flames, dark cavern background, dynamic composition, motion blur`

## Pipeline (what the Card Art Generator does per card)
1. `GET /system_stats` (once per batch; if it fails, tell the user to launch ComfyUI with the bat above).
2. Load template JSON, patch: positive text, negative text, width/height, seed, `SaveImage.filename_prefix = cardId`.
3. `POST /prompt` body `{"prompt": <api graph>, "client_id": "<guid>"}` → `{prompt_id}`.
4. Poll `GET /history/{prompt_id}` every 2 s (blocking HttpClient; cancelable progress bar checked each poll; cancel sends best-effort `POST /interrupt`; 5-min per-image timeout). Response shape: `{ "<prompt_id>": { "outputs": { "<node>": { "images": [{filename, subfolder, type}] } } } }` — first image of the first node with images.
5. `GET /view?filename=<f>&subfolder=<s>&type=output` → save bytes to `Assets/Art/Cards/{cardId}.png` → `AssetDatabase.ImportAsset`.
6. `CardArtIndexBuilder` rebuilds `CardArtIndex.asset` (cardId → Sprite).

Editor tools (menu paths verified against `[MenuItem]` — there is no "Window >" prefix):
- **`Pascension/Card Art Generator`** (`Assets/Scripts/Editor/ArtPipeline/ComfyUiArtWindow.cs`) — lists all cards from `CardDatabase`, flags missing art, batch-generates sequentially with progress + cancel, per-card re-roll (bumps seed salt). Cards with an empty `ArtPrompt` are flagged `NO PROMPT` and skipped/disabled.
- **`Pascension/Rebuild Card Art Index`** — manual index rebuild.
- Boss art renders at 880×1232 into `Assets/Art/Cards/the_gatekeeper.png`; heroes at 832×1216 into `Assets/Art/Heroes/`.

Import settings (enforced by `CardArtPostprocessor` for `Assets/Art/Cards/`): Sprite (Single), sRGB on, mipmaps off, max size 1024, compression Automatic (= `TextureImporterCompression.Compressed` — the enum has no literal "Automatic").

## CardArtIndex rules
- `CardArtIndexBuilder.Rebuild` + `EnsureCardArtIndex` scan Pascension folders AND `Assets/Art/Shards/Cards/` into the shared index (SoI ids never collide with Pascension's).
- `Build All Scenes` auto-populates missing entries from `Assets/Art/{Cards,Heroes}/*.png` but **never overwrites an existing non-null entry**.

## Shards of Infinity art
- **Original art** is generated from each def's `ArtPrompt` via this same ComfyUI pipeline (scratchpad `generate_soi_art.py` pattern) → `Assets/Art/Shards/Cards/{id}.png`, 880×1232, seed = stable hash of id (deterministic regeneration). Prompts are exported by `dotnet test --filter ExportShardsCardTable` → `Tools/ShardsData/art-prompts.json` (incl. `soichar_*` character portraits). The generated ORIGINAL art is committed (2026-07-19).
- **⚠ LEGAL — official art import**: the `Pascension/Setup/Shards Art Import` window reads `Tools/soi_art_sources.json` ({id, source} url-or-path) → `Assets/Art/Shards/Cards/{id}.png` → rebuilds the shared CardArtIndex. **Imported OFFICIAL art is PERSONAL USE ONLY, never distribute — if the import window is ever used, re-add the `Assets/Art/Shards/` gitignore before committing** (the folder currently holds only committed ORIGINAL Anima art). Fallback for missing ids: text-frame cards render fine, or generate original art from the def's ArtPrompt.

## Pascension art manifest
`Tools/art_manifest.json` is exported via `dotnet test --filter ExportArtManifest` (id/kind/prompt/size rows) — generation can also be driven by an external API loop over it (scratchpad `generate_art.ps1` pattern) instead of the editor window.

Docs: https://huggingface.co/circlestone-labs/Anima · https://docs.comfy.org/tutorials/image/anima/anima
