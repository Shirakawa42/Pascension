"""Same V10 inputs, rules and weights; efficient shared-effect embedding backward."""
import hashlib
from pathlib import Path
import variant_v10_runtime as v10
import learning_model,train_campaign
from choice_policy_v11 import ChoicePolicy
HERE=Path(__file__).resolve().parent
SCHEMA='shards-real-selfplay-v11'
BASE_V10_SOURCE='6fa50aec9ca63a3218a62606f3ec1f8469bc5b112e40072ec4eeb85e2050757e'
_installed=False

def variant_identity(config):
    base,catalog=v10.variant_identity(config)
    if base['training_source_sha256']!=BASE_V10_SOURCE:raise RuntimeError('Frozen V10 ancestry changed')
    sources=[HERE/name for name in ('variant_v11_runtime.py','variant_v11_entry.py','choice_policy_v11.py','migrate_runtime_v11.py')]
    return dict(base,schema=SCHEMA,base_v10_training_source_sha256=BASE_V10_SOURCE,
        training_source_sha256=hashlib.sha256((BASE_V10_SOURCE+train_campaign.file_hash(sources)).encode()).hexdigest(),
        effect_lookup_v11='Identical forward values; embedding backward replaces repeated advanced-index scatter accumulation'),catalog

def install(*,statistics_directory=None):
    global _installed
    if _installed:return
    variant_identity(train_campaign.TrainConfig(adaptive_actors=True))
    v10.install(statistics_directory=statistics_directory)
    learning_model.LearningPolicy=train_campaign.LearningPolicy=ChoicePolicy
    train_campaign.identity=variant_identity
    _installed=True
