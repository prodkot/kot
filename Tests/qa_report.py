"""Validate the delivered report's strict allowlist, not just a secret regex."""
import json
import re
import sys
import zipfile

with zipfile.ZipFile(sys.argv[1]) as archive:
    assert archive.namelist() == ['report.json'], 'Unexpected report attachment'
    assert archive.getinfo('report.json').file_size < 32768
    data = json.loads(archive.read('report.json'))
assert set(data) <= {'schema','commit','version','os','checks','manual','startup','coreSha256','clientSha256'}
assert data['schema'] == 1 and re.fullmatch('[0-9a-f]{40}', data['commit'])
assert re.fullmatch(r'\d+(\.\d+){1,3}', data['os'])
assert data['version'] == '0.8.0'
for name in ('coreSha256','clientSha256'):
    assert re.fullmatch('[0-9a-f]{64}', data[name])
allowed = {'exact-installed-payload','xray-version','webview2-runtime','startup-record','xray-config-acceptance',
           'real-vless-traffic','xray-stdin-acceptance','tun-ipv6-dns-sleep-provider','automatic-checks'}
for item in data['checks']:
    assert set(item) == {'name','status','detail'} and item['name'] in allowed
    assert item['status'] in {'PASS','FAIL','SKIP','NOT_TESTED'}
    assert re.fullmatch(r'[A-Za-z0-9 .;,=+\-]*',item['detail']) and len(item['detail']) < 300
required = {'exact-installed-payload','xray-version','webview2-runtime','xray-config-acceptance','xray-stdin-acceptance','real-vless-traffic'}
assert all(x['status'] == 'PASS' for x in data['checks'] if x['name'] in required)
assert {x['name'] for x in data['checks']} >= required
assert data['manual'] == [], 'CI must not claim manual results'
for item in data['startup']:
    assert set(item) == {'stage','type','hresult'}
    assert item['stage'] in {'startup','interface'} and type(item['hresult']) is int
    assert re.fullmatch(r'(System|Microsoft|Kot)\.[A-Za-z0-9_.+`]+Exception',item['type'])
print('PASS QA report: only allowlisted data; exact candidate and real local traffic checked; manual tests not claimed')
