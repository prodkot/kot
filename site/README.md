# Сайт kot.

https://prodkot.github.io/kot/

HTML, CSS и JavaScript без сторонних библиотек. Шрифт лежит рядом с сайтом.

Большое окно на главной можно потыкать. Это интерфейс Windows-клиента с примерами серверов, соединений и трафика. Он не подключается к сети, не читает HWID и не меняет настройки компьютера. Состояние демки живёт только до перезагрузки страницы.

Посмотреть локально из корня репозитория:

```sh
python3 site/build.py --output artifacts/site
python3 -m http.server 8000 --directory artifacts/site
```

Открой `http://localhost:8000`. Сборка сама добавит интерфейс из `Windows/ui` и демо-обработчики из `site/demo`.

GitHub Actions публикует сайт после изменений и успешных релизов. Кнопка скачивания ведёт на последний Setup из `prodkot/kot`.

Код под MIT, шрифт Manrope под SIL Open Font License.
