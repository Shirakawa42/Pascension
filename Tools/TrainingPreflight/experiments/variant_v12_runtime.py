"""Same semantic policy with explicitly versioned conservative continuation."""
import hashlib
from pathlib import Path
import variant_v11_runtime as v11
import train_campaign
HERE=Path(__file__).resolve().parent
SCHEMA='shards-real-selfplay-v12'
BASE_V11_SOURCE='0bc8231b72a39fd4e229144a743e36fda36e9aa2ce8e11e230c1845733799196'
_installed=False

def variant_identity(config):
    base,catalog=v11.variant_identity(config)
    if base['training_source_sha256']!=BASE_V11_SOURCE:raise RuntimeError('Frozen V11 ancestry changed')
    sources=[HERE/name for name in ('variant_v12_runtime.py','variant_v12_entry.py','recover_runtime_v12.py')]
    return dict(base,schema=SCHEMA,base_v11_training_source_sha256=BASE_V11_SOURCE,
        training_source_sha256=hashlib.sha256((BASE_V11_SOURCE+train_campaign.file_hash(sources)).encode()).hexdigest(),
        continuation_v12='Explicit validated-policy recovery; all spent budget, counters and seed history retained'),catalog

def install(*,statistics_directory=None):
    global _installed
    if _installed:return
    variant_identity(train_campaign.TrainConfig(adaptive_actors=True))
    v11.install(statistics_directory=statistics_directory)
    train_campaign.identity=variant_identity
    _installed=True
