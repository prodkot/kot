# Linux acceptance: retained kill switch and toggle

Дата проверки: 2026-10-24.

- Исходный пакет: `kot 0.8.0~linux0.3`, SHA-256
  `82d433b8075f2e81b12060b04324fbc965b5a7e6b4f1b083e31b162a0495c881`.
- Исправленный пакет: `kot 0.8.0~linux0.4`, SHA-256
  `0af673d0df6315c0ff521ec56e031c024f89bc0defe1123427f592c2cc382a1e`,
  собран из коммита `c57a8fb`.
- Ветка: `fix/linux-laptop-acceptance`.
- Стенд: Zorin OS 18.1, GNOME/Wayland, реальный профиль пользователя,
  установленный пакет `/usr/lib/kot`. Пароли и токены в отчёт не попадают.

## Дефект, воспроизведённый на 0.8.0~linux0.3

Сценарий: подключение с включённым kill switch, затем неудачное
переподключение (health-check принудительно сорван синтетическим routing-профилем).
После отказа состояние было `idle`, `killSwitchActive=true`, guard удержан,
`desired` оставался `true`. Затем отправлялся тот же запрос `toggle`, что и
кнопка «Подключить».

Результат: `FAIL`.

- toggle перешёл в `disconnecting`, а не в `connecting`;
- таблица `inet kot_guard` была удалена;
- файл-квитанция `~/.config/kot/guard-active` была удалена;
- snapshot после toggle: `killSwitchActive=false`, ошибка пустая.

То есть в состоянии «показан „Подключить“, но защита ещё удерживается»
нажатие кнопки выполняло отключение/явное восстановление вместо повторного
подключения. При кэшированной polkit-авторизации отдельный запрос пароля
может быть не виден, поэтому это опасно и как неожиданное снятие защиты.

## Исправление

`Linux/Backend.cs`, обработчик `toggle`:

- ветка отключения теперь выбирается только при `tunnel.State != "idle"`;
- при `state == "idle"` toggle всегда пытается подключиться, даже если
  `desired` остался `true` после неудачного reconnect. Удержанный guard при
  этом не снимается: `Connect()` либо переподключается, либо завершается
  ошибкой, оставляя защиту.

`Linux/Backend.cs`, `Settings` для `killSwitch=false`:

- убран безусловный повторный `pkexec network-helper.py recover`;
- `tunnel.Stop()` уже освобождает guard через helper, а при отсутствии helper
  сам выполняет явное восстановление. Лишний вызов давал второй запрос polkit
  и при отмене оставлял сохранённый профиль в несогласованном состоянии.

## Проверка на 0.8.0~linux0.4

Тот же сценарий на установленной исправленной копии: `PASS`.

- неудачное переподключение: `idle`, `killSwitchActive=true`, ошибка
  health-check, guard удержан;
- toggle: журнал `Состояние: connecting` → `Состояние: idle`, перехода
  `disconnecting` нет;
- таблица `inet kot_guard` и квитанция `guard-active` после toggle остались;
- явное восстановление в `finally`: helper `rc=0`, TUN/правила/таблица
  маршрутизации/guard удалены, DNS вернулся на физический интерфейс,
  прямой IPv4-выход восстановлен;
- профиль пользователя и выбранный сервер не изменены.

Регрессии на исправленной сборке:

- `LinuxTests/helper.py` — 8 тестов, OK;
- `LinuxTests/security.py` — PASS;
- `LinuxTests/process_tree.py` — PASS;
- `LinuxTests/Kot.LinuxTests.csproj` — все проверки PASS, включая реальный
  TUN health через локальный VLESS-сервер;
- GUI новой копии запускается, WebKit-окно отображается, закрытие завершает
  shell и backend.

## Ограничения и известные проблемы

Это не заявление об отсутствии всех уязвимостей.

- После прямого `pkexec /usr/lib/kot/network-helper.py recover` при уже
  завершённом клиенте файл `~/.config/kot/guard-active` может остаться.
  Следующий запуск покажет `killSwitchActive=true` при отсутствии реальной
  таблицы guard. В этой итерации не исправлено; сценарий восстановления
  удаляет квитанцию сам.
- Kill switch не переживает перезагрузку.
- Режим системного прокси GNOME не перехватывает произвольный UDP и
  приложения, игнорирующие прокси.
- Правила Windows `.exe` не имеют эквивалента в Linux и отклоняются.
- Вкладка Services в Linux отключена.
- Локальный deb не подписан издателем; это не канал подписанных обновлений.
- Совместная работа с активным UFW не проверена (`ufw` в тестовом стенде
  был неактивен).

## Ручное восстановление

```sh
/usr/lib/kot/Kot.Linux --restore-proxy
pkexec /usr/lib/kot/network-helper.py recover

ip link show kot-tun
ip rule show
ip route show table 47811
resolvectl status --no-pager | sed -n '1,20p'
curl -4 -sS -m 8 https://1.1.1.1/cdn-cgi/trace | grep '^ip='
```

Не выполнять `nft flush ruleset`, `ip rule flush`, `ufw reset` и не
перезаписывать `/etc/resolv.conf`. Не удалять чужие nftables-таблицы и
маршруты. Сырые логи, профиль, ключи и сетевые дампы в репозиторий не
публикуются.
