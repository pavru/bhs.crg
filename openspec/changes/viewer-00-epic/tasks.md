# Tasks: свой просмотрщик PDF — перечень дочерних change и PR

Каждая строка — дочерний change в `openspec/changes/viewer-NN-*`. Галка ставится, когда его PR слит и
change заархивирован. Архитектура — `design.md`. Порядок — порядок старта. Число в скобках — PR.

## 1. Компонент

- [x] 1.1 `viewer-01-component` (1) — `shared/ui/viewer`: панель, слой текста, Ctrl+P, режим поверх
  экрана, сборка, поставка, CSP в `vite preview`, храповик `native-pdf-frame`; первый потребитель — вид
  для чтения счёта. Метки [UI-10]…[UI-23] [COST-6.2]; issue #1490. Условие — три макета и выбор владельца
  (D10). Проверка — change заархивирован; вид для чтения показан в собранном образе `web`
  (слит #1618, bf5f923b; архив `2026-10-25-viewer-01-component`; ручная приёмка в образе `web` —
  задача 4.3 архива — не проведена)

## 2. Места (после 1.1, между собой независимы)

- [ ] 2.1 `viewer-02-invoice-scan` (1) — скан счёта (PDF и фото), узкий экран поверх формы [UI-24]
  [COST-6.2]; issue #1491. Проверка — change заархивирован
- [x] 2.2 `viewer-03-file-field` (1) — поле-файл документа, в том числе на телефоне; issue #1492.
  Проверка — change заархивирован
- [ ] 2.3 `viewer-04-document-preview` (1) — предпросмотр документа без прыжка, утечка
  `previewGeneratedFile` [UI-25]; issue #1493. Проверка — change заархивирован

## 3. Закрытие (после 2.1–2.3)

- [ ] 3.1 `viewer-05-close-frames` (1) — храповик на нуле и `shared/`, `frame-src` поставки, одна функция
  открытия вкладки [UI-26] [UI-27]; issue #1494. Проверка — change заархивирован; `grep -rn "<iframe"
  src/client/src` пуст

## 4. Независимо

- [ ] 4.1 `viewer-06-invoice-reading-note` (1) — строка-оговорка вида для чтения в одну строку
  [COST-6.2]; issue #1495. Удобнее после 1.1. Проверка — change заархивирован

## 5. Верификация эпика

- [ ] 5.1 `cd src/client && npm test` (vitest: состояние просмотра, компонент с подменённым движком,
  настоящий движок в Node, сторожа упаковки и CSP) — зелёный
- [ ] 5.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 5.3 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста; `native-pdf-frame` на
  нуле
- [ ] 5.4 Сервер (только `viewer-06`): `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj
  --filter "FullyQualifiedName~InvoiceOfficeFile|FullyQualifiedName~PrintProfile|FullyQualifiedName~ExcelPageLayout"`
  — зелёный
- [ ] 5.5 Лимит 500 строк кода: `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter
  FileSizeRatchet` — зелёный (файлы `shared/ui/viewer` под порогом)
- [ ] 5.6 Живые прогоны CI (`invoices-smoke`, `fields-smoke`) зелёные под CSP поставки
- [ ] 5.7 Владельцу сказано, что эпик закончен: выигрыш на телефоне, предпросмотр без прыжка, печать
  Ctrl+P

## Workflow follow-up

- Дочерним PR — «Closes #N» по-английски; функциональным (`viewer-01`…`04`, `06`) — метка
  `version:minor` и метка `ui`.
- Зонтик архивируется последним, после всех дочерних change; эпик #1489 закрывается вместе с ним.
