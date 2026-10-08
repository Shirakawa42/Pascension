"""Choose one hardware thread per physical core before using sibling threads."""
from pathlib import Path
import os

def select_cpus(allowed, count=6, topology=Path('/sys/devices/system/cpu')):
    cores={}
    for cpu in sorted(allowed):
        root=Path(topology)/f'cpu{cpu}'/'topology'
        try:key=(int((root/'physical_package_id').read_text()),int((root/'core_id').read_text()))
        except (OSError,ValueError):key=(0,cpu)
        cores.setdefault(key,[]).append(cpu)
    selected=[values[0] for values in cores.values()][:count]
    if len(selected)<min(count,len(allowed)):
        selected.extend(cpu for cpu in sorted(allowed) if cpu not in selected)
    return selected[:count]

if __name__=='__main__':
    import argparse,json
    p=argparse.ArgumentParser();p.add_argument('--pid',type=int,required=True);a=p.parse_args()
    actual=sorted(os.sched_getaffinity(a.pid));expected=select_cpus(os.sched_getaffinity(0),len(actual));keys=lambda cpus:{((Path('/sys/devices/system/cpu')/f'cpu{x}'/'topology/physical_package_id').read_text().strip(),(Path('/sys/devices/system/cpu')/f'cpu{x}'/'topology/core_id').read_text().strip()) for x in cpus}
    value=dict(actual_cpus=actual,actual_physical_cores=len(keys(actual)),possible_physical_cores=len(keys(expected)));print(json.dumps(value));assert value['actual_physical_cores']==value['possible_physical_cores'],'Benchmark is unnecessarily sharing physical cores'
