"""Explicit user-authorized continuation of the same ledger through a wall deadline.

No charged time/session/counter is reset. Only the allocation ceiling changes;
each new session is also capped at the authorized absolute wall deadline.
"""
import copy
import datetime
import fcntl
import hashlib
import json
import os
from pathlib import Path
import time
import campaign_state as persistence

ROOT=Path('/home/lva/.local/share/shards-training/2026-09-26')
RECEIPT=ROOT/'overnight-authorization-v10.json'
DEADLINE='2026-09-27T11:00:00+02:00'
ORIGINAL=persistence.CampaignBudget
_installed=False

def authorization():
    data=json.loads(RECEIPT.read_text())
    if (data.get('schema')!='shards-explicit-overnight-extension-v1' or data.get('deadline')!=DEADLINE
        or not 43200 < data['new_limit_seconds'] <= 24*3600
        or data['deadline_wall']!=datetime.datetime.fromisoformat(DEADLINE).timestamp()):
        raise RuntimeError('Invalid explicit overnight authorization')
    return data

def authorize_extension():
    ledger=ROOT/'budget.json'
    with Path(str(ledger)+'.lock').open('a') as lock:
        fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
        original=ledger.read_bytes();data=json.loads(original)
        if RECEIPT.exists():
            receipt=authorization()
            if data['limit_seconds']!=receipt['new_limit_seconds']:raise RuntimeError('Partial extension; inspect before continuing')
            return receipt
        if data['active'] is not None or data['limit_seconds']!=43200:
            raise RuntimeError('Extension requires cleanly paused original allocation')
        check=ORIGINAL(ledger);check._data=copy.deepcopy(data);check._validate()
        deadline=datetime.datetime.fromisoformat(DEADLINE).timestamp();now=time.time()
        if deadline<=now:raise RuntimeError('Authorized deadline already passed')
        limit=data['charged_seconds']+deadline-now
        if not 43200<limit<=86400:raise RuntimeError('Unexpected extension size')
        receipt=dict(schema='shards-explicit-overnight-extension-v1',deadline=DEADLINE,deadline_wall=deadline,
            authorized_wall=now,campaign_id=data['campaign_id'],old_limit_seconds=43200,new_limit_seconds=limit,
            charged_seconds_at_extension=data['charged_seconds'],old_ledger_sha256=hashlib.sha256(original).hexdigest(),
            user_instruction='Training can then be extended the whole night until about 11 am.',
            timezone='Europe/Paris',scope='Same campaign and all historical charges; absolute deadline also enforced on every session')
        persistence._atomic_write(ROOT/'budget-before-overnight-extension-v10.json',original)
        persistence._atomic_write(RECEIPT,json.dumps(receipt,indent=2).encode())
        data['limit_seconds']=limit;data['revision']+=1
        persistence._atomic_write(ledger,persistence._canonical(data))
        return receipt

class ExtendedBudget(ORIGINAL):
    def __init__(self,path,limit_seconds=None,*,clock=None):
        receipt=authorization()
        if Path(path).resolve()!=ROOT/'budget.json':raise RuntimeError('Extended allocation only applies to its authorized ledger')
        if limit_seconds is not None and limit_seconds!=receipt['new_limit_seconds']:raise RuntimeError('Allocation mismatch')
        self.authorization=receipt
        super().__init__(path,limit_seconds=receipt['new_limit_seconds'],clock=clock)

    def _validate(self):
        super()._validate()
        if self._data['campaign_id']!=self.authorization['campaign_id']:raise RuntimeError('Different campaign')
        if self._data['charged_seconds']<self.authorization['charged_seconds_at_extension']:raise RuntimeError('Charged time rolled back')

    def start_session(self,label,requested_seconds=None,stop_buffer_seconds=30):
        remaining_wall=self.authorization['deadline_wall']-self.clock.time()
        if remaining_wall<=stop_buffer_seconds+1:raise persistence.BudgetExceeded('Authorized11am deadline reached')
        requested=min(self.remaining_seconds if requested_seconds is None else requested_seconds,remaining_wall)
        return super().start_session(label,requested,stop_buffer_seconds)

def install():
    global _installed
    if _installed:return
    receipt=authorization()
    persistence.MAX_CAMPAIGN_SECONDS=receipt['new_limit_seconds']
    persistence.CampaignBudget=ExtendedBudget
    import train_campaign
    train_campaign.CampaignBudget=ExtendedBudget
    _installed=True
