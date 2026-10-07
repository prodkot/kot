# kot.

Персональный VPN-клиент для Windows x64 на sing-box. Текущая версия: **0.4.1 beta**.

- Подписки HTTPS и отдельные профили, постоянный HWID с копированием.
- VLESS, VMess, Trojan, Shadowsocks, Hysteria2 и поддерживаемые параметры транспорта.
- Режимы маршрутизации, избранное, автоматический выбор, проверки задержки.
- Логи и активные соединения, скорость загрузки и отправки.
- Светлая/тёмная тема, встроенный Manrope, трей и автозапуск.
- Подписанные обновления из GitHub Releases или VPS, установка и перезапуск.

## Установка

[Скачать установщик и portable ZIP](https://github.com/prodkot/kot/releases/latest).

Запустите `Kot-Setup-0.4.1-Windows-x64.exe`. Portable-вариант: распакуйте Windows ZIP целиком и запустите `Kot.exe`. WebView2 Runtime требуется; bootstrap Microsoft включён в комплект. Подписки и HWID хранятся отдельно от папки программы. Обновление не сбрасывает их.

## Разработка

Windows x64, .NET 10 SDK, PowerShell 7. Для установщика также Python 3 и NSIS 3.

```powershell
pwsh ./build.ps1 -Repository "OWNER/REPO"
dotnet run --project Tests/Kot.Tests.csproj -c Release
```

[Публикация, ключ издателя, GitHub Actions и установщик](Release/GITHUB.md). Для VPS см. [HOSTING.md](Release/HOSTING.md).

## Проверки и ограничения

См. [VALIDATION.txt](VALIDATION.txt) и [SECURITY-AUDIT.md](SECURITY-AUDIT.md). Нативный Windows сценарий установки/обновления и испытания сетевых утечек ещё требуют проверки. Kill switch отсутствует. Приложение работает с повышенными правами. Установщик пока без Authenticode-сертификата. Это beta, а не заявление о полной защищённости.

## Лицензии

[LICENSE.txt](LICENSE.txt), [THIRD-PARTY.txt](THIRD-PARTY.txt), каталог `licenses/`. Подпись релиза не меняет условия лицензий зависимостей; при распространении сохраняйте уведомления и предоставление исходников, описанное в THIRD-PARTY.txt.
