#!/usr/bin/env python3
"""Generate reviewed October patch art using the repository's ComfyUI workflow.

Run with Windows pythonw.exe when ComfyUI listens only on Windows localhost.
Progress and errors are persisted to the manifest; no console is required.
Only these four original images are generated. Existing user jobs are untouched.
"""
import argparse
import copy
import hashlib
import json
import struct
import time
import urllib.parse
import urllib.request
import uuid
from pathlib import Path

SUBJECTS = {
    "horizon_seeker": "a lone mystic pilgrim in white and gold ceremonial armor standing on a high ruined observatory, blue cloak flowing in the wind, gazing toward a luminous golden horizon over an otherworldly city, a small radiant crystal hovering above an open palm, hopeful expansive composition",
    "riftbreaker": "a spectral mercenary in jagged black armor smashing a dimensional barrier with a heavy violet energy blade, luminous purple cracks and glasslike reality fragments bursting outward, smoky wraith tendrils, aggressive dynamic pose, ruined science fantasy battlefield",
    "rift_scout": "an agile masked scout in sleek silver and turquoise armor leaping through a curling temporal fissure, cyan clockwork energy arcs and translucent afterimages trailing behind, short curved blade in hand, floating ancient ruins, dynamic diagonal composition",
    "dna": "a luminous iridescent double helix suspended inside an ancient futuristic crystal chamber, the spiral branching into two identical floating crystalline relics, flowing cyan violet and gold energy, intricate organic geometry, mysterious destiny and magical duplication, centered vertical composition",
}
PREFIX = "masterpiece, best quality, score_7, safe, year 2025, newest, highres, painterly, fantasy, detailed illustration, dramatic lighting, trading card game art"
NEGATIVE = "worst quality, low quality, score_1, score_2, score_3, blurry, jpeg artifacts, watermark, text, signature, artist name"


def fnv1a(value):
    seed = 2166136261
    for byte in value.encode("utf-8"):
        seed = ((seed ^ byte) * 16777619) & 0xffffffff
    return seed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--endpoint", default="http://127.0.0.1:8188")
    parser.add_argument("--manifest", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    template_path = root / "Assets/Scripts/Editor/ArtPipeline/anima_card_workflow_api.json"
    manifest_path = args.manifest or Path(__file__).with_name("balance-patch-art-2026-10-08.json")
    template_bytes = template_path.read_bytes()
    template = json.loads(template_bytes)
    manifest = json.loads(manifest_path.read_text("utf-8")) if manifest_path.exists() else {
        "patch": "2026-10-08-v5", "source": "Original art generated locally with ComfyUI and Anima",
        "workflow": str(template_path.relative_to(root)).replace("\\", "/"),
        "workflow_sha256": hashlib.sha256(template_bytes).hexdigest(),
        "width": 880, "height": 1232, "steps": template["7"]["inputs"]["steps"],
        "prefix": PREFIX, "negative": NEGATIVE, "cards": {},
    }
    assert manifest["workflow_sha256"] == hashlib.sha256(template_bytes).hexdigest(), "Workflow changed; use a new manifest."
    client_id = str(uuid.uuid4())

    def save():
        temporary = manifest_path.with_suffix(".json.tmp")
        temporary.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        temporary.replace(manifest_path)

    def request(route, body=None):
        data = None if body is None else json.dumps(body).encode("utf-8")
        req = urllib.request.Request(args.endpoint.rstrip("/") + route, data=data,
                                     headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=30) as response:
            return response.read()

    active = None
    try:
        json.loads(request("/system_stats"))
        queue = json.loads(request("/queue"))
        manifest["queue_at_start"] = {key: len(queue.get(key, [])) for key in ("queue_running", "queue_pending")}
        manifest["status"] = "running"
        save()
        for card_id, subject in SUBJECTS.items():
            output = root / "Assets/Art/Shards/Cards" / (card_id + ".png")
            row = manifest["cards"].setdefault(card_id, {
                "subject": subject, "seed": fnv1a(card_id), "salt": 0,
                "path": str(output.relative_to(root)).replace("\\", "/"), "status": "pending",
            })
            active = row
            assert row["subject"] == subject, "Prompt changed; use a new manifest."
            if row["status"] == "complete" and output.exists():
                assert hashlib.sha256(output.read_bytes()).hexdigest() == row["sha256"], "Generated image was changed."
                continue
            if output.exists():
                raise RuntimeError("Refusing to replace existing art: " + str(output))
            if "prompt_id" not in row:
                graph = copy.deepcopy(template)
                graph["4"]["inputs"]["text"] = PREFIX + ", " + subject
                graph["5"]["inputs"]["text"] = NEGATIVE
                graph["6"]["inputs"].update(width=880, height=1232)
                graph["7"]["inputs"]["seed"] = row["seed"]
                graph["9"]["inputs"]["filename_prefix"] = "pascension_balance_2026_10_08/" + card_id
                reply = json.loads(request("/prompt", {"prompt": graph, "client_id": client_id}))
                if "prompt_id" not in reply:
                    raise RuntimeError("ComfyUI rejected the workflow: " + json.dumps(reply))
                row["prompt_id"] = reply["prompt_id"]
                row["submitted_at_unix"] = time.time()
            row["status"] = "generating"
            save()
            deadline = time.monotonic() + 300
            while time.monotonic() < deadline:
                record = json.loads(request("/history/" + row["prompt_id"])).get(row["prompt_id"], {})
                if record.get("status", {}).get("status_str") == "error":
                    raise RuntimeError("Generation failed: " + json.dumps(record.get("status")))
                images = [image for node in record.get("outputs", {}).values() for image in node.get("images", [])]
                if images:
                    image = images[0]
                    image_bytes = request("/view?" + urllib.parse.urlencode(image))
                    if image_bytes[:8] != b"\x89PNG\r\n\x1a\n" or struct.unpack(">II", image_bytes[16:24]) != (880, 1232):
                        raise RuntimeError("Unexpected image format or dimensions")
                    output.write_bytes(image_bytes)
                    row.update(status="complete", sha256=hashlib.sha256(image_bytes).hexdigest(),
                               completed_at_unix=time.time(), comfy_output=image,
                               elapsed_seconds=round(time.time()-row["submitted_at_unix"], 2))
                    save()
                    break
                time.sleep(2)
            else:
                raise TimeoutError("Generation exceeded five minutes; job left intact for safe resumption.")
        manifest["status"] = "complete"
        save()
    except Exception as exc:
        manifest["status"] = "error"
        manifest["error"] = repr(exc)
        if active is not None:
            active["status"] = "error"
        save()
        raise


if __name__ == "__main__":
    main()
