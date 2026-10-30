# Tasks: три ручных меню на страницах — на Radix

Один PR с «Closes #1538», после слияния `sharedui-dropdown-layer-guard` (#1537). Без новой
функциональности — PATCH, метка `version:minor` не нужна.

## 1. Редактор шаблона

- [x] 1.1 `RequisitePicker` → `DropdownMenu`: портал, вид слоя, составные поля — `DropdownMenu.Label`,
  высота по доступной, фокус в Monaco после вставки; свой `mousedown` удалён [UI-51]. Проверка — тест:
  стрелки перешагивают группу, Enter вставляет `#get("<путь>")`, Esc возвращает фокус на «Реквизит»
- [x] 1.2 ~~Меню стрелки «Сохранить» → `DropdownMenu`~~ — снято сверкой 30.10.2026: уже `SplitButton`
  (#1696), его тесты — `EditorPanel.test.tsx`

## 2. Разбиение PDF

- [x] 2.1 «Перенести в группу» → `DropdownMenu`: группы и «+ Новый документ», вид слоя `MENU_SURFACE`
  вместо `z-10` [UI-51]. Проверка — тест полосы выделения: выбор группы зовёт `onMoveSelected`, щелчок
  мимо закрывает; ручная проверка нижней группы ленты на стенде — описание PR
- [x] 2.2 Уровень размера `PdfGroupingEditor` — по факту. Проверка — `FileSizeRatchet` зелёный

## 3. Правило

- [x] 3.1 Уровень `manual-dropdown` — ноль (`npm run ui:ratchet:update`); `docs/UI.md`, «Кто сторожит» —
  уровень ноль [UI-51]. Проверка — `npm run ui:ratchet` без роста, раздел `manual-dropdown` пуст

## 4. Верификация

- [x] 4.1 `cd src/client && npx tsc -b` — без ошибок
- [x] 4.2 `cd src/client && npm test` — зелёный
- [x] 4.3 `cd src/client && npm run ui:ratchet` — без роста, `manual-dropdown` 0
- [x] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
