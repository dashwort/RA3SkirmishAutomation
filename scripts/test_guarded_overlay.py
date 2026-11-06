from pathlib import Path
import sys
import uuid
sys.path.insert(0,str(Path(__file__).parent))
from build_guarded_overlay import build
root=Path(__file__).resolve().parents[1]
old=root/'artifacts/AutoSkirmish/navigation-only/Epic War 5.2-AutoSkirmish-2026-09-26-v36-navigation.big'
apt=root/'work/menu-fix-tools/candidate-main-menu.apt'
out=root/'work/menu-fix-tools/candidate'/('test-'+uuid.uuid4().hex+'.big')
try:
    build(old,apt,out)
    data=out.read_bytes()
    assert b'epicWarAutoNavigated' in data
    assert bytes.fromhex('5A5AB901B26559175917') not in data
    print('Guarded overlay structurally valid')
finally:
    out.unlink(missing_ok=True)
