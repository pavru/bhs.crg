# Tasks: «Назад» на телефоне закрывает поповер

Один PR — исправление по правилу #1418, PATCH (без `version:minor`), метка `ui`. Решение владельца
15.11.2026 (`design.md`): В1 — А (обёртка), В2 — да (поповеры на странице и пояснения — слои), В3 — макетов
нет. Начинать после слияния PR #1975: ветка — от master после него.

## 1. Обёртка `shared/ui/Popover`

- [ ] 1.1 `shared/ui/Popover.tsx`: реэкспорт частей Radix, свой `Root` (управляемый и неуправляемый) зовёт
  `useHistoryLayer`, `onBack?` для своего шага назад; `Trigger` и `Anchor` сообщают якорь; `Content` с
  `hideWhenDetached` на телефоне закрывает слой при уходе якоря (D2, D3) [UI-96]. Проверка — новый
  `Popover.history.test.tsx`: на узком экране открытие кладёт запись, «Назад» закрывает поповер, окно
  остаётся; Esc и выбор снимают запись; неуправляемый поповер тоже слой; `onBack` → `false` оставляет
  открытым; меню внутри поповера закрывается первым; на рабочем столе записи нет; якорь ушёл — на
  телефоне закрыт, на столе спрятан
- [ ] 1.2 `scripts/menu-layer.test.mjs`: `LAYER_PACKAGES` узнаёт импорт `@/shared/ui/Popover` и `./Popover`
  (D5). Проверка — образец с импортом обёртки и своим `bg-surface` у `Content` ловится в тестах файла;
  `npm test` зелёный

## 2. Перевод поповеров на обёртку

- [ ] 2.1 Импорт обёртки в `ComputedFieldEditor`, `FilterChips`, `ColumnsPanel`, `TableViewHeader`,
  `SitesFilter`, `LayoutChooser`, `InvoicePartyNote`, `LineMatchChip`, `InvoiceFileImage`,
  `InvoicePlaceField`, `NotificationList` (D1, D4) [UI-96]. Проверка — их тесты зелёные; тест
  `FilterChips` или `TablePage` на узком экране: «Назад» закрывает поповер условия, адрес страницы прежний
- [ ] 2.2 `ActivityCenter`: свой `useHistoryLayer` убран, слой — от обёртки (D4) [UI-96]. Проверка — тест на
  узком экране: одна «Назад» закрывает панель
- [ ] 2.3 `DateInput`: слой и наблюдатель значка из #1975 — в обёртку, возврат из списка месяцев — через
  `onBack` (D6) [UI-54] [UI-96]. Проверка — `DateInput.history.test.tsx` без правок зелёный
- [ ] 2.4 `TableViewHeader`: переход по пункту при открытом поповере (D1, «Переход по пункту поповера»)
  [UI-96]. Проверка — тест на узком экране: после выбора представления «Назад» возвращает к прежнему
  одним шагом

## 3. Сторож и правила

- [ ] 3.1 `windowsAreHistoryLayers.test.ts`: пакет `@radix-ui/react-popover` (статический импорт и
  `import()`) — только `shared/ui/Popover.tsx` и `FIELD_PARTS` (`SuggestBox`) с причиной (D5) [UI-96].
  Проверка — правило вынесено функцией, образец с прямым импортом ловится; тест на дереве `src` зелёный
- [ ] 3.2 `docs/UI.md`: порядок «Назад» (#1418) называет поповер; пункт «Поповер — слой истории» рядом с
  исключением частей поля; «Слои над окном» — `hideWhenDetached` на телефоне закрывает; «Кто сторожит» —
  правило 3.1 [UI-96]. Проверка — ревью PR

## 4. Живой прогон

- [ ] 4.1 `table-chips-smoke` на 390 px: открыть поповер условия → «Назад» закрывает его, страница и адрес
  остаются [UI-96]. Проверка — `npm run test:e2e:table-chips` зелёный

## 5. Верификация

- [ ] 5.1 `cd src/client && npx tsc -b` — без ошибок
- [ ] 5.2 `cd src/client && npm test` — зелёный
- [ ] 5.3 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста
- [ ] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
