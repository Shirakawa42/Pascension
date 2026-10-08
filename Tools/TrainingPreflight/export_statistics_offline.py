"""Package a completed statistics cohort for opening directly from file://.

Requires Pillow only while exporting; the recipient needs only a browser.
Usage: python export_statistics_offline.py --snapshot <directory> --output <zip>
"""
from pathlib import Path
import argparse
import base64
import hashlib
import io
import json
import re
import zipfile
from PIL import Image
from statistics_assets import CardArtwork

HERE = Path(__file__).resolve().parent


def encoded(value):
    return json.dumps(value, ensure_ascii=False, allow_nan=False, separators=(",", ":"))


def safe_script(value):
    return encoded(value).replace("<", "\\u003c").replace("\u2028", "\\u2028").replace("\u2029", "\\u2029")


def portable(value):
    if isinstance(value, dict):
        return {k: portable(v) for k, v in value.items()}
    if isinstance(value, list):
        return [portable(v) for v in value]
    if isinstance(value, str) and value.startswith(("/home/", "/mnt/", "C:\\", "F:\\", "E:\\")):
        return value.replace("\\", "/").rstrip("/").rsplit("/", 1)[-1]
    return value


def replace_once(text, old, new):
    if text.count(old) != 1:
        raise ValueError("Dashboard export anchor changed: " + old[:80])
    return text.replace(old, new, 1)


def export(snapshot_dir, output):
    source = snapshot_dir / "statistics.json"
    original = source.read_bytes()
    stats = portable(json.loads(original))
    games = json.loads((snapshot_dir / "games.json").read_text())
    count = stats["totals"]["resolved_games"]
    if stats["evaluation_progress"]["state"] != "completed" or count != stats["evaluation_progress"]["target"]:
        raise ValueError("Only complete snapshots can be shared")
    if len(games["rows"]) != count or games["games"] != count or stats["totals"]["censored_games"]:
        raise ValueError("Game rows do not match the completed snapshot")
    strategy = snapshot_dir / "raw/forced-random/strategy-games.jsonl"
    strategy_rows = [json.loads(line) for line in strategy.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    if len(strategy_rows) != count:
        raise ValueError("Strategy records do not match the completed snapshot")

    art = CardArtwork(metadata=stats["card_catalog"])
    images, cache = {}, {}
    for row in stats["card_catalog"]["cards"] + stats["card_catalog"]["heroes"]:
        path = art.path(row["id"])
        if path is None:
            raise ValueError("Missing card artwork: " + row["id"])
        if path not in cache:
            with Image.open(path) as source_image:
                image = source_image.convert("RGB")
                image.thumbnail((700, 1000), Image.Resampling.LANCZOS)
                buffer = io.BytesIO()
                image.save(buffer, format="WEBP", quality=86, method=6)
                cache[path] = "data:image/webp;base64," + base64.b64encode(buffer.getvalue()).decode("ascii")
        images[row["id"]] = cache[path]

    # Preserve all metrics. Only replace server image URLs with portable identifiers.
    def local_images(value):
        if isinstance(value, dict):
            return {k: ("embedded:" + v.split("id=", 1)[1] if k == "image_url" and isinstance(v, str) and v.startswith("/api/card-image?id=") else local_images(v)) for k, v in value.items()}
        if isinstance(value, list):
            return [local_images(v) for v in value]
        return value
    stats = local_images(stats)
    html = (HERE / "statistics_dashboard.html").read_text()
    html = replace_once(html, 'href="/statistics"', 'href="index.html"')
    html = replace_once(html, '<a href="/">Training monitor ↗</a>', '<a href="README.txt">About this snapshot</a>')
    html = replace_once(html, '<span class="status" id="connection">Connecting</span>', '<span class="status" id="connection">Offline snapshot</span>')
    html = replace_once(html, '<title>Shards of Infinity · Balance observatory</title>', f'<title>Shards of Infinity · {count:,} games · Offline statistics</title>')
    html = replace_once(html, '<script>\n', '<script id="offline-data" type="application/json">' + safe_script(stats) + '</script>\n<script id="offline-images" type="application/json">' + safe_script(images) + '</script>\n<script>\n')
    html = replace_once(html, "'use strict';", """'use strict';
const offlineSnapshot=JSON.parse(document.getElementById('offline-data').textContent);
const offlineArtwork=JSON.parse(document.getElementById('offline-images').textContent);
const rawSnapshotUrl=URL.createObjectURL(new Blob([JSON.stringify(offlineSnapshot,null,2)],{type:'application/json'}));
""")
    start = html.index('function imageUrl(row)')
    end = html.index('\nfunction imageNode', start)
    html = html[:start] + """function imageUrl(row){const id=String(row.id??row.hero_id??row.card_id??'').split(':')[0];return offlineArtwork[id]||null;}""" + html[end:]
    html = replace_once(html, "pool()?' · changing policies':''", "currentSource.startsWith('training_')?' · changing policies':''")
    html = replace_once(html, "$('raw').href='/api/artifact?'+new URLSearchParams({name:rawNames[currentSource]});", "$('raw').href=rawSnapshotUrl;$('raw').download='statistics.json';")
    html = replace_once(html, "'Refreshed '+new Date((data.wall||Date.now()/1000)*1000).toLocaleTimeString()+' · every 5 seconds'", "'Offline snapshot · '+new Date(snapshot.updated_wall*1000).toLocaleString()")
    start = html.index('let fetching=false,lastSignature=null;')
    end = html.index("\n$('source').onchange", start)
    html = html[:start] + "function refresh(){render({wall:offlineSnapshot.updated_wall,balance_statistics_sources:{final:offlineSnapshot},catalog:offlineSnapshot.card_catalog});}" + html[end:]
    html = replace_once(html, 'refresh();setInterval(refresh,5000);', 'refresh();')
    html = html.replace('Updates every 5 seconds', 'Offline snapshot')
    if 'fetch(' in html or 'setInterval(' in html or "'/api/" in html:
        raise ValueError('A live-server dependency remains')

    # Deduplicate art aliases (Duel replacements share their original artwork).
    unique = list(dict.fromkeys(images.values()))
    mapping = {key: unique.index(value) for key, value in images.items()}
    html = replace_once(html, safe_script(images), safe_script({'images': unique, 'ids': mapping}))
    html = replace_once(html, "const offlineArtwork=JSON.parse(document.getElementById('offline-images').textContent);", "const artBundle=JSON.parse(document.getElementById('offline-images').textContent);\nconst offlineArtwork=Object.fromEntries(Object.entries(artBundle.ids).map(([id,index])=>[id,artBundle.images[index]]));")
    info = {'schema': 'shards-offline-statistics-v1', 'games': count, 'snapshot_id': stats['snapshot_id'],
            'source_statistics_sha256': hashlib.sha256(original).hexdigest(),
            'policy_sha256': stats['scope']['policy_sha256'], 'art_definitions': len(images), 'unique_images': len(unique),
            'training_updates': 0, 'description': 'Completed evaluation only; no training games or diagnostic replays.'}
    readme = f"""SHARDS OF INFINITY — {count:,} GAME STATISTICS

1. Extract this ZIP (Windows: right-click > Extract All).
2. Open index.html in Chrome, Edge, Firefox or Safari.

No installation, server or internet connection is needed. The HTML embeds all
statistics and card images; it can also be shared by itself. Keep README.txt
beside it if you want the About link to work.

Explore Heroes, Cards, Relics, Destinies, Matchups, Hero x seat, Winning strategies
and Balance patch. Click an image for card details. Filters, sorting and CSV/JSON
exports work offline. This is a fixed snapshot; it does not refresh from training.

Exactly {count:,} completed games from the September 28, 2026 approved relic balance
patch. Both players use the same frozen hybrid AI. Distinct hero pairs are evenly
distributed, with 75 games per ordered pair. No older cohorts are mixed in.

statistics.json: complete aggregated statistics.
games.json: all game results (heroes, seats, winner, rounds, seed).
strategy-games.jsonl: per-game strategy and balance measurements, not action replays.
snapshot-info.json: model and source-data identification.

Scores describe this AI and this sample, not perfect play. Card/relic associations
are affected by selection, timing and deck composition; small samples are uncertain.
"""
    entries = {'index.html': html.encode(), 'README.txt': readme.encode(),
               'statistics.json': encoded(stats).encode(),
               'games.json': encoded({'schema': 'shards-offline-game-results-v1', 'games': count, 'rows': games['rows']}).encode(),
               'strategy-games.jsonl': ('\n'.join(encoded(portable(r)) for r in strategy_rows)+'\n').encode(),
               'snapshot-info.json': json.dumps(info, indent=2).encode()}
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        raise FileExistsError(output)
    with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=7) as archive:
        for name, body in entries.items():
            archive.writestr(name, body)
    print(json.dumps(dict(zip=str(output), bytes=output.stat().st_size, html_bytes=len(entries['index.html']), **info), indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    export(args.snapshot, args.output)
