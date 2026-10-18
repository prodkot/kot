# Pure report helpers. Manual answers never rewrite automatic evidence.
function Get-ManualSteps {
    @(
        @('window-tray','Откройте kot, сверните в трей, откройте снова, повторно запустите ярлык: одно рабочее окно.'),
        @('provider-and-auto','Подключите свой сервер в TUN, откройте сайт. Полную конфигурацию выберите вручную. В Auto должны участвовать только обычные серверы.'),
        @('sleep-resume','При подключённом VPN выполните сон и пробуждение. Проверьте восстановление соединения.'),
        @('network-change','При подключённом VPN смените Wi-Fi или сеть. Проверьте восстановление соединения.'),
        @('disconnect-exit','Нажмите Отключить, затем Выход. Проверьте, что сеть восстановлена и значка в трее нет.')
    )
}
function Get-QaSummary($Report) {
    $automaticPass=@($Report.checks | Where-Object { $_.status -eq 'PASS' }).Count
    $automaticFail=@($Report.checks | Where-Object { $_.status -eq 'FAIL' }).Count
    $automaticSkip=@($Report.checks | Where-Object { $_.status -in @('SKIP','NOT_TESTED') }).Count
    $manualPass=@($Report.manual | Where-Object { $_.status -eq 'PASS' }).Count
    $manualFail=@($Report.manual | Where-Object { $_.status -eq 'FAIL' }).Count
    $manualSkip=@($Report.manual | Where-Object { $_.status -eq 'NOT_TESTED' }).Count
    "Автоматически: PASS=$automaticPass; FAIL=$automaticFail; не проверено=$automaticSkip."
    "Ответы пользователя: PASS=$manualPass; FAIL=$manualFail; пропущено=$manualSkip."
    'TUN, внешний IPv6, DNS провайдера, сон и смена сети автоматически не проверяются (NOT_TESTED).'
    'Ручные ответы сохраняются отдельно и не меняют автоматические результаты.'
}
