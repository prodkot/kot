# Выпуск версии

1. Обнови номер в `Core/ClientIdentity.cs`, `Windows/Kot.Windows.csproj`, `Windows/app.manifest`, интерфейсе и `Installer/Kot.nsi`.
2. Запиши изменения в `Release/NOTES.md` и `CHANGELOG.md`.
3. Запусти `python Tests/repository.py`, проверки из [docs/BUILD.md](../docs/BUILD.md) и CI pull request.
4. Сначала запусти workflow **Windows release** вручную на ветке PR: он проверит подписанный Setup без публикации. После успешных проверок слей PR.
5. Создай тег `vНОМЕР` на проверенном коммите `main` и отправь его на GitHub. Не ставь релизный тег на непроверенную ветку.

Workflow соберёт Setup, подпишет вложенный пакет ключом `KOT_RELEASE_SIGNING_KEY` из GitHub Secrets и опубликует один установщик. Текст страницы релиза берётся из `Release/NOTES.md`.

Приватный ключ хранится вне репозитория. Его публичная часть уже закреплена в клиенте. Не заменяй ключ при обычном обновлении: предыдущие клиенты не примут новую подпись.

Для локального выпуска на Windows нужны NSIS 3, Python 3, .NET 10 SDK и PowerShell 7:

```powershell
pwsh ./Release/release.ps1 -Key 'C:\private\kot-release.pem'
```

В `artifacts` останется только Setup. ZIP используется внутри сборки и встраивается в Setup, отдельно не публикуется. Перед публикацией Setup повторно проверяется клиентским валидатором.
