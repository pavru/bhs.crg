# Tasks: закрыть фреймы

Условие — `viewer-02`, `viewer-03`, `viewer-04` слиты. Макеты не нужны: интерфейс не меняется.

## 1. Храповик

- [x] 1.1 Область правила по пути файла: механизм `sharedui-ratchet-shared-scope` (D1), если тот слит;
  иначе поле `skip` здесь (`design.md`). `countViolations` получает путь, случаи — в
  `ui-ratchet.test.mjs` [UI-26]. Проверка — тесты храповика зелёные (`npm test`)
- [x] 1.2 `native-pdf-frame`: область — `features/` и весь `shared/`, кроме `shared/ui/viewer/`; базовый уровень
  — 0 [UI-26]. Проверка — `npm run ui:ratchet` зелёный; пробный `<iframe>` в `shared/ui/X.tsx` красный;
  `WeakMap<object, …>` не считается
- [x] 1.3 `native-title`: ветка `tag !== 'iframe'` удалена, тест переписан; `docs/UI.md` — исключение `iframe`
  убрано из «Подсказки» и абзаца храповика, записано правило «фреймов нет, PDF — просмотрщик»
  [UI-26]. Проверка — `npm run ui:ratchet` без роста по `native-title`

## 2. Политика поставки

- [x] 2.1 `deploy/nginx.conf`: `frame-src blob:`; комментарий — `blob:` у frame только для печати
  просмотрщика, `'unsafe-eval'` держит и wasm pdf.js [UI-26]. Проверка — сторож CSP (`viewer-01`) зелёный;
  живые прогоны под новой строкой зелёные
- [ ] 2.2 Ручная проверка в собранном образе `web` (`docker compose -f deploy/docker-compose.yml -f
  deploy/docker-compose.build.yml`): Ctrl+P в виде для чтения печатает файл, вкладки открываются. Проверка —
  описание PR
- [x] 2.3 `docs/DEV_NOTES.md`: CSP поставки — почему `frame-src blob:` и что держит `'unsafe-eval'`. Проверка —
  ревью PR

## 3. Одна функция открытия вкладки

- [x] 3.1 `openAttachmentInNewTab` → обёртка над `openInNewTab` с загрузчиком `loadAttachmentBlob` [UI-27].
  Проверка — тест функции: окно до загрузки, отзыв с отсрочкой, отказ — текст во вкладке и исключение
  месту
- [x] 3.2 `no-restricted-properties` на `window.open` вне `shared/ui/viewer/openInNewTab.ts` [UI-27]. Проверка —
  `npm run lint:ratchet` зелёный; `grep -rn "window.open" src/client/src` — только `openInNewTab.ts`
- [x] 3.3 Ревью PR #1637: тип вне белого списка скачивается (`saveUnknown`), белый список один
  (`shownTypeOf`), места зовут `useOpenAttachment` (отказ тостом), сторож — и `globalThis`/`self`/разбор/голый
  `open` [UI-27]. Проверка — тесты `openInNewTab`, `attachments.open`, `useOpenAttachment`; пробные обходы
  линта красные

## 4. Верификация

- [x] 4.1 `cd src/client && npm test` — зелёный
- [x] 4.2 `cd src/client && npx tsc -b` — без ошибок
- [x] 4.3 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — зелёные, `native-pdf-frame` = 0
- [x] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
  (`scripts/ui-ratchet-lib.mjs` под присмотром размера)
- [x] 4.5 `grep -rn "<iframe\|createElement('iframe'" src/client/src` — только `shared/ui/viewer`

## Workflow follow-up

- PR: метка `ui`; «Closes #1494». После слияния — закрыть эпик #1489, если `viewer-06` тоже слит.
