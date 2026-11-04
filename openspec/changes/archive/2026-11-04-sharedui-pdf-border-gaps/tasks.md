# Tasks

## 1. Заплатки [UI-21]

- [x] 1.1 `pdfPolyfills.ts`: глобал `Iterator` и одиннадцать итераторных помощников на
  `%IteratorPrototype%`, `Set.prototype.intersection`, `Response`/`Blob.prototype.bytes`,
  `ReadableStream.prototype.values` и `[Symbol.asyncIterator]` — только при отсутствии; `IMPLS`
  дополнен; шапка модуля называет все три браузера (D2); проверка — `pdfPolyfills.test.ts`: каждая
  заплатка против встроенной функции Node на одних входах (порядок, ленивость, ранний выход
  закрывает итератор и отменяет поток, `TypeError` на не-функции), `patch` не трогает имеющееся.

## 2. Снимок границы и сторож [UI-21]

- [x] 2.1 `browserBorderPaths.ts`: путь `%IteratorPrototype%` (D3); генератор
  `scripts/browser-border.mjs` — ключ `--bcd`, правило «поддержано на границе», веб-API, `kept`,
  поля `bcd` и `browsers`, печать выброшенного и оставленного без данных; шапка — новая команда
  пересъёма (D3); проверка — пересъём на `node:22.23.3-alpine` с данными 8.1.8: в снимке нет
  итераторных помощников, методов `Set`, `Response.prototype.bytes`, `Blob.prototype.bytes`,
  `ReadableStream.prototype.values`, глобала `Iterator`; `Error.captureStackTrace` — в `kept`.
- [x] 2.2 `browserBorder.ts` — шапка (что снимок теперь моделирует и чего нет: DOM); тест «граница
  наступила» в `pdfEngine.test.ts` сверяет места новых заплаток; тест «Node новее границы» называет
  убранное из Firefox/Safari и веб-API (D3); проверка — `npm test -- pdfEngine` зелёный на 26.13.0.
- [x] 2.3 Сторож по имени вызова в `pdfEngine.test.ts` и проверка `kept` (D4); проверка — зелёный с
  заплатками; руками и с возвратом: без заплатки `bytes` — красный с `Response.prototype.bytes` и
  `Blob.prototype.bytes`; без `intersection` — красный с ним; без глобала `Iterator` — тест движка
  красный на загрузке библиотеки; без асинхронного обхода потока — красный `doc.text()`.

## 3. Документация и ручная проверка [UI-21]

- [x] 3.1 DEV_NOTES, абзац «Граница браузеров» в разделе просмотрщика: снимок — Node 22 ∩ данные MDN,
  команда пересъёма, сторож по имени, чего не видит (DOM); проверка — абзац не называет пробелы,
  которые закрыты, и команда в нём совпадает с шапкой генератора.
- [x] 3.2 Комментарий `viewerSupported()` (`viewerGate.ts`) и `ViewerMessage` не врут: граница та же,
  остальное — заплатки трёх браузеров; проверка — чтение.
- [x] 3.3 Ручная проверка в браузере ниже исправленных функций (Firefox 127 — нет `Iterator`,
  помощников и `bytes`), если его удаётся поднять без правки проекта (Playwright той поры в
  scratchpad): до заплаток — ошибка загрузки куска, после — документ показан и выделяется; не
  удалось — сказать в PR прямо, чем проверено вместо.
  Принято владельцем 04.11.2026 без прогона в Firefox 127 / WebKit 18.0 (браузеры той поры не
  докачались). Проверено вместо: тест движка с границей, каждая заплатка снята руками по очереди —
  красный; без глобала `Iterator` — `ReferenceError: Iterator is not defined` при загрузке.

## 4. Верификация

- [x] 4.1 `cd src/client && npx tsc -b`, `npm test`, `npm run lint:ratchet`, `npm run ui:ratchet` —
  зелёные; лимит 500 строк кода — `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj
  --filter FileSizeRatchet` зелёный (`pdfPolyfills.ts` и `pdfEngine.test.ts` растут).
- [x] 4.2 `openspec validate sharedui-pdf-border-gaps --strict` зелёный.

## Workflow follow-up

- Ветка перенесена на master после слияния PR #1747 и архива `infra-node26` (#1749): дельта [UI-21]
  этого change написана поверх его текста.
- PR → CI зелёный → слияние (пользователь) → `/opsx:archive sharedui-pdf-border-gaps`.
