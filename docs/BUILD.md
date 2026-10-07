# Сборка

Проще всего собирать на Windows x64. Нужны .NET 10 SDK и PowerShell 7. Для установщика дополнительно нужны Python 3 и NSIS 3.

В корне репозитория:

```powershell
pwsh ./build.ps1 -Repository "prodkot/kot"
```

Готовая программа появится в новой папке `publish-…`. Скрипт скачает sing-box и установщик WebView2, проверит их и добавит к приложению. Нужен доступ к GitHub, Microsoft и NuGet.

Параметр `-Repository` задаёт источник обновлений для новых профилей. Если собираешь свой форк, укажи свой `owner/repository`. Сборка без этого параметра не получает адрес репозитория по умолчанию.

## Где что лежит

- `Core/`: подписки, конфигурации sing-box, пинг, резервные копии и проверка обновлений.
- `Windows/`: окно, запуск sing-box, хранение профиля и интеграция с Windows.
- `Windows/ui/`: HTML, стили, JavaScript и локальный шрифт.
- `Tests/`: проверки основной логики и интерфейса.
- `NativeTests/`: проверки запуска и завершения процессов на Windows.
- `Release/` и `Installer/`: подпись пакетов, публикация и установщик.

## Проверки

Основная логика:

```powershell
dotnet run --project Tests/Kot.Tests.csproj -c Release
```

Нативный запуск процессов, только на Windows:

```powershell
dotnet publish NativeTests/Kot.NativeTests.csproj -c Release -r win-x64 --self-contained false -o artifacts/native-tests
./artifacts/native-tests/Kot.NativeTests.exe
```

Интерфейс проверяется через Chromium с тестовым мостом к приложению. Нужны Python и Playwright:

```powershell
python -m pip install playwright
python -m playwright install chromium
python Tests/ui.py
```

Эти проверки не заменяют запуск туннеля на настоящем компьютере. Перед выпуском отдельно проверяй установку, обновление с предыдущей версии, восстановление подключения, удаление и работу DNS/IPv6. Текущий список выполненных проверок есть в [VALIDATION.txt](../VALIDATION.txt).

## Установщик и релиз

Одна сборка `build.ps1` ещё не делает установщик и подписанный ZIP. Для них нужен `Release/release.ps1` и ключ издателя. Полный порядок описан в [инструкции по GitHub](../Release/GITHUB.md).

Не меняй файлы внутри уже подписанного ZIP. После правок собери и подпиши пакет заново.

[На главную](../README.md)
