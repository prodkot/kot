# Сайт kot.

https://prodkot.github.io/kot/

Обычный HTML, CSS и немного JavaScript. Шрифт и скриншоты лежат в `assets`, внешние библиотеки не нужны.

Для просмотра из корня репозитория:

```sh
python3 -m http.server 8000 --directory site
```

Открой `http://localhost:8000`.

GitHub Actions публикует сайт после изменений в `site/` и после успешной сборки релиза. Ссылка на Setup и номер версии берутся из последнего релиза `prodkot/kot`. Если данные недоступны, кнопка открывает Releases.

Скриншоты сделаны с примерными данными. Код под MIT, шрифт Manrope под SIL Open Font License.
