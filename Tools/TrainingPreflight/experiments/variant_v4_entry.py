"""Launch only an explicitly identified same-observation HostV4 continuation."""
import argparse
import json
from pathlib import Path
import sys

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
from bench_common import save_json
from variant_v4_runtime import install, variant_identity
from train_campaign import TrainConfig


def training_arguments(parser):
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--ledger", type=Path, required=True)
    parser.add_argument("--seconds", type=float, required=True)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--resume", type=Path, required=True)
    parser.add_argument("--label", default="main-h128-v4")


def main():
    if len(sys.argv) < 2 or sys.argv[1] not in ("identity", "train", "supervise", "evaluate"):
        raise SystemExit("Expected identity/train/supervise/evaluate")
    mode = sys.argv.pop(1)
    if mode == "identity":
        parser = argparse.ArgumentParser()
        parser.add_argument("--config", type=Path, required=True)
        parser.add_argument("--output", type=Path, required=True)
        args = parser.parse_args()
        identity, catalog = variant_identity(TrainConfig(**json.loads(args.config.read_text())))
        save_json(args.output, {"identity": identity, "catalog": catalog})
    elif mode == "supervise":
        parser = argparse.ArgumentParser()
        training_arguments(parser)
        args = parser.parse_args()
        if not 1 <= args.seconds <= 43200:
            parser.error("Session must stay within the existing 12-hour allocation")
        from supervise_training import supervise
        command = [sys.executable, str(Path(__file__).resolve()), "train", *sys.argv[1:]]
        raise SystemExit(supervise(command, args.run_dir, args.ledger))
    else:
        stats_directory = None
        if mode == "train":
            routing = argparse.ArgumentParser(add_help=False)
            routing.add_argument("--run-dir", type=Path, required=True)
            routing_args, _ = routing.parse_known_args()
            stats_directory = routing_args.run_dir / "training-statistics"
        install(statistics_directory=stats_directory)
        if mode == "train":
            if "--resume" not in sys.argv:
                raise SystemExit("This variant requires a migrated checkpoint; --resume is mandatory")
            import train_campaign
            train_campaign.main()
        else:
            import evaluate_checkpoints
            original_save = evaluate_checkpoints.save_json

            def save_variant_report(path, report):
                if "runtime" in report:
                    import torch
                    report["runtime"]["matmul_precision"] = torch.backends.cuda.matmul.fp32_precision
                return original_save(path, report)

            evaluate_checkpoints.save_json = save_variant_report
            evaluate_checkpoints.main()


if __name__ == "__main__":
    main()
