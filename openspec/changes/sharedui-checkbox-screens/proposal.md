# Proposal: флажки экранов — на `Checkbox`

Самостоятельный change общих компонентов интерфейса (домен `sharedui`). Правило храповика уже
слито, остался перевод экранов.

## Why

Issue #1414 (нативные флажки в `features/` → `Checkbox`). Компонент `Checkbox` (`shared/ui/Checkbox`,
#1351) есть, правило `native-checkbox` в храповике интерфейса слито (#1415), `shared/` переведён
(#1420). А экраны так и рисуют нативный `<input type="checkbox">`: у каждого свой размер, цвет и
отступ, у голого — серый браузерный вид. Это крупнейший остаток повторного замера интерфейса
17.10.2026: 35 мест в 30 файлах (`src/client/ui-baseline.json`, `native-checkbox`).

## What Changes

- **Сначала сам `Checkbox`:** подпись становится блоком — `flex w-fit` вместо `inline-flex`. Прежние
  `<label>` на экранах блочные, и без этой правки два флажка с подписью подряд встают в одну строку
  (на стенде так встали «Шифрование» и «Через прокси» в настройках почты).
- Перевод всех 30 файлов уровня на `Checkbox`; уровень `native-checkbox` — ноль. Законных исключений
  нет: клетка таблицы — `size="sm"`, под `Tooltip` — `Checkbox` передаёт ref, только для чтения —
  `aria-readonly` и `onClick` уходят во флажок как есть.
- Доступные имена сохраняются: строка со своей разметкой — `Checkbox` без `label` внутри её `<label>`;
  иначе — пропы `label` / `description`; флажок без имени получает `aria-label`.
- Храповик размера: похудевшие `FieldBuilder` и `PrimitiveTypesPage` опускают уровень в
  `src/file-size-baseline.json` тем же PR.
- `docs/UI.md`, «Кто сторожит»: накопленных нативных флажков больше нет, уровень — ноль.

## Capabilities

### New Capabilities
- `sharedui`: общие компоненты интерфейса — `shared/ui`, оболочка, храповик интерфейса и правила
  `docs/UI.md`.

### Modified Capabilities
- Нет.

## Impact

- **Клиент:** `shared/ui/Checkbox.tsx`; 30 файлов `features/` (перечень — `tasks.md`).
- **Уровни:** `src/client/ui-baseline.json` (`native-checkbox` → пусто), `src/file-size-baseline.json`.
- **Живые прогоны, которые ищут флажки** через `input[type=checkbox]` или по подписи: `routing`
  (матрица прав), `settings` (движки, рабочие пространства), `dialogs` (вкладка генерации),
  `table-view` (панель колонок), `table-saved-views` («Общее»), `fields`, `types`.
- **Сервер:** не меняется.
