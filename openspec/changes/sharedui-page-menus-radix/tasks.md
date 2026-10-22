# Tasks: три ручных меню на страницах — на Radix

Один PR с «Closes #1538», после слияния `sharedui-dropdown-layer-guard` (#1537). Без новой
функциональности — PATCH, метка `version:minor` не нужна.

## 1. Редактор шаблона

- [ ] 1.1 `RequisitePicker` → `DropdownMenu`: портал, вид слоя, составные поля — `DropdownMenu.Label`,
  высота по доступной, фокус в Monaco после вставки; свой `mousedown` удалён [UI-51]. Проверка — тест:
  стрелки перешагивают группу, Enter вставляет `#get("<путь>")`, Esc возвращает фокус на «Реквизит»
- [ ] 1.2 Меню стрелки «Сохранить» → `DropdownMenu` под `<Tooltip name>`; свой `mousedown` удалён
  [UI-51]. Проверка — тест: «Сохранить как новую версию…» открывает окно новой версии, Esc закрывает
  меню без сохранения

## 2. Разбиение PDF

- [ ] 2.1 «Перенести в группу» → `DropdownMenu`: группы и «+ Новый документ», вид слоя вместо `z-10` /
  `shadow-lg` [UI-51]. Проверка — тест полосы выделения: выбор группы зовёт `onMoveSelected`, щелчок
  мимо закрывает; ручная проверка нижней группы ленты на стенде — описание PR
- [ ] 2.2 Уровень размера `PdfGroupingEditor` — по факту. Проверка — `FileSizeRatchet` зелёный

## 3. Правило

- [ ] 3.1 Уровень `manual-dropdown` — ноль (`npm run ui:ratchet:update`); `docs/UI.md`, «Кто сторожит» —
  уровень ноль [UI-51]. Проверка — `npm run ui:ratchet` без роста, раздел `manual-dropdown` пуст

## 4. Верификация

- [ ] 4.1 `cd src/client && npx tsc -b` — без ошибок
- [ ] 4.2 `cd src/client && npm test` — зелёный
- [ ] 4.3 `cd src/client && npm run ui:ratchet` — без роста, `manual-dropdown` 0
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
