"""V7 learner continuation on the exact frozen V6 rules, host and observations."""
import hashlib
from pathlib import Path
import learning_model
import train_campaign
import variant_v6_runtime as v6
from choice_policy_v7 import ChoicePolicy, EXPLORATION

HERE = Path(__file__).resolve().parent
SCHEMA = 'shards-real-selfplay-v7'
BASE_V6_SOURCE = '255421fa0c0ae41a388efb4d90bbfd9b31d93e1ee6512306bc59d6a0849d3ef6'
_installed = False

def variant_identity(config):
    base, catalog = v6.variant_identity(config)
    if base['training_source_sha256'] != BASE_V6_SOURCE:
        raise RuntimeError('V7 requires exact frozen V6 lineage')
    files = [HERE/name for name in ('choice_policy_v7.py', 'choice_logits_v7.py', 'variant_v7_runtime.py',
        'variant_v7_entry.py', 'migrate_runtime_v7.py')]
    return dict(base, schema=SCHEMA,
        training_source_sha256=hashlib.sha256((BASE_V6_SOURCE+train_campaign.file_hash(files)).encode()).hexdigest(),
        base_v6_training_source_sha256=BASE_V6_SOURCE,
        choice_learning={'schema':'shards-categorical-volos-choice-mixture-v1',
            'gpu_choice_logits':'fused FP32 forward and chain-rule backward; Torch CPU reference',
            'volos_modes':4,'residual_head':'linear-state-to-four-independent-logits',
            'relic_mixture_probability':EXPLORATION[0], 'destiny_mixture_probability':EXPLORATION[1],
            'mixture_scope':'normal legal menus; saved per-policy buffers; actor and learner identical',
            'old_policy_migration':'zero head and zero exploration; learner enables declared mixture',
            'evaluation':'actual saved policy distribution; no silent exploration override'}), catalog

def install(*, statistics_directory=None):
    global _installed
    if _installed:
        return
    variant_identity(train_campaign.TrainConfig(adaptive_actors=True))
    v6.install(statistics_directory=statistics_directory)
    learning_model.LearningPolicy = ChoicePolicy
    train_campaign.LearningPolicy = ChoicePolicy
    train_campaign.identity = variant_identity
    _installed = True
