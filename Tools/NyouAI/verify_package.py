"""Verify both independently addressable AI resource sets in a built Unity player."""
import argparse,json,sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'Tools/TrainingPreflight'))
import verify_packaged_ai as original
original.RESOURCES.update({'ai/nyou-policy':'nyou-policy.bytes','ai/nyou-policy-metadata':'nyou-policy-metadata.json','ai/nyou-search-settings':'nyou-search-settings.json'})
p=argparse.ArgumentParser();p.add_argument('player_data',type=Path);p.add_argument('report',type=Path);a=p.parse_args()
result=original.verify(a.player_data/'globalgamemanagers',ROOT/'Assets/Resources/AI')
a.report.write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps(dict(passed=result['passed'],resources=len(result['checks']))))
raise SystemExit(0 if result['passed'] else 1)
