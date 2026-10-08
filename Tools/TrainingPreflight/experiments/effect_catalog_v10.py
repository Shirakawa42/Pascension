"""Static public semantic descriptors exported by the exact versioned game host."""
from functools import lru_cache
from pathlib import Path
import json
import subprocess
import numpy as np
from pipeline_bench import DOTNET

BINARY=Path(__file__).resolve().parent/'HostV10/bin/Release/net8.0/TrainingHostV10.dll'

@lru_cache(maxsize=1)
def effect_catalog():
    data=json.loads(subprocess.check_output([DOTNET,str(BINARY),'effects'],text=True,timeout=30))
    matrix=np.asarray(data['matrix'],dtype=np.float32)
    if matrix.shape!=(193,512) or not np.isfinite(matrix).all() or matrix[0].any():
        raise ValueError('Invalid public card-effect descriptor matrix')
    return data

def effect_matrix():
    return np.asarray(effect_catalog()['matrix'],dtype=np.float32)
