# Документация BHS.CRG

Рабочие документы — в **Markdown** (`*.md`); готовые **PDF** для передачи — в `pdf/`.

| Документ | Markdown | PDF |
|---|---|---|
| Развёртывание системы | [DEPLOYMENT.md](DEPLOYMENT.md) | [pdf/DEPLOYMENT.pdf](pdf/DEPLOYMENT.pdf) |
| Инструкция пользователя | [USER_GUIDE.md](USER_GUIDE.md) | [pdf/USER_GUIDE.pdf](pdf/USER_GUIDE.pdf) |
| Инструкция администратора | [ADMIN_GUIDE.md](ADMIN_GUIDE.md) | [pdf/ADMIN_GUIDE.pdf](pdf/ADMIN_GUIDE.pdf) |
| Заметки разработчика: почему так сделано | [DEV_NOTES.md](DEV_NOTES.md) | — |
| Правила интерфейса: форма, цвет, радиусы | [UI.md](UI.md) | — |
| Функциональные тэги (разработка) | [FUNCTIONAL_TAGS.md](FUNCTIONAL_TAGS.md) | — |
| План: привязки материалов и MCP (разработка) | [PLAN_QUALITY_LINKS_AND_MCP.md](PLAN_QUALITY_LINKS_AND_MCP.md) | — |

- `images/` — скриншоты, используемые в инструкциях.
- `tools/` — конвертер Markdown → PDF и построитель содержания.

## Пересборка PDF

```bash
cd docs/tools
npm install                       # один раз
npx playwright install chromium   # один раз (скачать браузер)
npm run pdf                        # собрать все PDF в ../pdf/
# или один документ:
node build-pdf.mjs USER_GUIDE.md
```

Конвертер использует [marked](https://github.com/markedjs/marked) (Markdown → HTML) и
[Playwright](https://playwright.dev) (HTML → PDF) с обложкой, нумерацией страниц и едиными
стилями. Чтобы добавить новый документ в сборку «по умолчанию», впишите его в список
`DEFAULT_DOCS` в `tools/build-pdf.mjs`.

## Содержание инструкций

В начале `USER_GUIDE.md`, `ADMIN_GUIDE.md` и `DEPLOYMENT.md` стоит содержание — все разделы `##` и
подразделы `###`, каждая строка — ссылка на свой заголовок. **Руками его не правят**: блок между
метками `<!-- toc -->` и `<!-- /toc -->` строит скрипт, остальной текст он не трогает.

```bash
cd docs/tools
npm run toc                 # перестроить содержание после правки заголовков
node toc.mjs --check        # только сверить
```

Добавили, убрали или переименовали главу — перестройте содержание, затем PDF (`npm run pdf`).
Забытое ловит обязательная проверка CI «Frontend» (`src/client/scripts/docs-toc.test.mjs`): она
называет расхождение и эту команду.

Якоря ставятся **по правилам GitHub**, и тем же модулем (`tools/toc.mjs`) — заголовкам в PDF, поэтому
одна ссылка работает и на GitHub, и в PDF; в PDF есть и закладки по заголовкам. Ссылка на раздел из
текста той же инструкции — тем же якорем, например в DEPLOYMENT: `[§4](#4-конфигурация-env)`.

> Скриншоты в инструкциях сняты с живого интерфейса и содержат демонстрационные данные.
> При необходимости их можно переснять после наполнения системы реальными данными.
