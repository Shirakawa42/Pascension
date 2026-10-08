"""Retain each tested Rez runtime so later iterations cannot alter its identity."""
from pathlib import Path
import argparse
import shutil

def freeze(destination):
    source=Path(__file__).resolve().parents[2]
    destination=Path(destination);destination.mkdir(parents=True,exist_ok=False)
    for folder in ('Tools/RezTrainingHost','Assets/Scripts/Core','Assets/Scripts/Shards/Engine',
                   'Assets/Scripts/Shards/Content','Tools/TrainingPreflight/experiments/HostV10'):
        # Host binary is retained verbatim; obj output does not belong to identity.
        shutil.copytree(source/folder,destination/folder,ignore=shutil.ignore_patterns('obj'))
    for name in ('rez_runtime.py','rez_entry.py','rez_policy.py','rez_tactical_learning.py'):
        target=destination/'Tools/TrainingPreflight'/name;target.parent.mkdir(parents=True,exist_ok=True)
        shutil.copy2(source/'Tools/TrainingPreflight'/name,target)
    return destination

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('destination',type=Path);a=p.parse_args();print(freeze(a.destination))
