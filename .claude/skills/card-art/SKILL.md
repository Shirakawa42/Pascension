---
name: card-art
description: Generate Pascension card/hero/board art with ComfyUI + the Anima model — prompting standards, API pipeline, resolutions, and how to run the editor tool. Use when generating or regenerating any game art, or writing an ArtPrompt for a new card.
---

# Card Art Generation (ComfyUI + Anima)

## Setup facts
- ComfyUI: `F:\comfyuiauto\ComfyUI`, API at `http://127.0.0.1:8188` (no auth). Launch: `F:\comfyuiauto\Windows_Run_GPU_venv_p312_cu130.bat`. Health check: `GET /system_stats`.
- Model (all on disk): UNETLoader `anima-base-v1.0.safetensors` · CLIPLoader `qwen_3_06b_base.safetensors` (type `stable_diffusion`) · VAELoader `qwen_image_vae.safetensors`. Do NOT use `anima-preview3` (weights not on disk).
- Reference user workflow: `F:\comfyuiauto\ComfyUI\user\default\workflows\image_anima_base_rembg.json`. Our API-format template: `Assets/Scripts/Editor/ArtPipeline/anima_card_workflow_api.json`.
- Sampler: `er_sde`, scheduler `simple`, steps 40, CFG 4.5, denoise 1.0. Seed = stable hash of card id (+ re-roll salt) for reproducibility.

## Resolutions
| Asset | Size | Notes |
|---|---|---|
| Card art | **880×1232** | ≈63:88 MTG card ratio, full-bleed |
| Hero portraits | 832×1216 | portrait |
| Board/backgrounds | 1536×864 | 16:9 |
Keep 1-2 MP total; dimensions divisible by 16.

## Prompting Anima (hybrid: danbooru tags + natural language)
Structure: `<quality/meta/year tags>, <@artist style tags>, <count tag>, <subject description>, <composition/background tags>`.
- **Positive prefix (always)**: `masterpiece, best quality, score_7, safe, year 2025, newest, highres`
- **House style (all Pascension art)**: `painterly, fantasy, detailed illustration, dramatic lighting, trading card game art`
- **Negative (always)**: `worst quality, low quality, score_1, score_2, score_3, blurry, jpeg artifacts, watermark, text, signature, artist name`
- Tags lowercase with spaces (not underscores). Artist styles: `@artist name` near the start. Prompt weighting needs higher values than SDXL (e.g. `(chibi:2)`).
- **Never ask for text/letters/numbers in the image** (Anima is weak at text; the card frame renders all text).
- Natural-language part: 1-3 sentences describing the subject; name colors/materials/pose explicitly.
- `ArtPrompt` in a card definition contains ONLY the subject part; the tool prepends prefix + house style and appends the negative.
- Example ArtPrompt (Fireball): `a blazing sphere of fire hurtling forward, trailing embers and smoke, orange and crimson flames, dark cavern background, dynamic composition, motion blur`

## Pipeline (what the editor tool does per card)
1. `GET /system_stats` (once per batch; if it fails, tell the user to launch ComfyUI with the bat above).
2. Load template JSON, patch: positive text, negative text, width/height, seed, `SaveImage.filename_prefix = cardId`.
3. `POST /prompt` body `{"prompt": <api graph>, "client_id": "<guid>"}` → `{prompt_id}`.
4. Poll `GET /history/{prompt_id}` every 2 s (timeout 5 min) until `outputs` present.
5. `GET /view?filename=<f>&subfolder=<s>&type=output` → save bytes to `Assets/Art/Cards/{cardId}.png` → `AssetDatabase.ImportAsset`.
6. `CardArtIndexBuilder` rebuilds `CardArtIndex.asset` (cardId → Sprite).

Editor tool: **Window > Pascension > Card Art Generator** (`Assets/Scripts/Editor/ArtPipeline/ComfyUiArtWindow.cs`) — lists all cards from `CardDatabase`, flags missing art, batch-generates sequentially with progress + cancel, per-card re-roll (bumps seed salt).

Import settings (enforced by `CardArtPostprocessor` for `Assets/Art/Cards/`): Sprite (Single), sRGB on, mipmaps off, max size 1024, compression Automatic.

Docs: https://huggingface.co/circlestone-labs/Anima · https://docs.comfy.org/tutorials/image/anima/anima
