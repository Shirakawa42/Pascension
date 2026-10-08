from pathlib import Path
from types import SimpleNamespace
import tempfile
import threading
import unittest
from unittest.mock import patch
import urllib.error
import urllib.request

from statistics_assets import CardArtwork
from monitor_training import make_server, Monitor


class ArtworkTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.metadata = {"cards": [{"id": "original", "replaces_id": None},
                                    {"id": "replacement", "replaces_id": "original"}],
                         "heroes": [{"id": "tetra"}]}
        (self.root/"original.png").write_bytes(b"\x89PNG\r\n\x1a\nfixture")
        (self.root/"soichar_tetra.png").write_bytes(b"portrait")
        self.art = CardArtwork(self.metadata, self.root)

    def test_game_replacement_and_hero_mapping_and_unknown_paths(self):
        self.assertEqual(self.art.path("replacement"), self.art.path("original"))
        self.assertEqual(self.art.path("tetra"), self.root/"soichar_tetra.png")
        for value in ("../../budget", "/etc/passwd", "original.png", "unknown", ""):
            self.assertIsNone(self.art.path(value))

    def test_symlinked_art_cannot_escape_allowlist(self):
        (self.root/"original.png").unlink()
        (self.root/"original.png").symlink_to(self.root/"soichar_tetra.png")
        self.assertIsNone(self.art.path("original"))

    def test_http_image_cache_and_unknown_id(self):
        server = make_server(SimpleNamespace(card_artwork=self.art), 0)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        url = f"http://127.0.0.1:{server.server_port}/api/card-image?id=original"
        with urllib.request.urlopen(url) as response:
            self.assertEqual(response.headers["Content-Type"], "image/png")
            self.assertIn("max-age=3600", response.headers["Cache-Control"])
            tag = response.headers["ETag"]
            self.assertEqual(response.read(), (self.root/"original.png").read_bytes())
        with self.assertRaises(urllib.error.HTTPError) as cached:
            urllib.request.urlopen(urllib.request.Request(url, headers={"If-None-Match": tag}))
        self.assertEqual(cached.exception.code, 304)
        with self.assertRaises(urllib.error.HTTPError) as unknown:
            urllib.request.urlopen(url.replace("id=original", "id=../../budget"))
        self.assertEqual(unknown.exception.code, 404)

    def test_statistics_serialization_is_cached_until_a_publication_changes(self):
        monitor = Monitor(self.root)
        monitor.balance_sources = {"training_pool": {"snapshot_id": "first", "updated_wall": 1,
            "state": "ready", "totals": {"resolved_games": 10000}}}
        first, tag = monitor.statistics_response()
        with patch("monitor_training.json.dumps", side_effect=AssertionError("unchanged statistics reserialized")):
            self.assertEqual(monitor.statistics_response(), (first, tag))
        monitor.balance_sources["training_pool"].update(snapshot_id="second", updated_wall=2)
        second, new_tag = monitor.statistics_response()
        self.assertNotEqual(first, second)
        self.assertNotEqual(tag, new_tag)


if __name__ == "__main__":
    unittest.main()
