<p align="center"><img src="Brand/kot-abstract-dark.svg" width="56" height="56" alt=""></p>

<h1 align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="Brand/kot-wordmark-dark.svg">
    <img src="Brand/kot-wordmark-light.svg" width="98" alt="kot.">
  </picture>
</h1>

<p align="center">Клиент для Windows. Твои подписки, серверы и правила маршрутизации в одном окне.</p>

<p align="center"><strong><a href="https://github.com/prodkot/kot/releases/latest">Скачать для Windows x64</a></strong> · <a href="https://prodkot.github.io/kot/#demo" title="Твой сервер. Один клик.">Сайт и демо</a> · <a href="docs/README.md">Документация</a></p>

![Главный экран kot. с активным TUN, выбранным сервером и скоростью передачи данных](docs/images/readme/connection.png)

*Главный экран v0.8.3. Сервер и показатели трафика демонстрационные.*

## Первое подключение

1. Установи kot. на Windows 10 или 11 (x64). При запуске подтверди запрос прав администратора.
2. Нажми **+** и вставь ссылку на свою подписку или отдельный сервер.
3. Выбери сервер и нажми **Подключить**. В режиме TUN клиент проверит адаптер, DNS и прохождение трафика перед тем, как показать «Подключено».

## Как проходит трафик

kot. использует Xray и поддерживает VLESS, VMess, Trojan, Shadowsocks и Hysteria2. В режиме **TUN** трафик приложений проходит через виртуальный сетевой адаптер. **Системный прокси** подходит для программ, которые используют настройки прокси Windows.

В профиле маршрутизации можно отправить рабочие сайты через VPN, оставить локальные адреса доступными напрямую и заблокировать отдельные домены. Правила также поддерживают IP-подсети и приложения.

<details>
<summary>Посмотреть список серверов</summary>

![Экран «Серверы» в светлой теме с примером подписки и результатами проверки задержки](docs/images/readme/servers.png)

</details>

[Руководство](docs/GUIDE.md) · [Примеры маршрутов](docs/ROUTING.md) · [История изменений](CHANGELOG.md) · [Лицензия MIT](LICENSE.txt)
