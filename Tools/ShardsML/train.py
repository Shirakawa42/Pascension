"""Train the SoI value net on SOIP self-play positions and export C#-ready weights.

Usage:
  uv run python train.py --data ../ShardsData/selfplay/gen0 [--epochs 12] [--out out/gen0]
  uv run python train.py --data ../ShardsData/selfplay/gen0 --fixtures ../ShardsData/neural/fixtures.soip

Reads every *.soip under --data (32-byte header: magic 'SOIP', formatVersion u16,
featureSchema u16, featureCount u32, recordSize u32, 16B reserved). Splits
train/val by gameSeed hash (never same-game leakage). MLP 768-[--layers]-1
(default 512-256-128; the C# loader accepts any shape from the header).
Label: z (final outcome); when a record carries a search q >= 0 the target is
(1-w)*z + w*q with w = --q-weight (default 0.5). Exports:
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

DEFAULT_LAYERS = "512,256,128"


def make_dtype(features: int) -> np.dtype:
    return np.dtype([
        ("x", "<f4", features), ("z", "<f4"), ("q", "<f4"),
        ("seed", "<u8"), ("move", "<u2"), ("seat", "u1"), ("flags", "u1"), ("pad", "V4"),
    ])


def read_header(file: str):
    with open(file, "rb") as f:
        magic, fmt, schema, feat, rec = struct.unpack("<IHHII", f.read(16))
    assert magic == 0x50494F53, f"{file}: bad magic"
    return schema, feat, rec


def load_dir(paths: str):
    """Comma-separated dirs; append " CAP" (space-separated int) to subsample a dir
    with a fixed seed, e.g. "gen0 280000,gen1,gen1b" — caps the bootstrap so fresh
    search data isn't drowned (the attempt-1/-2 lesson). Feature count and schema
    come from the file headers; every file must agree (schemas never mix)."""
    parts = []
    schema = features = dtype = None
    for spec in paths.split(","):
        tokens = spec.strip().rsplit(" ", 1)
        path, cap = (tokens[0], int(tokens[1])) if len(tokens) == 2 and tokens[1].isdigit() else (spec.strip(), None)
        dir_parts = []
        for file in sorted(glob.glob(os.path.join(path, "*.soip"))):
            s, feat, rec = read_header(file)
            if schema is None:
                schema, features = s, feat
                dtype = make_dtype(features)
            assert (s, feat) == (schema, features), \
                f"{file}: schema {s}/feat {feat} mixes with {schema}/{features}"
            assert rec == dtype.itemsize, f"{file}: record size {rec} != {dtype.itemsize}"
            dir_parts.append(np.fromfile(file, dtype=dtype, offset=32))
        assert dir_parts, f"no .soip files under {path}"
        block = np.concatenate(dir_parts)
        if cap is not None and len(block) > cap:
            block = block[np.random.default_rng(42).choice(len(block), cap, replace=False)]
        print(f"  {path}: {len(block):,} positions" + (f" (capped from more)" if cap else ""))
        parts.append(block)
    data = np.concatenate(parts)
    print(f"loaded {len(data):,} positions total (schema {schema}, {features} features)")
    return data, schema, features


class ValueNet(nn.Module):
    def __init__(self, features, hidden):
        super().__init__()
        dims = [features] + hidden
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
    ap.add_argument("--layers", default=DEFAULT_LAYERS,
                    help="hidden layer widths, e.g. '1024,512,256' (C# loads any shape)")
    ap.add_argument("--q-weight", type=float, default=0.5,
                    help="blend weight w in (1-w)*z + w*q for q-labeled records")
    args = ap.parse_args()
    hidden = [int(x) for x in args.layers.split(",")]

    device = "cuda" if torch.cuda.is_available() else "cpu"
    if device == "cuda":
        cap = torch.cuda.get_device_capability()
        print(f"cuda: {torch.cuda.get_device_name()} sm_{cap[0]}{cap[1]}")
    else:
        print("WARNING: training on CPU (torch sees no CUDA — 5090 needs a cu128 build)")

    data, schema, features = load_dir(args.data)
    # Split by gameSeed hash so both perspectives of a game land on the same side.
    seed_hash = (data["seed"] * np.uint64(0x9E3779B97F4A7C15)) >> np.uint64(56)
    val_mask = seed_hash < np.uint64(16)  # ~6.25% validation
    train, val = data[~val_mask], data[val_mask]
    print(f"train {len(train):,} / val {len(val):,}")

    def targets(d):
        z, q = d["z"].astype(np.float32), d["q"].astype(np.float32)
        w = args.q_weight
        return np.where(q >= 0, (1 - w) * z + w * q, z)

    x_train = torch.from_numpy(np.ascontiguousarray(train["x"]))
    y_train = torch.from_numpy(targets(train))
    x_val = torch.from_numpy(np.ascontiguousarray(val["x"])).to(device)
    y_val = torch.from_numpy(targets(val)).to(device)

    model = ValueNet(features, hidden).to(device)
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
        "schemaVersion": schema,
        "featureCount": features,
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
        fs, ff, _ = read_header(args.fixtures)
        assert (fs, ff) == (schema, features), \
            f"fixtures are schema {fs}/{ff}, training data {schema}/{features} — regenerate with soisim netfixture"
        fixtures = np.fromfile(args.fixtures, dtype=make_dtype(features), offset=32)
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
