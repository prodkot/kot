"""Real local VLESS chain; never creates a TUN or alters host routes."""
import argparse, sys, http.client, http.server, threading, subprocess, pathlib, time, json, socket
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--core', required=True, type=pathlib.Path, help='Path to the official sing-box 1.14.2 executable')
args = parser.parse_args()
ROOT=pathlib.Path(__file__).resolve().parents[1]
core=args.core.resolve()
generated=ROOT/'Tests/generated'
if not core.is_file():
    parser.error('sing-box executable not found')
if not all((generated/name).is_file() for name in ('fixture-client.json','fixture-server.json')):
    parser.error('Run dotnet run --project Tests/Kot.Tests.csproj -c Release -- integration first')
class Fixture(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        body=b'kot-real-vless-fixture-ok'
        self.send_response(200);self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
    def log_message(self,*args): pass
for path in generated.glob('*.json'):
    p=subprocess.run([str(core),'check','-c',str(path)],capture_output=True,text=True,timeout=20)
    assert p.returncode==0, (path.name,p.stderr)
print('PASS bundled core validates all generated protocol configurations')
# Loopback fixture needs no interface discovery; the host prohibits netlink.
fixture=json.loads((generated/'fixture-client.json').read_text());fixture['route']['auto_detect_interface']=False
(generated/'fixture-client-local.json').write_text(json.dumps(fixture))
server=http.server.ThreadingHTTPServer(('127.0.0.1',19443),Fixture)
threading.Thread(target=server.serve_forever,daemon=True).start()
processes=[]
try:
    for name in ('fixture-server','fixture-client-local'):
        processes.append(subprocess.Popen([str(core),'run','-c',str(generated/(name+'.json'))],stdout=subprocess.DEVNULL,stderr=subprocess.PIPE))
    for _ in range(50):
        for child in processes:
            if child.poll() is not None:
                error=child.stderr.read().decode()
                if 'netlink socket: operation not permitted' in error:
                    print('SKIP live core fixture: the Linux host denies netlink sockets; exit 77')
                    sys.exit(77)
                raise AssertionError(error)
        try:
            with socket.create_connection(('127.0.0.1',19442),.1): break
        except OSError: time.sleep(.1)
    else:
        raise AssertionError('Core did not open its local proxy within five seconds')
    # CONNECT forces an actual HTTP proxy hop; no urllib loopback proxy bypass.
    def via_proxy():
        import http.client
        conn=http.client.HTTPConnection('127.0.0.1',19442,timeout=4)
        conn.set_tunnel('127.0.0.1',19443);conn.request('GET','/');r=conn.getresponse();body=r.read();conn.close();return body
    assert via_proxy()==b'kot-real-vless-fixture-ok'
    print('PASS real HTTP CONNECT -> mixed inbound -> VLESS -> fixture web server')
    processes[0].terminate();processes[0].wait(timeout=5)
    rejected=False
    try: via_proxy()
    except (OSError, http.client.HTTPException): rejected=True
    assert rejected, 'Client fell back to direct when the chosen server died'
    print('PASS server failure does not fall back to direct traffic')
finally:
    for p in processes:
        if p.poll() is None:p.terminate()
    for p in processes:
        try:p.wait(timeout=5)
        except subprocess.TimeoutExpired:p.kill();p.wait()
    server.shutdown();server.server_close()
