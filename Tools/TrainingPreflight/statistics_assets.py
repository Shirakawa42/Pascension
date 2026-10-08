"""Read-only allowlist for the game's existing Shards card artwork."""
from pathlib import Path
import json

HERE = Path(__file__).resolve().parent
ART_ROOT = HERE.parents[1]/"Assets/Art/Shards/Cards"


class CardArtwork:
    def __init__(self, metadata=None, root=ART_ROOT):
        self.root = Path(root).resolve()
        self.catalog = metadata or json.loads((HERE/"results/balance-card-catalog.json").read_text())
        self.names = {row["id"]: row.get("replaces_id") or row["id"] for row in self.catalog["cards"]}
        self.names.update({row["id"]: "soichar_"+row["id"] for row in self.catalog["heroes"]})

    def path(self, definition):
        name = self.names.get(definition)
        if name is None or Path(name).name != name:
            return None
        path = self.root/(name+".png")
        if path.is_symlink() or not path.is_file() or path.resolve().parent != self.root:
            return None
        return path

    def presentation(self):
        # Rules/rich text remain inert JSON strings; the page renders text nodes.
        return {"cards": self.catalog["cards"], "heroes": self.catalog["heroes"]}
