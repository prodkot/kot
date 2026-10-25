# Сборка

Предварительный Ubuntu-порт собирается отдельно: [Linux: сборка, установка и ограничения](LINUX.md). Текущее состояние — [LINUX_PORT.md](../LINUX_PORT.md), выполненные проверки — [LINUX_TEST_REPORT.md](../LINUX_TEST_REPORT.md). Windows Setup и Linux `.deb` не взаимозаменяемы.

Для Windows-клиента нужны Windows x64, PowerShell 7, Python 3.12+, Go 1.27.3 и .NET SDK из `global.json` (10.0.401). Для Setup дополнительно нужны NSIS 3 и Visual Studio 2022 или новее с компонентом **Desktop development with C++** для программы распаковки NativeAOT. Сборка скачивает компоненты с GitHub, Microsoft и NuGet, поэтому нужен интернет.

Из корня репозитория:

```powershell
pwsh ./build.ps1
```

Готовая программа появится в новой папке `publish-…`. Скрипт сначала проверяет согласованность репозитория, затем собирает клиент, собирает закреплённый официальный Xray, проверяет SHA256 исходников и Wintun и подпись установщика WebView2. Ранее созданная папка не используется повторно. Профили и настройки пользователей не входят в сборку.

## Структура

- `Core/`: подписки, конфигурации Xray, пинг, резервные копии и проверка обновлений.
- `Windows/`: окно, запуск ядра, хранение профиля и интеграция с Windows.
- `Windows/ui/`: HTML, JavaScript, флаги и общий локальный шрифт.
- `Tests/`: основная логика, интерфейс и проверка состава репозитория.
- `NativeTests/`: запуск и завершение процессов на Windows.
- `WindowTests/`: окно, трей, реальные настройки прокси и WFP-фильтры.
- `Release/`, `Installer/`: подпись пакетов, публикация и установщик.
- `site/`: статический сайт и изолированное интерактивное демо клиента.
- `licenses/`, `upstream/`: обязательные уведомления и соответствующие исходники Xray.

## Проверки

Согласованность версий, локальных ссылок, лицензий и GitHub Actions:

```powershell
python Tests/repository.py
python Tests/release_ref.py
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
./.tools/venv/Scripts/python.exe Tests/routing_ui.py
./.tools/venv/Scripts/python.exe Tests/statistics_ui.py
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

Реальные локальные цепочки используют то же закреплённое ядро, что и Setup. Из корня репозитория на Linux:

```sh
python3 Release/xray-core.py artifacts/core --target linux
dotnet run --project Tests/Kot.Tests.csproj -c Release
python3 Tests/xray_integration.py --core artifacts/core/xray
dotnet run --project Tests/Kot.Tests.csproj -c Release --no-build -- geoip-core artifacts/core/xray
```

На Windows опусти `--target linux` и укажи `artifacts/core/xray.exe`. Реальные TUN/WFP-проверки запускаются на отдельной Windows-машине с правами администратора: `Kot.WindowTests.exe --tunnel-test /path/to/core/xray.exe`. Рядом должен находиться `wintun.dll` из проверенной сборки. Порты 19441-19445 для локальных тестов должны быть свободны.

Режим `geoip-core` проверяет CIDR IPv4/IPv6 и нативные конфигурации правил стран. Старые генераторы и фикстуры sing-box в `Core/Configuration.cs` и отдельных тестах сохранены как справочные проверки совместимости прежнего формата хранения; рабочий клиент их не запускает и sing-box не поставляет.

## Выпуск

Полный порядок находится в [Release/GITHUB.md](../Release/GITHUB.md). Workflow выпуска проверяет подписи пакета и Setup, реальные TUN/WFP-фильтры, установку, повторную установку поверх старых документов и удаление. Он сохраняет пользовательские файлы и обязательные лицензии.

Эти проверки не заменяют ручные испытания на разных Windows-компьютерах: смена сети, сон, авария питания, DNS/IPv6 и восстановление после обновления. Известные ограничения перечислены в [SECURITY.md](SECURITY.md).

Не меняй Setup после подписи. Внутри него находится проверяемый пакет обновления; отдельно он не публикуется. Приватный ключ издателя хранится вне исходников. Адрес обновлений закреплён в `Core/RemoteUpdates.cs`.

[На главную](../README.md)
