# Сборка

Для Windows-клиента нужны Windows x64, PowerShell 7, Python 3.10+ и .NET SDK из `global.json` (10.0.401). Для Setup дополнительно нужен NSIS 3. Сборка скачивает компоненты с GitHub, Microsoft и NuGet, поэтому нужен интернет.

Из корня репозитория:

```powershell
pwsh ./build.ps1
```

Готовая программа появится в новой папке `publish-…`. Скрипт сначала проверяет согласованность репозитория, затем собирает клиент, проверяет хеш архива sing-box и подпись установщика WebView2. Ранее созданная папка не используется повторно. Профили и настройки пользователей не входят в сборку.

## Структура

- `Core/`: подписки, конфигурации sing-box, пинг, резервные копии и проверка обновлений.
- `Windows/`: окно, запуск ядра, хранение профиля и интеграция с Windows.
- `Windows/ui/`: HTML, JavaScript, флаги и общий локальный шрифт.
- `Tests/`: основная логика, интерфейс и проверка состава репозитория.
- `NativeTests/`: запуск и завершение процессов на Windows.
- `WindowTests/`: окно, трей, реальные настройки прокси и WFP-фильтры.
- `Release/`, `Installer/`: подпись пакетов, публикация и установщик.
- `site/`: статический сайт и изолированное интерактивное демо клиента.
- `licenses/`, `upstream/`: обязательные уведомления и соответствующие исходники sing-box.

## Проверки

Согласованность версий, локальных ссылок, лицензий и GitHub Actions:

```powershell
python Tests/repository.py
```

Основная логика, также работает на Linux:

```powershell
dotnet run --project Tests/Kot.Tests.csproj -c Release
```

Браузерные проверки используют ту же версию Playwright, что и CI. Установи зависимости в отдельное окружение:

```powershell
python -m venv .tools/venv
./.tools/venv/Scripts/python.exe -m pip install -r requirements-dev.txt
./.tools/venv/Scripts/python.exe -m playwright install chromium
./.tools/venv/Scripts/python.exe Tests/ui.py
./.tools/venv/Scripts/python.exe Tests/flags_ui.py
./.tools/venv/Scripts/python.exe Tests/subscriptions_ui.py
./.tools/venv/Scripts/python.exe site/check.py
./.tools/venv/Scripts/python.exe site/demo_browser_check.py
```

На Linux исполняемый файл окружения находится в `.tools/venv/bin/python`. Установка системных библиотек Chromium описана в [Playwright](https://playwright.dev/python/docs/browsers#install-system-dependencies).

Нативные проверки выполняются на Windows:

```powershell
dotnet publish NativeTests/Kot.NativeTests.csproj -c Release -r win-x64 --self-contained false -o artifacts/native-tests
./artifacts/native-tests/Kot.NativeTests.exe
dotnet publish WindowTests/Kot.WindowTests.csproj -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=false -o artifacts/window-tests
./artifacts/window-tests/Kot.WindowTests.exe artifacts/window-ui
```

Для реальной локальной VLESS-цепочки скачай официальный sing-box 1.14.2 для своей платформы и проверь его контрольную сумму. Тест не создаёт TUN и не меняет маршруты компьютера:

```powershell
dotnet run --project Tests/Kot.Tests.csproj -c Release -- integration
python Tests/integration.py --core 'C:\tools\sing-box.exe'
```

Порты 19441–19443 должны быть свободны. На Linux-хосте, запрещающем netlink, live-проверка может завершиться кодом 77; это пропуск, а не успешная проверка соединения. В CI цепочка должна пройти полностью.

## Выпуск

Полный порядок находится в [Release/GITHUB.md](../Release/GITHUB.md). Workflow выпуска проверяет подписи пакета и Setup, реальные TUN/WFP-фильтры, установку, повторную установку поверх старых документов и удаление. Он сохраняет пользовательские файлы и обязательные лицензии.

Эти проверки не заменяют ручные испытания на разных Windows-компьютерах: смена сети, сон, авария питания, DNS/IPv6 и восстановление после обновления. Известные ограничения перечислены в [SECURITY.md](SECURITY.md).

Не меняй Setup после подписи. Внутри него находится проверяемый пакет обновления; отдельно он не публикуется. Приватный ключ издателя хранится вне исходников. Адрес обновлений закреплён в `Core/RemoteUpdates.cs`.

[На главную](../README.md)
