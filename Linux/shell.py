#!/usr/bin/python3
"""GTK/WebKitGTK desktop shell. All application logic lives in Kot.Linux.

The only web-accessible bridge is injected into our local main document. No
external page, iframe, download or permission prompt is admitted to this view.
"""
import base64
import json
import mimetypes
import os
from pathlib import Path
import secrets
import signal
import subprocess
import sys
import threading
import gi
gi.require_version('Gtk', '3.0')
gi.require_version('WebKit2', '4.1')
from gi.repository import Gtk, Gio, GLib, WebKit2, Gdk, GdkPixbuf

BASE = Path(__file__).resolve().parent
URI = 'kot://app/index.html'


class Application(Gtk.Application):
    def __init__(self):
        super().__init__(application_id='io.github.prodkot.kot', flags=Gio.ApplicationFlags.FLAGS_NONE)
        self.window = None
        self.backend = None
        self.tray = None
        self.model = {}
        self.token = secrets.token_hex(32)
        self.closing = False
        self.write_lock = threading.Lock()

    def do_activate(self):
        if self.window:
            self.window.show_all()
            self.window.present()
            return
        self.window = Gtk.ApplicationWindow(application=self, title='kot. — Linux preview')
        self.window.set_default_size(960, 680)
        self.window.set_size_request(800, 600)
        self.window.set_icon_name('io.github.prodkot.kot')
        self.window.connect('delete-event', self.close_window)
        self.manager = WebKit2.UserContentManager()
        self.manager.register_script_message_handler('kot')
        self.manager.connect('script-message-received::kot', self.message)
        # Closure token never exposed to subframes; a fresh view gets a fresh token.
        bridge = '''(() => {
          if (window !== window.top || location.href !== %s) return;
          const token=%s, listeners=new Set();
          window.chrome={webview:{postMessage:request=>window.webkit.messageHandlers.kot.postMessage(JSON.stringify({token,request})),
            addEventListener:(type,callback)=>{if(type==='message')listeners.add(callback);},
            removeEventListener:(type,callback)=>listeners.delete(callback)}};
          window.__kotReceive=message=>{for(const callback of listeners)callback({data:message});};
          window.kotStartup={theme:'dark',spin:false};
          window.kotCapabilities=Object.freeze({services:false});
        })();''' % (json.dumps(URI), json.dumps(self.token))
        self.manager.add_script(WebKit2.UserScript.new(bridge, WebKit2.UserContentInjectedFrames.TOP_FRAME,
                                WebKit2.UserScriptInjectionTime.START, [URI], None))
        # Use GNOME's native titlebar for correct Wayland/X11 move/resize semantics.
        self.manager.add_style_sheet(WebKit2.UserStyleSheet.new('.chrome,.resize-edge{display:none!important}',
                                    WebKit2.UserContentInjectedFrames.TOP_FRAME, WebKit2.UserStyleLevel.USER, None, None))
        context = WebKit2.WebContext.new_ephemeral()
        context.register_uri_scheme('kot', self.resource)
        security = context.get_security_manager()
        security.register_uri_scheme_as_local('kot')
        security.register_uri_scheme_as_secure('kot')
        self.web = WebKit2.WebView(web_context=context, user_content_manager=self.manager)
        settings = self.web.get_settings()
        settings.set_enable_developer_extras(False)
        settings.set_enable_html5_database(False)
        settings.set_enable_html5_local_storage(False)
        settings.set_enable_page_cache(False)
        self.web.connect('decide-policy', self.policy)
        self.web.connect('permission-request', lambda view, request: request.deny() or True)
        self.web.connect('create', lambda *args: None)
        self.web.connect('context-menu', lambda *args: True)
        self.web.connect('web-process-terminated', self.web_crashed)
        context.connect('download-started', lambda context, download: download.cancel())
        self.window.add(self.web)
        self.setup_tray()
        self.window.show_all()  # no tray host: always retain an accessible window
        self.backend = subprocess.Popen([str(BASE / 'Kot.Linux'), '--backend'], stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                        text=True, encoding='utf-8', bufsize=1, start_new_session=True)
        threading.Thread(target=self.read_backend, daemon=True).start()
        threading.Thread(target=self.read_errors, daemon=True).start()
        self.web.load_uri(URI)

    def resource(self, request):
        from urllib.parse import urlsplit, unquote
        parsed = urlsplit(request.get_uri())
        root = BASE / 'ui'
        path = (root / unquote(parsed.path).lstrip('/')).resolve()
        if parsed.netloc != 'app' or parsed.query or parsed.fragment or not path.is_relative_to(root.resolve()) or not path.is_file():
            request.finish_error(GLib.Error('Resource denied'))
            return
        data = path.read_bytes()
        if path == (root / 'index.html').resolve():
            html = data.decode('utf-8')
            html = html.replace('Запуск с Windows', 'Запуск с GNOME').replace('прокси Windows', 'прокси GNOME')
            html = html.replace('DNS Windows проверяется отдельно', 'Системный DNS Linux требует отдельной проверки')
            html = html.replace('Клиент для Windows.', 'Клиент для Linux (предварительная версия).')
            html = html.replace('Имена .exe без пути, по одному в строке.', 'Правила приложений Windows .exe в Linux не применяются; Linux-политика ещё не реализована.')
            html = html.replace('<script src="services.js"></script>', '')
            data = html.encode('utf-8')
        request.finish(Gio.MemoryInputStream.new_from_bytes(GLib.Bytes.new(data)), len(data),
                       mimetypes.guess_type(str(path))[0] or 'application/octet-stream')

    def policy(self, web, decision, kind):
        if kind in (WebKit2.PolicyDecisionType.NAVIGATION_ACTION, WebKit2.PolicyDecisionType.NEW_WINDOW_ACTION):
            action = decision.get_navigation_action()
            if kind == WebKit2.PolicyDecisionType.NEW_WINDOW_ACTION or action.get_request().get_uri() != URI:
                decision.ignore()
                return True
        return False

    def reply(self, id, data=None, error=None):
        self.deliver({'kind': 'reply', 'id': id, 'ok': error is None, 'data': data, 'error': error})

    def message(self, manager, result):
        id = 0
        try:
            if self.web.get_uri() != URI:
                return
            raw = result.get_js_value().to_string()
            if len(raw) > 300_000:
                raise ValueError('Слишком большой запрос интерфейса.')
            envelope = json.loads(raw)
            if not secrets.compare_digest(envelope.get('token', ''), self.token):
                return
            request = envelope['request']
            if not isinstance(request, dict) or set(request) != {'id', 'action', 'data'} or type(request['id']) is not int or not isinstance(request['data'], dict):
                raise ValueError('Неверный запрос интерфейса.')
            id, action, data = request['id'], request['action'], request['data']
            if not isinstance(action, str) or len(action) > 64:
                raise ValueError('Неверная команда.')
            if action in ('drag', 'resize'):
                self.reply(id)
                return
            if action in ('close', 'minimize', 'maximize'):
                if action == 'close': self.close_window()
                elif action == 'minimize': self.window.iconify()
                else: self.window.unmaximize() if self.window.is_maximized() else self.window.maximize()
                self.reply(id)
                return
            if action == 'importServersFile':
                path = self.dialog(False, 'Импорт серверов')
                if path is None:
                    self.reply(id)
                    return
                if path.stat().st_size > 4 * 1024 * 1024:
                    raise ValueError('Файл больше 4 МиБ.')
                request['action'] = 'importText'
                request['data'] = {'text': path.read_text(encoding='utf-8-sig'), 'name': data.get('name') or 'Локальные серверы'}
            elif action == 'backupImport':
                path = self.dialog(False, 'Восстановить .kotbackup')
                if path is None:
                    self.reply(id)
                    return
                if path.stat().st_size > 21 * 1024 * 1024:
                    raise ValueError('Файл слишком большой.')
                request['data']['bytes'] = base64.b64encode(path.read_bytes()).decode()
            elif action in ('readQrFile', 'readQrClipboard'):
                if action == 'readQrFile':
                    path = self.dialog(False, 'Открыть изображение QR')
                    if path is None:
                        self.reply(id)
                        return
                    if path.stat().st_size > 16 * 1024 * 1024:
                        raise ValueError('Картинка больше 16 МиБ.')
                    info = GdkPixbuf.Pixbuf.get_file_info(str(path))
                    if info is None or info.width < 1 or info.height < 1 or info.width * info.height > 4_000_000:
                        raise ValueError('Картинка больше 4 млн пикселей или формат не поддерживается.')
                    image = GdkPixbuf.Pixbuf.new_from_file(str(path))
                else:
                    image = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD).wait_for_image()
                if image is None or image.get_width() * image.get_height() > 4_000_000:
                    raise ValueError('Нет изображения или оно больше 4 млн пикселей. Обрежьте QR перед импортом.')
                pixels = image.get_pixels()
                width, height, stride, channels = image.get_width(), image.get_height(), image.get_rowstride(), image.get_n_channels()
                rgb = bytes(pixels[y * stride + x * channels + c] for y in range(height) for x in range(width) for c in range(3))
                request['action'] = 'readQrPixels'
                request['data'] = {'rgb': base64.b64encode(rgb).decode(), 'width': width, 'height': height}
            elif action in ('readQrPixels', 'shutdown'):
                raise ValueError('Внутренняя команда оболочки.')
            self.write(request)
        except Exception as error:
            self.reply(id, error=str(error) if isinstance(error, ValueError) else 'Ошибка оболочки: ' + type(error).__name__)

    def write(self, request):
        if self.backend is None or self.backend.poll() is not None:
            raise ValueError('Backend недоступен. Перезапустите приложение.')
        with self.write_lock:
            self.backend.stdin.write(json.dumps(request, ensure_ascii=True, separators=(',', ':')) + '\n')
            self.backend.stdin.flush()

    def read_backend(self):
        for line in self.backend.stdout:
            if len(line) > 32 * 1024 * 1024:
                break
            try:
                message = json.loads(line)
                GLib.idle_add(self.deliver, message)
            except (ValueError, TypeError):
                break
        GLib.idle_add(self.backend_stopped)

    def read_errors(self):
        # Backend only emits sanitized startup messages, never raw core output.
        self.last_error = ''
        for line in self.backend.stderr:
            self.last_error = (self.last_error + line)[-4000:]

    def deliver(self, message):
        if self.closing:
            return False
        if message.get('kind') == 'snapshot':
            self.model = message['data']
        data = message.get('data')
        if message.get('kind') == 'reply' and isinstance(data, dict) and 'native' in data:
            try:
                self.native(data)
                message['data'] = None
            except Exception as error:
                message.update(ok=False, error='Операция ОС не завершена: ' + type(error).__name__)
        if self.web.get_uri() == URI:
            self.web.evaluate_javascript('window.__kotReceive?.(' + json.dumps(message, ensure_ascii=True) + ')', -1, None, URI, None, None, None)
        return False

    def native(self, data):
        kind = data['native']
        clipboard = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
        if kind == 'copy':
            clipboard.set_text(data['text'], -1)
            clipboard.store()
        elif kind == 'save':
            path = self.dialog(True, 'Сохранить файл', data['name'])
            if path is not None:
                self.atomic(path, base64.b64decode(data['bytes'], validate=True))
        elif kind in ('copyImage', 'saveImage'):
            loader = GdkPixbuf.PixbufLoader.new_with_type('svg')
            loader.write(base64.b64decode(data['image'].split(',', 1)[1], validate=True))
            loader.close()
            image = loader.get_pixbuf()
            if kind == 'copyImage':
                clipboard.set_image(image)
                clipboard.store()
            else:
                path = self.dialog(True, 'Сохранить QR', data['name'])
                if path is not None:
                    ok, pixels = image.save_to_bufferv('png', [], [])
                    if ok: self.atomic(path, pixels)
        else:
            raise ValueError('Unknown native command')

    @staticmethod
    def atomic(path, data):
        temporary = path.with_name('.' + path.name + '.' + secrets.token_hex(8))
        try:
            fd = os.open(temporary, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
            with os.fdopen(fd, 'wb') as stream:
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, path)
        finally:
            temporary.unlink(missing_ok=True)

    def dialog(self, save, title, name=''):
        dialog = Gtk.FileChooserNative.new(title, self.window, Gtk.FileChooserAction.SAVE if save else Gtk.FileChooserAction.OPEN,
                                           'Сохранить' if save else 'Открыть', 'Отмена')
        if save:
            dialog.set_current_name(name)
            dialog.set_do_overwrite_confirmation(True)
        try:
            return Path(dialog.get_filename()) if dialog.run() == Gtk.ResponseType.ACCEPT else None
        finally:
            dialog.destroy()

    def setup_tray(self):
        try:
            gi.require_version('AyatanaAppIndicator3', '0.1')
            from gi.repository import AyatanaAppIndicator3 as Indicator
            self.tray = Indicator.Indicator.new('kot', 'io.github.prodkot.kot', Indicator.IndicatorCategory.APPLICATION_STATUS)
            menu = Gtk.Menu()
            for label, callback in [('Открыть kot.', lambda: self.do_activate()), ('Подключить / отключить', lambda: self.write({'id': -1, 'action': 'toggle', 'data': {}})), ('Выход', self.quit_app)]:
                item = Gtk.MenuItem(label=label)
                item.connect('activate', lambda item, fn=callback: fn())
                menu.append(item)
            menu.show_all()
            self.tray.set_menu(menu)
            self.tray.set_status(Indicator.IndicatorStatus.ACTIVE)
        except (ValueError, ImportError):
            self.tray = None

    def close_window(self, *args):
        # An indicator object does not prove GNOME has a tray host. Minimize, never
        # hide irretrievably. GNOME Overview/taskbar always provides a way back.
        if self.model.get('settings', {}).get('tray'):
            self.window.iconify()
        else:
            self.quit_app()
        return True

    def web_crashed(self, *args):
        self.web.load_uri(URI)  # core and backend remain alive

    def backend_stopped(self):
        if not self.closing:
            dialog = Gtk.MessageDialog(transient_for=self.window, modal=True, message_type=Gtk.MessageType.ERROR,
                                       buttons=Gtk.ButtonsType.CLOSE, text='kot. backend остановлен')
            dialog.format_secondary_text(getattr(self, 'last_error', '') or 'Перезапустите приложение. Сохранённый профиль не удалён.')
            dialog.run()
            dialog.destroy()
            self.quit_app()
        return False

    def quit_app(self):
        if self.closing:
            return
        self.closing = True
        if self.backend and self.backend.poll() is None:
            self.write({'id': -2, 'action': 'shutdown', 'data': {}})
            self.backend.stdin.close()
            def wait():
                try:
                    self.backend.wait(timeout=35)
                except subprocess.TimeoutExpired:
                    # Only our exact process group, never killall/pkill.
                    os.killpg(self.backend.pid, signal.SIGTERM)
                GLib.idle_add(self.quit)
            threading.Thread(target=wait, daemon=True).start()
        else:
            self.quit()


def main():
    if os.geteuid() == 0:
        raise SystemExit('Не запускайте GUI от root.')
    os.umask(0o077)
    app = Application()
    GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, signal.SIGTERM, lambda: app.quit_app() or False)
    GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, signal.SIGINT, lambda: app.quit_app() or False)
    # --startup is a launcher hint, not a GTK option. Activation still brings the
    # existing single instance forward.
    return app.run([sys.argv[0]])


if __name__ == '__main__':
    sys.exit(main())
