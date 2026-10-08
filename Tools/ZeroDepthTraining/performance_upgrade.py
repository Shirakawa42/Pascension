"""Guarded runtime, curriculum and reviewed LR migration with a fixed deadline.

Prepared outside the repository. Nothing runs or writes a live campaign on
import. Root must stop its owned launcher, charge the pause, and hold the existing
CampaignBudget lock before calling commit_upgrade. Source review is explicit:
approved_changes binds every before/after hash, including newly added tests.
"""
from __future__ import annotations
import argparse,contextlib,copy,hashlib,json,math,os
from pathlib import Path
import shutil,tempfile,time

SCHEMA='shards-zero-depth-performance-upgrade-v1'
BOOT_PATH=Path('/proc/sys/kernel/random/boot_id')
PRODUCTION_PATHS={
 'Tools/ZeroDepthTraining/packed_transport.py',
 'Tools/ZeroDepthTraining/host.py',
 'Tools/ZeroDepthTraining/replay.py',
 'Tools/ZeroDepthTraining/game_stats.py',
 'Tools/ZeroDepthTraining/monitor.py',
 'Tools/ZeroDepthTraining/prepare.py',
 'Tools/ZeroDepthTraining/Host/Program.cs',
 'Tools/ZeroDepthTraining/Host/Encoder.cs',
 'Tools/ZeroDepthTraining/Host/LaneWorkers.cs',
 'Tools/ZeroDepthTraining/train.py',
 'Tools/ZeroDepthTraining/performance_upgrade.py',
 'Tools/ZeroDepthTraining/native_packing.py',
 'Tools/ZeroDepthTraining/combined_actor.py',
 'Tools/ZeroDepthTraining/gpu_behavior.py',
 'Tools/ZeroDepthTraining/gpu_owned_copy.py',
 'Tools/ZeroDepthTraining/pipeline_actor.py',
 'Tools/ZeroDepthTraining/gpu_ppo_step.py',
 'Tools/ZeroDepthTraining/opponent_archive.py',
 'Tools/ZeroDepthTraining/league.py',
 'Tools/ZeroDepthTraining/launch.py',
 'Tools/ZeroDepthTraining/Host/Knowledge.cs',
 'Tools/ZeroDepthTraining/Host/HeroAssignments.cs',
 'Tools/TrainingPreflight/campaign_state.py',
}
TEST_PATHS={
 'Tools/ZeroDepthTraining/tests/test_orchestration.py',
 'Tools/ZeroDepthTraining/tests/test_performance_upgrade.py',
 'Tools/ZeroDepthTraining/tests/test_packed_transport.py',
 'Tools/ZeroDepthTraining/tests/test_training_recovery.py',
 'Tools/ZeroDepthTraining/tests/test_native_packing.py',
 'Tools/ZeroDepthTraining/tests/test_gpu_behavior.py',
 'Tools/ZeroDepthTraining/tests/test_gpu_owned_copy.py',
 'Tools/ZeroDepthTraining/tests/test_pipeline_actor.py',
 'Tools/ZeroDepthTraining/tests/test_gpu_ppo_step.py',
 'Tools/ZeroDepthTraining/tests/test_hero_mode.py',
 'Tools/ZeroDepthTraining/tests/test_game_stats.py',
 'Tools/ZeroDepthTraining/tests/test_monitor.py',
 'Tools/ZeroDepthTraining/tests/test_opponent_archive.py',
 'Tools/ZeroDepthTraining/tests/test_learning_diagnostics.py',
 'Tools/ZeroDepthTraining/tests/test_ppo_diagnostics.py',
 'Tools/ZeroDepthTraining/tests/test_dashboard_diagnostics.py',
 'Tools/ZeroDepthTraining/tests/test_learning_upgrade.py',
 'Tools/ZeroDepthTraining/tests/test_learning_rate_upgrade.py',
 'Tools/ZeroDepthTraining/tests/test_optimizer_resume.py',
 'Tools/ZeroDepthTraining/tests/test_league.py',
 'Tools/ZeroDepthTraining/tests/test_launch_lifecycle.py',
 'Tools/ZeroDepthTraining/Host/HeroAssignmentsSelfTest.cs',
 'Tools/ZeroDepthTraining/Host/SelfTest.cs',
}
IDENTITY_EXECUTION_FIELDS={'source_fingerprint','host_sha256'}
NEW_SOURCE_PATHS={
 'Tools/ZeroDepthTraining/Host/LaneWorkers.cs',
 'Tools/ZeroDepthTraining/performance_upgrade.py',
 'Tools/ZeroDepthTraining/native_packing.py',
 'Tools/ZeroDepthTraining/pipeline_actor.py',
 'Tools/ZeroDepthTraining/gpu_ppo_step.py',
 'Tools/ZeroDepthTraining/Host/HeroAssignments.cs',
 'Tools/ZeroDepthTraining/combined_actor.py',
 'Tools/ZeroDepthTraining/gpu_behavior.py',
 'Tools/ZeroDepthTraining/gpu_owned_copy.py',
 'Tools/ZeroDepthTraining/opponent_archive.py',
 'Tools/ZeroDepthTraining/league.py',
}|TEST_PATHS

class UpgradeError(RuntimeError): pass

def canonical(value):
 return json.dumps(value,sort_keys=True,separators=(',',':'),allow_nan=False).encode()

def sha256_file(path):
 digest=hashlib.sha256()
 with Path(path).open('rb') as stream:
  for block in iter(lambda:stream.read(1<<20),b''): digest.update(block)
 return digest.hexdigest()

def _read_object(path,maximum=8*1024*1024):
 path=Path(path)
 if path.is_symlink() or not path.is_file() or path.stat().st_size>maximum: raise UpgradeError(f'Invalid metadata file: {path}')
 value=json.loads(path.read_text())
 if not isinstance(value,dict): raise UpgradeError(f'Metadata must be an object: {path}')
 return value

def source_inventory(repo_root):
 """Exact current train.source_fingerprint inventory, plus individual hashes."""
 repo_root=Path(repo_root).resolve();here=repo_root/'Tools/ZeroDepthTraining'; files=[]
 for folder in (here,repo_root/'Assets/Scripts/Core',repo_root/'Assets/Scripts/Shards/Engine',repo_root/'Assets/Scripts/Shards/Content',repo_root/'Assets/Scripts/Shards/AI'):
  files.extend(path for path in folder.rglob('*') if path.suffix in ('.py','.cs','.csproj') and not {'bin','obj','__pycache__'}.intersection(path.parts))
 files.extend(repo_root/'Tools/TrainingPreflight'/name for name in ('campaign_state.py','supervise_training.py','bench_common.py'))
 files.append(repo_root/'Tools/BalancePatchHost/EffectDescriptors.cs')
 digest=hashlib.sha256(); inventory={}
 for path in sorted(set(files)):
  if path.is_symlink(): raise UpgradeError(f'Symlink in runtime source inventory: {path}')
  relative=str(path.relative_to(repo_root)); data=path.read_bytes(); digest.update(relative.encode());digest.update(data)
  inventory[relative]={'sha256':hashlib.sha256(data).hexdigest(),'bytes':len(data)}
 return {'schema':'shards-runtime-source-inventory-v1','source_fingerprint':digest.hexdigest(),'files':inventory}

def source_delta(before,after):
 a,b=before['files'],after['files']
 return {path:{'before':a.get(path,{}).get('sha256'),'after':b.get(path,{}).get('sha256')} for path in sorted(set(a)|set(b)) if a.get(path)!=b.get(path)}

def validate_configuration(old_configuration,new_configuration,approved_configuration_changes=None):
 """Allow a reviewed curriculum, original-LR or rollout-memory resize.

 The absent old hero field denotes legacy policy drafting. An LR reduction is
 independent of curriculum changes and cannot alter any other training setting.
 """
 if not isinstance(old_configuration,dict) or not isinstance(new_configuration,dict): raise UpgradeError('Invalid configuration objects')
 approved={} if approved_configuration_changes is None else approved_configuration_changes
 changed={key:{'before':old_configuration.get(key),'after':new_configuration.get(key)}
          for key in sorted(set(old_configuration)|set(new_configuration))
          if key not in old_configuration or key not in new_configuration or old_configuration[key]!=new_configuration[key]}
 if changed!=approved: raise UpgradeError('Exact reviewed configuration change differs')
 if not changed: return changed
 if set(changed)=={'hero_mode'}:
  if 'hero_mode' in old_configuration and old_configuration['hero_mode']!='policy': raise UpgradeError('Original hero mode must use policy drafting')
  if new_configuration.get('hero_mode')!='balanced_random': raise UpgradeError('New hero mode must use balanced random matchups')
 elif set(changed)=={'archive_strategy'}:
  transition=(old_configuration.get('archive_strategy','recent'),new_configuration.get('archive_strategy'))
  if transition not in (('recent','historical'),('historical','prioritized')):
   raise UpgradeError('Only recent to historical or historical to prioritized curriculum is permitted')
 elif set(changed)=={'batch','capacity'}:
  # Resize rollout memory without changing the model, information or optimizer.
  # Keep this migration restricted to the explicitly requested resource profile.
  keys=('batch','capacity')
  before=tuple(old_configuration.get(key) for key in keys)
  after=tuple(new_configuration.get(key) for key in keys)
  if (any(type(value) is not int for value in before+after)
      or (before,after) not in (((128,131072),(32,32768)),((32,32768),(96,98304)))):
   raise UpgradeError('Only reviewed batch/capacity profiles 128/131072 to 32/32768 or 32/32768 to 96/98304 are permitted')
 elif set(changed)=={'batch','capacity','workers'}:
  keys=('batch','capacity','workers')
  before=tuple(old_configuration.get(key) for key in keys)
  after=tuple(new_configuration.get(key) for key in keys)
  if (any(type(value) is not int for value in before+after)
      or (before,after)!=((96,98304,8),(64,65536,6))):
   raise UpgradeError('Only the reviewed shared-computer resize 96/98304/8 to 64/65536/6 is permitted')
 elif set(changed)=={'learning_rate'}:
  before,after=old_configuration.get('learning_rate'),new_configuration.get('learning_rate')
  if (type(before) not in (int,float) or type(after) not in (int,float)
      or not math.isfinite(before) or not math.isfinite(after)
      or (before,after) not in ((.0003,.00015),(.0003,.0001),(.0001,.00005),(.00005,.0001)) or not 0<after<.1):
   raise UpgradeError('Only reviewed learning rate reductions .0003 to .00015/.0001, .0001 to .00005, or exact rollback .00005 to .0001 are permitted')
 else:
  raise UpgradeError('Only one explicitly reviewed curriculum, learning rate or rollout-memory resize may change; all other settings remain pinned')
 return changed

def validate_transition(old_identity,new_identity,old_inventory,new_inventory,approved_changes,approved_configuration_changes=None):
 """Reject unreviewed source, identity and training-configuration drift."""
 if set(old_identity)!=set(new_identity): raise UpgradeError('Identity field inventory changed')
 for key in old_identity:
  if key not in IDENTITY_EXECUTION_FIELDS|{'configuration'} and old_identity[key]!=new_identity[key]: raise UpgradeError(f'Immutable identity changed: {key}')
 validate_configuration(old_identity['configuration'],new_identity['configuration'],approved_configuration_changes)
 if old_identity.get('schema')!='shards-zero-depth-training-v1' or old_identity.get('lookahead_depth')!=0: raise UpgradeError('Unsupported training identity')
 if old_inventory.get('source_fingerprint')!=old_identity.get('source_fingerprint') or new_inventory.get('source_fingerprint')!=new_identity.get('source_fingerprint'): raise UpgradeError('Inventory fingerprint does not match identity')
 delta=source_delta(old_inventory,new_inventory)
 if delta!=approved_changes: raise UpgradeError('Exact reviewed source hash delta differs from current inventory')
 if not delta: raise UpgradeError('No reviewed runtime change to migrate')
 if not set(delta)<=(PRODUCTION_PATHS|TEST_PATHS): raise UpgradeError('Unapproved source path outside the performance upgrade scope')
 for path,change in delta.items():
  if change.get('after') is None: raise UpgradeError(f'Source deletion is forbidden: {path}')
  if change.get('before') is None and path not in NEW_SOURCE_PATHS: raise UpgradeError(f'Unexpected new source: {path}')
 return delta

def validate_anchor(anchor,ledger):
 required={'campaign_id','limit_seconds','boot_id','hard_deadline_monotonic','hard_deadline_wall'}
 if not isinstance(anchor,dict) or set(anchor)!=required: raise UpgradeError('Incomplete or unexpected original deadline fields')
 limit=anchor['limit_seconds']
 if (type(limit) not in (int,float) or not math.isfinite(limit) or not 1<=limit<=43200
     or anchor['campaign_id']!=ledger.get('campaign_id') or limit!=ledger.get('limit_seconds')): raise UpgradeError('Original campaign allocation changed')
 for key in ('hard_deadline_monotonic','hard_deadline_wall'):
  if isinstance(anchor[key],bool) or not isinstance(anchor[key],(int,float)) or not math.isfinite(anchor[key]) or anchor[key]<=0: raise UpgradeError('Invalid original deadline')
 if not isinstance(anchor['boot_id'],str) or not anchor['boot_id']: raise UpgradeError('Invalid original boot')
 sessions=ledger.get('sessions',[])
 first=sessions[0] if sessions else ledger.get('active')
 if not isinstance(first,dict): raise UpgradeError('No original campaign session can prove the deadline')
 for key in ('boot_id','hard_deadline_monotonic','hard_deadline_wall'):
  if first.get(key)!=anchor[key]: raise UpgradeError(f'Deadline differs from the original ledger session: {key}')
 return anchor

def session_seconds(run_dir,budget,seconds,*,monotonic=None,boot_id=None,with_deadline=False):
 """Use the original absolute deadline after upgrade; never alter budget limit.

 Call immediately before start_session and compute stop_buffer from this grant.
 A fresh campaign without an upgrade manifest retains the existing behavior.
 """
 path=Path(run_dir)/'performance-upgrade.json'
 if not path.exists():
  grant=min(seconds,budget.remaining_seconds)
  return (grant,None) if with_deadline else grant
 migration=_read_object(path)
 if migration.get('schema')!=SCHEMA or migration.get('state')!='committed': raise UpgradeError('Performance upgrade is incomplete; refusing resume')
 identity=_read_object(Path(run_dir)/'identity.json')
 if migration.get('new_identity_sha256')!=hashlib.sha256(canonical(identity)).hexdigest(): raise UpgradeError('Upgrade lineage differs from current campaign identity')
 ledger=_read_object(budget.path);anchor=validate_anchor(migration.get('original_deadline'),ledger)
 if anchor['campaign_id']!=budget.campaign_id or anchor['limit_seconds']!=budget.limit_seconds or seconds!=budget.limit_seconds: raise UpgradeError('Resume must retain the original campaign allocation')
 current_boot=BOOT_PATH.read_text().strip() if boot_id is None else boot_id
 if current_boot!=anchor['boot_id']: raise UpgradeError('Original monotonic deadline cannot be enforced after a boot change')
 now=time.monotonic() if monotonic is None else monotonic
 if isinstance(now,bool) or not isinstance(now,(int,float)) or not math.isfinite(now): raise UpgradeError('Invalid current monotonic time')
 grant=min(seconds,budget.remaining_seconds,anchor['hard_deadline_monotonic']-now)
 if grant<=0: raise UpgradeError('Original campaign deadline is exhausted')
 return (grant,anchor['hard_deadline_monotonic']) if with_deadline else grant

def _atomic_json(path,value):
 from campaign_state import _atomic_write
 _atomic_write(path,canonical(value)+b'\n')

def write_payload_atomic(path,payload):
 """Stream a safe Torch payload into the existing SOICP001 wire format.

 Does not capture or restore RNG, touch tensor values, or alter the old file on
 serialization/flush failure. It reserves the header and hashes file chunks.
 """
 import torch
 from campaign_state import _HEADER,_MAGIC
 path=Path(path);path.parent.mkdir(parents=True,exist_ok=True)
 fd,temporary=tempfile.mkstemp(prefix='.'+path.name+'.',suffix='.tmp',dir=path.parent)
 try:
  with os.fdopen(fd,'w+b') as stream:
   stream.write(b'\0'*_HEADER.size);torch.save(payload,stream);stream.flush()
   end=stream.tell();length=end-_HEADER.size;digest=hashlib.sha256();stream.seek(_HEADER.size)
   for block in iter(lambda:stream.read(1<<20),b''): digest.update(block)
   stream.seek(0);stream.write(_HEADER.pack(_MAGIC,length,digest.digest()));stream.flush();os.fsync(stream.fileno())
  os.replace(temporary,path)
  directory=os.open(path.parent,os.O_RDONLY|os.O_DIRECTORY)
  try: os.fsync(directory)
  finally: os.close(directory)
 finally:
  with contextlib.suppress(FileNotFoundError): os.unlink(temporary)
 return sha256_file(path)

def _same_state(left,right):
 import torch
 if isinstance(left,torch.Tensor):
  if not isinstance(right,torch.Tensor) or left.dtype!=right.dtype or left.shape!=right.shape: return False
  return torch.equal(left.contiguous().reshape(-1).view(torch.uint8),right.contiguous().reshape(-1).view(torch.uint8))
 if isinstance(left,dict): return isinstance(right,dict) and left.keys()==right.keys() and all(_same_state(left[key],right[key]) for key in left)
 if isinstance(left,(list,tuple)): return type(left)==type(right) and len(left)==len(right) and all(_same_state(a,b) for a,b in zip(left,right))
 return left==right

def _validate_adam_groups(optimizer,learning_rate):
 """Validate saved Adam LR and parameter identity before any publication."""
 if not isinstance(optimizer,dict) or set(optimizer)!={'state','param_groups'} or not isinstance(optimizer['state'],dict):
  raise UpgradeError('Malformed Adam optimizer state')
 groups=optimizer['param_groups']
 if not isinstance(groups,list) or not groups: raise UpgradeError('Adam optimizer has no parameter groups')
 parameter_ids=set()
 for group in groups:
  if not isinstance(group,dict): raise UpgradeError('Malformed Adam optimizer parameter group')
  rate=group.get('lr')
  if type(rate) not in (int,float) or not math.isfinite(rate) or rate!=learning_rate:
   raise UpgradeError('Saved Adam learning rate differs from the pinned original configuration')
  ids=group.get('params')
  if not isinstance(ids,list) or not ids: raise UpgradeError('Malformed Adam optimizer parameter IDs')
  for parameter_id in ids:
   if type(parameter_id)!=int or parameter_id<0 or parameter_id in parameter_ids:
    raise UpgradeError('Malformed or duplicate Adam optimizer parameter IDs')
   parameter_ids.add(parameter_id)
 if any(type(key)!=int or key not in parameter_ids or not isinstance(value,dict)
        for key,value in optimizer['state'].items()): raise UpgradeError('Adam optimizer state does not match its parameter IDs')
 return groups

def _optimizer_with_learning_rate(optimizer,before,after):
 _validate_adam_groups(optimizer,before)
 migrated=copy.deepcopy(optimizer)
 for group in migrated['param_groups']: group['lr']=after
 return migrated

def _verify_optimizer_learning_rate(original,migrated,before,after):
 """Compare every Adam byte and group field except the approved LR values."""
 original_groups=_validate_adam_groups(original,before)
 migrated_groups=_validate_adam_groups(migrated,after)
 if len(original_groups)!=len(migrated_groups) or not _same_state(original['state'],migrated['state']):
  raise UpgradeError('Checkpoint optimizer moments, steps or group structure changed')
 for old,new in zip(original_groups,migrated_groups):
  if old.keys()!=new.keys() or not _same_state({k:v for k,v in old.items() if k!='lr'},
                                             {k:v for k,v in new.items() if k!='lr'}):
   raise UpgradeError('Checkpoint optimizer parameter IDs or non-learning-rate group fields changed')

def _copy_durable(source,destination):
 shutil.copyfile(source,destination)
 with Path(destination).open('rb') as stream: os.fsync(stream.fileno())
 directory=os.open(Path(destination).parent,os.O_RDONLY|os.O_DIRECTORY)
 try: os.fsync(directory)
 finally: os.close(directory)

def commit_upgrade(run_dir,*,repo_root,binary_path,current_catalog,budget,old_identity,new_identity,old_inventory,new_inventory,approved_changes,original_deadline,checkpoint_path=None,approved_configuration_changes=None):
 """Commit only while root owns the stopped campaign's existing budget lock.

 Root charges pause time separately before calling. Immutable prior checkpoint,
 identity, source inventory and a forensic ledger are retained outside the repo.
 The final committed manifest is published last; interrupted commits fail closed.
 Never restore the forensic budget backup: the current ledger is authoritative.
 """
 from campaign_state import load_checkpoint
 run_dir=Path(run_dir).resolve();repo_root=Path(repo_root).resolve()
 budget._require_open()
 if Path(budget.path).resolve()!=run_dir/'budget.json': raise UpgradeError('Budget does not belong to this campaign directory')
 ledger=_read_object(budget.path)
 if ledger.get('active') is not None: raise UpgradeError('Stop and close every training/pause session before committing')
 validate_anchor(original_deadline,ledger)
 if BOOT_PATH.read_text().strip()!=original_deadline['boot_id']: raise UpgradeError('Boot changed since campaign start')
 if time.monotonic()>=original_deadline['hard_deadline_monotonic']: raise UpgradeError('Original deadline expired before migration')
 if _read_object(run_dir/'identity.json')!=old_identity: raise UpgradeError('Campaign no longer has the expected original identity')
 if _read_object(run_dir/'config.json')!=old_identity['configuration']: raise UpgradeError('Campaign configuration changed')
 current=source_inventory(repo_root)
 if current!=new_inventory: raise UpgradeError('Sources changed after review')
 delta=validate_transition(old_identity,new_identity,old_inventory,current,approved_changes,approved_configuration_changes)
 configuration_changes=validate_configuration(old_identity['configuration'],new_identity['configuration'],approved_configuration_changes)
 if sha256_file(binary_path)!=new_identity['host_sha256']: raise UpgradeError('Host binary changed after review')
 catalog=_read_object(run_dir/'catalog.json')
 if catalog!=current_catalog or hashlib.sha256(json.dumps(current_catalog,sort_keys=True).encode()).hexdigest()!=new_identity['catalog_sha256']: raise UpgradeError('Current host observation catalog differs from the original pinned catalog')
 # Production incumbent sources/artifacts must remain frozen, even if the host
 # scheduler and sparse encoder implementation changed without changing outputs.
 from evaluate import verify_bundle
 verify_bundle(run_dir/'incumbent',repo_root=repo_root)
 checkpoint_path=Path(checkpoint_path or run_dir/'latest.soicp')
 if checkpoint_path.resolve()!=run_dir/'latest.soicp': raise UpgradeError('Migration must use this campaign latest checkpoint')
 payload=load_checkpoint(checkpoint_path,expected_identity=old_identity,budget=budget)
 if payload['state'].get('configuration')!=old_identity['configuration']: raise UpgradeError('Checkpoint configuration differs from its identity')
 learning_rate_change=configuration_changes.get('learning_rate')
 migrated_optimizer=None
 if learning_rate_change:
  migrated_optimizer=_optimizer_with_learning_rate(payload['state'].get('optimizer'),
      learning_rate_change['before'],learning_rate_change['after'])
 migrated_archive_metadata=None
 if configuration_changes.get('archive_strategy',{}).get('after')=='prioritized':
  from opponent_archive import OpponentArchive
  state=payload['state']; config=old_identity['configuration']
  old_archive=OpponentArchive('historical',limit=config['archive_limit'],every=config['archive_every'],
      generation=state['generations'],current=state['policy'],archive=state['archive'],metadata=state['opponent_archive'])
  migrated_archive_metadata={**old_archive.state_dict(state['generations']),'strategy':'prioritized','payoffs':{}}
  OpponentArchive('prioritized',limit=config['archive_limit'],every=config['archive_every'],
      generation=state['generations'],current=state['policy'],archive=state['archive'],metadata=migrated_archive_metadata)
 prior_manifest_path=run_dir/'performance-upgrade.json'
 prior_manifest=_read_object(prior_manifest_path) if prior_manifest_path.exists() else None
 if prior_manifest is not None and prior_manifest.get('state')!='committed': raise UpgradeError('An earlier upgrade is incomplete')
 old_checkpoint_sha=sha256_file(checkpoint_path)
 transaction=hashlib.sha256(canonical({'old_checkpoint_sha256':old_checkpoint_sha,'new_identity':new_identity})).hexdigest()[:24]
 backup=run_dir/'performance-upgrades'/transaction
 if backup.exists(): raise UpgradeError('Upgrade transaction already exists; inspect it instead of retrying silently')
 backup.mkdir(parents=True)
 directory=os.open(backup.parent,os.O_RDONLY|os.O_DIRECTORY)
 try: os.fsync(directory)
 finally: os.close(directory)
 _copy_durable(checkpoint_path,backup/'original.soicp')
 if sha256_file(backup/'original.soicp')!=old_checkpoint_sha: raise UpgradeError('Immutable checkpoint backup differs')
 for name in ('identity.json','config.json','catalog.json','prepared.json','budget.json','status.json','deadline.json'):
  if (run_dir/name).is_file(): _copy_durable(run_dir/name,backup/name)
 _atomic_json(backup/'original-source-inventory.json',old_inventory)
 _atomic_json(backup/'new-source-inventory.json',current)
 if prior_manifest is not None: _atomic_json(backup/'previous-upgrade.json',prior_manifest)
 lineage={'schema':SCHEMA,'state':'prepared','created_wall':time.time(),'transaction':transaction,'campaign_id':budget.campaign_id,
          'old_identity':old_identity,'new_identity':new_identity,'new_identity_sha256':hashlib.sha256(canonical(new_identity)).hexdigest(),
          'old_checkpoint_sha256':old_checkpoint_sha,'source_changes':delta,'original_deadline':original_deadline,
          'original_checkpoint':str(backup/'original.soicp'),'configuration_unchanged':not configuration_changes,
          'configuration_changes':configuration_changes,
          'preserved':['policy','optimizer','archive','rng','counters','rolling_game_stats','next_engine_seed'],
          'previous_upgrade_sha256':hashlib.sha256(canonical(prior_manifest)).hexdigest() if prior_manifest else None,
          'budget_backup_is_forensic_only':True}
 if learning_rate_change:
  lineage['preserved'][1]='optimizer moments, steps and groups except explicitly approved learning rate'
  lineage['optimizer_learning_rate_change']={**learning_rate_change,'groups':len(migrated_optimizer['param_groups'])}
 _atomic_json(backup/'lineage.json',lineage);_atomic_json(prior_manifest_path,lineage)
 # Keep the original learned tensors and RNG. Adam is copied independently so
 # changing the approved group LR cannot mutate the preservation reference.
 original_state=payload['state'].copy();original_rng=payload['rng'];payload.pop('file_sha256',None);payload['identity']=new_identity;payload['identity_sha256']=hashlib.sha256(canonical(new_identity)).hexdigest()
 if configuration_changes: payload['state']['configuration']=dict(new_identity['configuration'])
 if learning_rate_change: payload['state']['optimizer']=migrated_optimizer
 if migrated_archive_metadata is not None: payload['state']['opponent_archive']=migrated_archive_metadata
 payload['state']['performance_upgrade']={'schema':SCHEMA,'transaction':transaction,'original_deadline':original_deadline,'previous_checkpoint_sha256':old_checkpoint_sha}
 candidate=backup/'migrated.soicp';new_checkpoint_sha=write_payload_atomic(candidate,payload)
 checked=load_checkpoint(candidate,expected_identity=new_identity,budget=budget)
 # No RNG capture or model/optimizer loading occurs in this metadata update.
 if not _same_state(original_rng,checked['rng']): raise UpgradeError('Checkpoint RNG values changed')
 for key,value in original_state.items():
  if key=='opponent_archive' and migrated_archive_metadata is not None:
   expected={**value,'strategy':'prioritized','payoffs':{}}
   if not _same_state(expected,checked['state'].get(key)): raise UpgradeError('Archive priority initialization changed retained history')
   continue
  if key=='optimizer' and learning_rate_change:
   _verify_optimizer_learning_rate(value,checked['state'].get(key),learning_rate_change['before'],learning_rate_change['after'])
   continue
  if key!='performance_upgrade' and not (key=='configuration' and configuration_changes) and not _same_state(value,checked['state'].get(key)): raise UpgradeError(f'Checkpoint learned state changed: {key}')
 if checked['state'].get('configuration')!=new_identity['configuration']: raise UpgradeError('Migrated configuration differs from the reviewed identity')
 lineage.update(new_checkpoint_sha256=new_checkpoint_sha)
 _atomic_json(backup/'lineage.json',lineage)
 os.replace(candidate,checkpoint_path)
 directory=os.open(run_dir,os.O_RDONLY|os.O_DIRECTORY)
 try: os.fsync(directory)
 finally: os.close(directory)
 if configuration_changes: _atomic_json(run_dir/'config.json',new_identity['configuration'])
 _atomic_json(run_dir/'identity.json',new_identity)
 lineage['state']='committed';lineage['committed_wall']=time.time()
 _atomic_json(backup/'lineage.json',lineage);_atomic_json(prior_manifest_path,lineage)
 return lineage

def main():
 parser=argparse.ArgumentParser(description=__doc__);sub=parser.add_subparsers(dest='command',required=True)
 snapshot=sub.add_parser('snapshot');snapshot.add_argument('--repo',type=Path,required=True);snapshot.add_argument('--output',type=Path,required=True)
 verify=sub.add_parser('verify');verify.add_argument('--old-identity',type=Path,required=True);verify.add_argument('--new-identity',type=Path,required=True);verify.add_argument('--old-inventory',type=Path,required=True);verify.add_argument('--new-inventory',type=Path,required=True);verify.add_argument('--approved-changes',type=Path,required=True)
 verify.add_argument('--approved-configuration-changes',type=Path)
 args=parser.parse_args()
 if args.command=='snapshot':
  value=source_inventory(args.repo);args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_bytes(canonical(value)+b'\n');print(json.dumps({'source_fingerprint':value['source_fingerprint'],'files':len(value['files']),'output':str(args.output)}))
 else:
  configuration_changes=_read_object(args.approved_configuration_changes) if args.approved_configuration_changes else None
  delta=validate_transition(*(_read_object(getattr(args,key)) for key in ('old_identity','new_identity','old_inventory','new_inventory','approved_changes')),approved_configuration_changes=configuration_changes);print(json.dumps({'valid':True,'reviewed_paths':list(delta)}))

if __name__=='__main__':main()
