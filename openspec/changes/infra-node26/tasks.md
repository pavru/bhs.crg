# Tasks

## 0. Вопросы владельцу

- [x] 0.1 Сторож заплаток pdf.js на Node 26 (`pdfEngine.test.ts`). Варианты: (а) тест сам делает
  границу — убирает из среды функции, которых на границе нет; (б) отдельный шаг CI на Node 22;
  (в) поднять границу браузеров и снять заплатки; (г) убрать сторож. **Ответ владельца 04.11.2026:
  (а) «Тест сам делает границу»** — design D6.
- [x] 0.2 Версия — `26.13.0`, как локально у владельца (решение 04.11.2026); тег `node:26.13.0-alpine`
  существует — ближайшая не понадобилась.

## 1. Одна версия и сверка [UI-85]

- [x] 1.1 `.nvmrc` в корне = `26.13.0`; `ci.yml` — обе работы `node-version-file: .nvmrc` вместо
  `node-version: '22'`, комментарий переписан (D1, D7); проверка — сверка 1.4.
- [x] 1.2 `deploy/Dockerfile.web` — `FROM node:26.13.0-alpine`, комментарий о пороге переписан;
  `src/client/package.json` — `engines.node >=26.13.0` и `//engines`; `src/client/.npmrc` —
  комментарий; `docs/tools/package.json` — `engines` (D2); проверка — `npm ci` в клиенте проходит на
  26.13.0.
- [x] 1.3 dependabot — комментарии у правила `ignore` образа node и у `pdfjs-dist` (D3, D7).
- [x] 1.4 `src/client/nodeVersion.test.ts` — сверка `.nvmrc` / `FROM` / `engines` ×2 / setup-node в
  `ci.yml` (D4); проверка — тест зелёный, и красный с названием места, если поменять версию в одном
  файле (проверить руками и вернуть).

## 2. vitest под Node 26 [UI-85]

- [x] 2.1 `vite.config.ts` — `test.execArgv: ['--no-webstorage']` с комментарием о причине (D5);
  проверка — пять прежде красных файлов (`ModuleSettingsSections`, `BrandIconLinks`, `PdfViewer`,
  `PdfViewer.swap`, `ImageView`) зелёные без `NODE_OPTIONS`.

## 3. Сторож заплаток делает границу сам [UI-21]

- [x] 3.1 Генератор `src/client/scripts/browser-border.mjs` и запись
  `src/client/src/shared/testing/browserBorder.json`, снятая на `node:22.23.3-alpine` (D6);
  проверка — в записи нет `getOrInsertComputed`, `sumPrecise`, `toBase64`, `try` у `Promise`.
- [x] 3.2 Помощник `browserBorder.ts` и подключение первым импортом `pdfEngine.test.ts`; тест «граница
  наступила» — на местах функций из `IMPLS` стоят заплатки (D6); проверка — весь `pdfEngine.test.ts`
  зелёный на 26.13.0; без подключения помощника тест «граница наступила» красный (проверить руками
  и вернуть); после удаления одной заплатки из `installPdfPolyfills` тест движка красный
  (проверить руками и вернуть).
- [x] 3.3 Шапки `pdfPolyfills.ts` и `pdfEngine.test.ts`, DEV_NOTES (граница браузеров, команда
  переснятия записи) — без «Node 22 из CI» (D7).

## 4. Документация правил

- [x] 4.1 CLAUDE.md и AGENTS.md тем же коммитом — «одна версия Node — `.nvmrc`»; DEV_NOTES — абзац
  «Одна версия Node» (источник, сверка, dependabot); проверка — `AgentRulesCopyTests` зелёный
  (`dotnet test … --filter AgentRulesCopyTests`).

## 5. Верификация

- [x] 5.1 `cd src/client && npx tsc -b`, `npm run build`, `npm test` (весь набор, без `NODE_OPTIONS`),
  `npm run lint:ratchet`, `npm run ui:ratchet`.
- [x] 5.2 `docker build -f deploy/Dockerfile.web .` — образ собирается на `node:26.13.0-alpine`.
- [x] 5.3 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FileSizeRatchet|AgentRulesCopyTests"`
  (сервер не тронут — остальные backend-тесты не нужны).
- [ ] 5.4 PR: CI зелёный, включая обе работы на Node из `.nvmrc` и живые прогоны.

## 6. Правки по ревью PR #1747

- [x] 6.1 Помощник границы и снимок — в `src/shared/testing/`, вне Node отказ исключением; разбор пути и
  ключа — общий `browserBorderPaths.ts` у генератора и помощника; снимок пересобран — совпал байт в байт.
- [x] 6.2 Сверка `setup-node` — по всем workflow и по шагу целиком (и «`- name:` первым»); тест на это.
- [x] 6.3 `execArgv` — `--no-webstorage` только там, где у Node есть хранилище (до 25 флага нет).
- [x] 6.4 Тексты: что снимок не моделирует (Firefox/Safari, веб-API) — design, DEV_NOTES, dependabot,
  шапки; комментарии `release.yml` и `Dockerfile.web` без Node 22.

## Workflow follow-up

- Архив change отдельным PR после слияния (`/opsx:archive infra-node26`).
- Обновить память `project_node26_migration.md` итогом.
