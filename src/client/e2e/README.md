# e2e — клавиатурный smoke (issue #107)

Живой прогон клавиатурных контрактов по фронту (без мыши). Дополняет юнит-тесты
(`vitest`): проверяет то, что видно только в браузере — видимый фокус после `Tab`,
глобальные горячие клавиши, поведение Radix-диалогов.

## Что проверяется (`keyboard-smoke.mjs`)

- **login-token** — вход по форме кладёт `access_token` в localStorage;
- **tab-focus-visible `<route>`** — на ключевых экранах (`/`, `/document-sets`,
  `/common-data`, `/quality-docs`, `/settings`) первый `Tab` уводит фокус с `body` на
  элемент, соответствующий `:focus-visible` (кольцо фокуса реально видно);
- **ctrl-k / palette** — `Ctrl/⌘+K` открывает командную палитру, её поле получает
  фокус, `Esc` закрывает;
- **question / help** — `?` на неполевом фокусе открывает шпаргалку, `Esc` закрывает.

## Предусловия

Подняты фронт (`:5173`) и бэк (`:5000`):

```bash
docker compose up -d                             # postgres + minio
dotnet run --project ../server/BHS.CRG.Api       # :5000
npm run dev                                       # :5173 (этот пакет)
```

Playwright берётся из npx-кеша, браузер — из `ms-playwright` (пути резолвятся
динамически). При нестандартном расположении переопределите:
`PLAYWRIGHT_PKG` (путь к `playwright/index.js`) и `CHROMIUM_EXE`
(путь к `chrome-headless-shell.exe`).

## Запуск

```bash
# из src/client, Git Bash (MSYS_NO_PATHCONV — чтобы MSYS не переписал пути в аргументах)
MSYS_NO_PATHCONV=1 npm run test:e2e:keyboard
```

Учётка по умолчанию — админ `admin@bhs.local` / `Demo12345!` (переопределяется
`SMOKE_EMAIL` / `SMOKE_PASSWORD`), адрес — `SMOKE_BASE`.

Код возврата `0` — все проверки прошли, `1` — есть провал (имена провалов печатаются).
