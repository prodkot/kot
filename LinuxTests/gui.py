#!/usr/bin/python3
"""Native GTK/WebKitGTK + real backend test, not a browser mock."""
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import threading

appdir = Path(sys.argv[1]).resolve()
evidence = Path(sys.argv[2]).resolve()
evidence.mkdir(parents=True, exist_ok=True)
isolation = Path(tempfile.mkdtemp(prefix='kot-gui-', dir='/tmp/opencode'))
for key in ('XDG_CONFIG_HOME', 'XDG_DATA_HOME', 'XDG_CACHE_HOME'):
    os.environ[key] = str(isolation / key)
spec = importlib.util.spec_from_file_location('kot_shell', appdir / 'shell.py')
shell = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shell)
from gi.repository import GLib, WebKit2, Gdk


class Test(shell.Application):
    def __init__(self):
        super().__init__()
        self.set_application_id('io.github.prodkot.kot.NativeTests')
        self.steps = []
        self.started = False
        self.result = 1
        GLib.timeout_add_seconds(35, self.timeout)

    def setup_tray(self):
        pass

    def deliver(self, message):
        value = super().deliver(message)
        if message.get('kind') == 'snapshot' and not self.started:
            self.started = True
            self.send_test(900001, 'importText', {'name': 'Нативный Linux', 'text': 'vless://11111111-1111-4111-8111-111111111111@127.0.0.1:19491?encryption=none#Linux-test'})
        if message.get('kind') == 'reply' and message.get('id', 0) >= 900001:
            if message['id'] == 900004:
                if message.get('ok') or 'убраны' not in message.get('error', ''):
                    self.fail('Removed service API was not rejected')
                else:
                    self.steps.append('removed service API rejected by actual Linux backend')
                    self.web.evaluate_javascript("window.__kotReceive({kind:'navigate',page:'services'})", -1, None, shell.URI, None, None, None)
                    GLib.timeout_add(1500, self.inspect)
            elif not message.get('ok'):
                self.fail('RPC failed: ' + str(message.get('error')))
            elif message['id'] == 900001:
                self.steps.append('real Core import + pinned Xray validation via native bridge')
                self.send_test(900002, 'settings', {'key': 'theme', 'value': 'light'})
            elif message['id'] == 900002:
                self.steps.append('theme persisted by real backend')
                # A forged bridge envelope has no authority, even at our URI.
                self.web.evaluate_javascript("window.webkit.messageHandlers.kot.postMessage(JSON.stringify({token:'wrong',request:{id:900003,action:'settings',data:{key:'theme',value:'dark'}}}))", -1, None, shell.URI, None, None, None)
                self.web.load_uri('https://example.invalid/denied')
                self.send_test(900004, 'serviceAction', {'service': 'telegram', 'operation': 'install'})
        return value

    def send_test(self, id, action, data):
        # Uses exactly the same JS bridge as the UI, not direct backend injection.
        self.web.evaluate_javascript('chrome.webview.postMessage(' + json.dumps({'id': id, 'action': action, 'data': data}) + ')', -1, None, shell.URI, None, None, None)

    def inspect(self):
        if self.web.get_uri() != shell.URI:
            self.fail('External navigation was not denied')
            return False
        self.steps.append('external navigation denied before loading; forged token rejected')
        script = '''JSON.stringify({ready:!document.body.classList.contains('booting'),
          light:document.body.classList.contains('theme-light'),
           version:document.getElementById('version').textContent,
           noServices:!document.querySelector('[data-page="services"],#page-services,#serviceSettingsDialog,#zapretTestDialog,#serviceRemoveDialog,#serviceSecretDialog')&&typeof window.KotServices==='undefined'&&document.querySelector('.main').dataset.view!=='services',
          selected:document.getElementById('selectedName').textContent})'''
        self.web.evaluate_javascript(script, -1, None, shell.URI, None, self.inspected, None)
        return False

    def inspected(self, view, result, user):
        try:
            dom = json.loads(view.evaluate_javascript_finish(result).to_string())
            if not dom['ready'] or not dom['light'] or not dom['noServices'] or dom['selected'] != 'Linux-test':
                raise AssertionError(str(dom))
            self.steps.append('native DOM rendered real snapshot and Russian UI')
            self.steps.append('services tab/page/dialogs/module absent; service navigation ignored')
            backend_pid = self.backend.pid
            def activate():
                import subprocess
                try:
                    # Registration/activation is synchronous D-Bus: do not block
                    # the primary GTK loop while the remote caller waits for it.
                    subprocess.run(['/usr/bin/python3', '-c', "from gi.repository import Gio; a=Gio.Application(application_id='io.github.prodkot.kot.NativeTests'); a.register(None); assert a.get_is_remote(); a.activate()"], check=True, timeout=5)
                    GLib.idle_add(self.activated, dom, backend_pid)
                except Exception as error:
                    GLib.idle_add(self.fail, type(error).__name__)
            threading.Thread(target=activate, daemon=True).start()
        except Exception as error:
            self.fail(type(error).__name__ + ': ' + str(error))

    def activated(self, dom, backend_pid):
        if self.backend.pid != backend_pid:
            self.fail('second instance replaced the backend')
            return False
        self.steps.append('second GApplication activation forwards to existing instance')
        self.web.get_snapshot(WebKit2.SnapshotRegion.VISIBLE, WebKit2.SnapshotOptions.NONE, None, self.snapshot, dom)
        return False

    def snapshot(self, view, result, dom):
        try:
            surface = view.get_snapshot_finish(result)
            surface.write_to_png(str(evidence / 'native-gtk.png'))
            self.steps.append('native WebKitGTK screenshot captured')
            (evidence / 'native-gui.json').write_text(json.dumps({'result': 'PASS', 'display': Gdk.Display.get_default().__gtype__.name, 'steps': self.steps, 'dom': dom}, ensure_ascii=False, indent=2))
            print('PASS native GTK/WebKitGTK → bridge → .NET Core → real Xray validation → saved profile → DOM')
            self.result = 0
            self.quit_app()
        except Exception as error:
            self.fail(type(error).__name__ + ': ' + str(error))

    def fail(self, error):
        (evidence / 'native-gui.json').write_text(json.dumps({'result': 'FAIL', 'error': error, 'steps': self.steps}))
        print('FAIL native GUI: ' + error, file=sys.stderr)
        self.quit_app()

    def timeout(self):
        self.fail('35s deadline')
        return False


test = Test()
test.run([sys.argv[0]])
sys.exit(test.result)
