# Выпуск версии

1. Обнови номер в `Core/ClientIdentity.cs`, `Windows/Kot.Windows.csproj`, `Windows/app.manifest`, интерфейсе и `Installer/Kot.nsi`.
2. Запиши изменения в `Release/NOTES.md` и `CHANGELOG.md`.
3. Проверь сборку и тесты. Создай тег `vНОМЕР` и отправь его на GitHub.

Workflow соберёт Setup, подпишет вложенный пакет ключом `KOT_RELEASE_SIGNING_KEY` из GitHub Secrets и опубликует один установщик. Текст страницы релиза берётся из `Release/NOTES.md`.

Приватный ключ хранится вне репозитория. Его публичная часть уже закреплена в клиенте. Не заменяй ключ при обычном обновлении: предыдущие клиенты не примут новую подпись.

Для локального выпуска на Windows нужны NSIS 3, Python 3, .NET 10 SDK и PowerShell 7:

```powershell
pwsh ./Release/release.ps1 -Key 'C:\private\kot-release.pem'
```

В `artifacts` останется только Setup. ZIP используется внутри сборки и встраивается в Setup, отдельно не публикуется. Перед публикацией Setup повторно проверяется клиентским валидатором.
