#!/usr/bin/env bash
set -euo pipefail
task_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
task_venv="${SHARDS_PREFLIGHT_VENV:-/home/lva/.venvs/shards-preflight}"
task_python="${SHARDS_BASE_PYTHON:-/usr/bin/python3}"
task_dotnet="${SHARDS_DOTNET:-/home/lva/.dotnet/dotnet}"
if [[ ! -x "$task_venv/bin/python" ]]; then
  uv venv "$task_venv" --python "$task_python"
fi
uv pip install --python "$task_venv/bin/python" --index-url https://download.pytorch.org/whl/cu130 -r "$task_dir/requirements-lock.txt"
"$task_dotnet" build "$task_dir/Host/TrainingHost.csproj" -c Release --nologo --verbosity minimal
"$task_dotnet" "$task_dir/Host/bin/Release/net8.0/TrainingHost.dll" selftest
"$task_venv/bin/python" "$task_dir/Host/check_transport.py"
"$task_venv/bin/python" -c 'import torch; assert torch.cuda.is_available(); print(torch.__version__, torch.version.cuda, torch.cuda.get_device_name(), torch.cuda.get_device_capability())'
