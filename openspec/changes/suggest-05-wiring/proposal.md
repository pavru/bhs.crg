# Proposal: подключение подсказок — шаг «Новая позиция», формы, ячейки, флажок в редакторе типа

Дочерний change зонтика `suggest-00-epic`, общая архитектура — `../suggest-00-epic/design.md` (D3, D4,
D9, D10, D12).

## Why

Issue #1533 (UI: подсказки 5/5 — подключение в «Новую позицию», формы и ячейки, флажок в редакторе
типа, e2e), часть эпика #1527. Последняя: после `suggest-02` и `suggest-03`; `suggest-04` она не ждёт.

Главное место по замеру #1528 — шаг «Новая позиция» в выборе номенклатуры: почти весь выигрыш в копии
базы заказчика — поле «Номенклатура → Производитель» (110 пар, 17 различных значений, сбережено 93), а
позиции номенклатуры заводятся именно там, из строки счёта. Шаг рисует свой `<input>` мимо
`PrimitiveInput`, как и ячейки массивов — поэтому точек подключения три, а не одна. Администратору нужен
способ выключить подсказку у поля — флажок в карточке поля, хранимый тэгом `input.noSuggest`
(`suggest-01`).

## What Changes

- **Три точки подключения** через `SuggestField` (`suggest-03`), условие — правило `Suggestable` по
  `field.type`, а не по виду контрола (`text` в ячейке рисуется однострочным `<input>`):
  - шаг «Новая позиция» (`NewNomenclatureStep.tsx`, поле на `:162`) — право на подсказку по решению В-1
    зонтика; описание двери `/api/nomenclature/intake` отдаёт у поля код причины правила;
  - `PrimitiveInput.tsx` — поля форм (реквизиты, записи каталога, вложенные составные, документы
    качества);
  - `TableCell` (`ComplexCells.tsx:110-118`) — подключается сразу: ответ для строк массивов пуст до
    `suggest-04` и наполнится сам.
- `InvoiceExtraFields` не подключается (данные за границей модуля, D9): подсказка включается только там,
  где задан контекст типа-владельца, а у дописанных полей счёта его нет.
- **Контекст типа-владельца** — React-контекст из `RequisitesTab`, `ComplexFields`,
  `ArrayTableModal`/ячеек, формы записи каталога и шага «Новая позиция»: тип, чьей схеме принадлежит
  поле, и `scope`/`scopeId` записи.
- **Флажок «Подсказывать введённое ранее»** в карточке поля `FieldBuilder`, рядом с «Вычисляемое
  (формула)», компонентом `Checkbox`, пояснение текстом под флажком. Хранится тэгом `input.noSuggest`;
  чипом среди тэгов поля не показывается. `manual` — флажок виден и снят; `type`, `computed` — строки нет;
  прочие коды — вместо флажка строка с причиной (текст по коду на клиенте). Переопределения в наследнике
  не заводятся.
- Попутно: у текстовой ячейки `TableCell` появляется `aria-label` (правило `docs/UI.md`).
- e2e на свежей базе; `USER_GUIDE` и `ADMIN_GUIDE` (флажок поля), `npm run toc`.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `core`: поля записей, нарисованные по схеме типа, и шаг «Новая позиция» (запись справочника ядра через
  дверь ядра) предлагают введённое ранее. Поведение модуля `costs` не меняется: шаг — экран модуля, но
  поле и дверь — ядра (D9 зонтика).
- `types-and-tags`: редактор типа показывает флажок «Подсказывать введённое ранее» или причину, по которой
  поле не подсказывается.

## Impact

- **Клиент:** `features/costs/NewNomenclatureStep.tsx`, `shared/api/nomenclatureIntake.ts`,
  `features/document-sets/fields/PrimitiveInput.tsx`, `ComplexCells.tsx`, `ArrayTableModal.tsx`,
  `ComplexFields.tsx`, `editor/RequisitesTab.tsx`, `catalog/index.tsx`, `features/quality-docs/
  QualityDocForm.tsx`, `features/settings/FieldBuilder.tsx`; новые `fields/suggestContext.tsx`,
  `settings/SuggestFlag.tsx`. `FieldBuilder.tsx`, `RequisitesTab.tsx`, `ComplexFields.tsx` — в храповике
  размера: новое — в свои файлы.
- **Сервер:** `Api/Endpoints/Documents/NomenclatureIntakeEndpoints.cs` и
  `Application/Documents/NomenclatureIntake.cs` — код причины `Suggestable` у поля описания.
- **e2e:** новый сценарий в `src/client/e2e/`, вписанный в список живых прогонов CI.
- **Документы:** `docs/USER_GUIDE.md`, `docs/ADMIN_GUIDE.md` (`npm run toc` в `docs/tools`).
- **Версия:** не меняется; PR — с меткой `version:minor`.
