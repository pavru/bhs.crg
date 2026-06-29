# Документация BHS.CRG

Рабочие документы — в **Markdown** (`*.md`); готовые **PDF** для передачи — в `pdf/`.

| Документ | Markdown | PDF |
|---|---|---|
| Развёртывание системы | [DEPLOYMENT.md](DEPLOYMENT.md) | [pdf/DEPLOYMENT.pdf](pdf/DEPLOYMENT.pdf) |
| Инструкция пользователя | [USER_GUIDE.md](USER_GUIDE.md) | [pdf/USER_GUIDE.pdf](pdf/USER_GUIDE.pdf) |
| Инструкция администратора | [ADMIN_GUIDE.md](ADMIN_GUIDE.md) | [pdf/ADMIN_GUIDE.pdf](pdf/ADMIN_GUIDE.pdf) |

- `images/` — скриншоты, используемые в инструкциях.
- `tools/` — конвертер Markdown → PDF.

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

> Скриншоты в инструкциях сняты с живого интерфейса и содержат демонстрационные данные.
> При необходимости их можно переснять после наполнения системы реальными данными.
