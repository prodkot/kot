"""Build identifiers cannot collide across channels, runs or retries."""
import datetime
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location('build_identity', Path(__file__).resolve().parents[1] / 'Release/build-identity.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
base = dict(GITHUB_ACTIONS='true', GITHUB_WORKFLOW='Windows development build', GITHUB_RUN_NUMBER='123', GITHUB_RUN_ATTEMPT='1')
assert module.build_id(base) == 'dev.123.1'
assert len({module.build_id(base), module.build_id({**base, 'GITHUB_RUN_ATTEMPT': '2'}),
            module.build_id({**base, 'GITHUB_RUN_NUMBER': '124'}),
            module.build_id({**base, 'GITHUB_WORKFLOW': 'Windows release'})}) == 4
for key, value in [('GITHUB_RUN_NUMBER', ''), ('GITHUB_RUN_ATTEMPT', '0'), ('GITHUB_RUN_NUMBER', '1;bad'),
                   ('GITHUB_RUN_NUMBER', '01'), ('GITHUB_RUN_ATTEMPT', '1000000000'), ('GITHUB_WORKFLOW', 'Unknown')]:
    try:
        module.build_id({**base, key: value})
        raise AssertionError((key, value))
    except ValueError:
        pass
now = datetime.datetime(2026, 11, 9, 14, 0, 0, tzinfo=datetime.timezone.utc)
assert module.build_id({}, now, '1234abcd') == 'local.20261109140000.1234abcd'
assert module.build_id({}, now) != module.build_id({}, now)
print('PASS build identity: channels, runs, retries, invalid CI metadata and simultaneous local packages')
