"""Train the SoI value net on SOIP self-play positions and export C#-ready weights.

Usage:
  uv run python train.py --data ../ShardsData/selfplay/gen0 [--epochs 12] [--out out/gen0]
  uv run python train.py --data ../ShardsData/selfplay/gen0 --fixtures ../ShardsData/neural/fixtures.soip

Reads every *.soip under --data (32-byte header: magic 'SOIP', formatVersion u16,
featureSchema u16, featureCount u32, recordSize u32, 16B reserved). Splits
train/val by gameSeed hash (never same-game leakage). MLP 768-512-256-128-1.
Label: z (final outcome); when a record carries a search q >= 0 the target is
0.5*z + 0.5*q. Exports:
  out/weights.bin   f16 blob, per layer W row-major [out][in] then bias [out]
  out/weights.json  schema header + sha256 + val metrics
  out/expected.json (only with --fixtures) net outputs on the fixture states
"""

import argparse
import glob
import hashlib
import json
import os
import struct
import sys

import numpy as np
import torch
import torch.nn as nn

FEATURES = 768
LAYERS = [512, 256, 128]
RECORD_DTYPE = np.dtype([
    ("x", "<f4", FEATURES), ("z", "<f4"), ("q", "<f4"),
    ("seed", "<u8"), ("move", "<u2"), ("seat", "u1"), ("flags", "u1"), ("pad", "V4"),
])


def load_dir(paths: str) -> np.ndarray:
    """Comma-separated dirs = the sliding training window (e.g. gen0,gen1)."""
    parts = []
    files = []
    for path in paths.split(","):
        files += sorted(glob.glob(os.path.join(path.strip(), "*.soip")))
    for file in files:
        with open(file, "rb") as f:
            magic, fmt, schema, feat, rec = struct.unpack("<IHHII", f.read(16))
        assert magic == 0x50494F53, f"{file}: bad magic"
        assert schema == 1 and feat == FEATURES, f"{file}: schema {schema}/{feat} != 1/{FEATURES}"
        assert rec == RECORD_DTYPE.itemsize, f"{file}: record size {rec} != {RECORD_DTYPE.itemsize}"
        parts.append(np.fromfile(file, dtype=RECORD_DTYPE, offset=32))
    assert parts, f"no .soip files under {paths}"
    data = np.concatenate(parts)
    print(f"loaded {len(data):,} positions from {paths}")
    return data


class ValueNet(nn.Module):
    def __init__(self):
        super().__init__()
        dims = [FEATURES] + LAYERS
        layers = []
        for i in range(len(dims) - 1):
            layers += [nn.Linear(dims[i], dims[i + 1]), nn.ReLU()]
        layers += [nn.Linear(dims[-1], 1)]
        self.net = nn.Sequential(*layers)

    def forward(self, x):
        return self.net(x).squeeze(-1)  # logits; sigmoid at export/eval


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True)
    ap.add_argument("--out", default="out/latest")
    ap.add_argument("--epochs", type=int, default=12)
    ap.add_argument("--batch", type=int, default=8192)
    ap.add_argument("--lr", type=float, default=3e-4)
    ap.add_argument("--fixtures", default=None)
    ap.add_argument("--generation", type=int, default=0)
    args = ap.parse_args()

    device = "cuda" if torch.cuda.is_available() else "cpu"
    if device == "cuda":
        cap = torch.cuda.get_device_capability()
        print(f"cuda: {torch.cuda.get_device_name()} sm_{cap[0]}{cap[1]}")
    else:
        print("WARNING: training on CPU (torch sees no CUDA — 5090 needs a cu128 build)")

    data = load_dir(args.data)
    # Split by gameSeed hash so both perspectives of a game land on the same side.
    seed_hash = (data["seed"] * np.uint64(0x9E3779B97F4A7C15)) >> np.uint64(56)
    val_mask = seed_hash < np.uint64(16)  # ~6.25% validation
    train, val = data[~val_mask], data[val_mask]
    print(f"train {len(train):,} / val {len(val):,}")

    def targets(d):
        z, q = d["z"].astype(np.float32), d["q"].astype(np.float32)
        return np.where(q >= 0, 0.5 * z + 0.5 * q, z)

    x_train = torch.from_numpy(np.ascontiguousarray(train["x"]))
    y_train = torch.from_numpy(targets(train))
    x_val = torch.from_numpy(np.ascontiguousarray(val["x"])).to(device)
    y_val = torch.from_numpy(targets(val)).to(device)

    model = ValueNet().to(device)
    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-5)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=args.epochs)
    loss_fn = nn.BCEWithLogitsLoss()

    n = len(x_train)
    for epoch in range(args.epochs):
        model.train()
        perm = torch.randperm(n)
        total = 0.0
        for i in range(0, n, args.batch):
            idx = perm[i:i + args.batch]
            xb = x_train[idx].to(device, non_blocking=True)
            yb = y_train[idx].to(device, non_blocking=True)
            opt.zero_grad(set_to_none=True)
            with torch.autocast(device_type=device, dtype=torch.bfloat16, enabled=device == "cuda"):
                loss = loss_fn(model(xb), yb)
            loss.backward()
            opt.step()
            total += loss.item() * len(idx)
        sched.step()
        model.eval()
        with torch.no_grad():
            val_logits = model(x_val)
            val_loss = loss_fn(val_logits, y_val).item()
            val_acc = ((torch.sigmoid(val_logits) > 0.5) == (y_val > 0.5)).float().mean().item()
        print(f"epoch {epoch + 1:2d}  train {total / n:.4f}  val {val_loss:.4f}  acc {val_acc:.3f}")

    os.makedirs(args.out, exist_ok=True)
    model = model.cpu().eval()

    blob = bytearray()
    layer_dims = []
    for module in model.net:
        if isinstance(module, nn.Linear):
            layer_dims.append(module.out_features)
            blob += module.weight.detach().numpy().astype(np.float16).tobytes()
            blob += module.bias.detach().numpy().astype(np.float16).tobytes()
    bin_path = os.path.join(args.out, "weights.bin")
    with open(bin_path, "wb") as f:
        f.write(blob)
    header = {
        "schemaVersion": 1,
        "featureCount": FEATURES,
        "layers": layer_dims,
        "act": "relu",
        "out": "sigmoid",
        "dtype": "f16",
        "sha256": hashlib.sha256(blob).hexdigest(),
        "generation": args.generation,
        "valLoss": round(val_loss, 5),
        "valAcc": round(val_acc, 4),
        "positions": len(data),
    }
    with open(os.path.join(args.out, "weights.json"), "w") as f:
        json.dump(header, f, indent=2)
    print(f"exported {len(blob):,} bytes -> {bin_path}  (val acc {val_acc:.3f})")
    try:  # campaign log (best-effort; path relative to Tools/ShardsML)
        from datetime import datetime
        with open(os.path.join(os.path.dirname(__file__), "..", "ShardsData", "campaign-log.md"),
                  "a", encoding="utf-8") as log:
            log.write(f"- **{datetime.now():%Y-%m-%d %H:%M}** — trained generation "
                      f"{args.generation}: val acc {val_acc:.1%}, {len(data):,} positions\n")
    except OSError:
        pass

    if args.fixtures:
        fixtures = np.fromfile(args.fixtures, dtype=RECORD_DTYPE, offset=32)
        with torch.no_grad():
            # f16-roundtrip the weights so expectations match the C# dequantized net.
            for module in model.net:
                if isinstance(module, nn.Linear):
                    module.weight.copy_(torch.from_numpy(
                        module.weight.numpy().astype(np.float16).astype(np.float32)))
                    module.bias.copy_(torch.from_numpy(
                        module.bias.numpy().astype(np.float16).astype(np.float32)))
            out = torch.sigmoid(model(torch.from_numpy(np.ascontiguousarray(fixtures["x"])))).numpy()
        with open(os.path.join(args.out, "expected.json"), "w") as f:
            json.dump([round(float(v), 6) for v in out], f)
        print(f"fixture expectations ({len(out)}) -> {args.out}/expected.json")


if __name__ == "__main__":
    sys.exit(main())
