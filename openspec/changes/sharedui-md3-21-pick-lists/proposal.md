# Proposal: списки выбора в окнах и списки карточки по MD3

Дочерний change зонтика `sharedui-md3-00-epic` — задача 4.1b. Общая архитектура —
`../sharedui-md3-00-epic/design.md` (D6 слой состояния, D7 плотность, D8 отступления, D9 вид через
`shared/ui`, D10 макеты, D11 сторожа, D12 документы и дизайн-система). Предшественник —
`sharedui-md3-18-lists` (архив `2026-11-05-sharedui-md3-18-lists`). Он завёл `ListItem`, `ListGroupHeader`
и правило `list-row` [UI-84], а списки выбора и списки карточки решением владельца Л4 (05.11.2026) отдал
сюда.

## Why

После md3-18 у строк list-detail вид один, а у строки в окне выбора — свой в каждом окне:
- скругление 2–4 (`rounded-xs`, `rounded-sm`), высота 32–40 (`py-1.5`, `py-2`), подпись `body-medium`;
- активная строка (стрелкой из поля поиска) залита в каждом окне по-своему: `secondary-container`
  (`RefPickerModal`, `BaseCandidatePicker`, `ScopeGroupList`), `primary-container` (`InvoicePlaceField`),
  `surface-container` (`PickerOption`, `NewNomenclatureStep`);
- наведение — `hover:bg-primary-container`, то есть цветом выбора, а не слоем состояния (D6).

То же в списках внутри карточки: элементы массива (`ComplexFields`), находки сверки, уведомления, оглавление
формы каталога. Отмеченная флажком строка там — `primary-container`, непрочитанное уведомление —
`secondary-container`, то есть цветом выбранной строки. Кнопка «Разобрать» у находки видна только при
наведении, а на касании её нет вовсе.

Правило `list-row` насчитывает 7 мест (`ComplexFields` 3, `RefPickerModal` 2, `BaseCandidatePicker`,
`ScopeGroupList`), но остаток больше. `PickerOption` собирает заливку в переменную, и правило её не видит.
Через `PickerOption` идут три окна: номенклатура, позиция сметы, ресурс. `NewNomenclatureStep`,
`InvoicePlaceField`, оглавление формы и уведомления красят строку не той заливкой (не `primary-container`
или `secondary-container`), и правило их не считает. Ноль на счётчике значил бы «сделано», хотя на деле
не сделано.

## What Changes

- **Строка варианта выбора** в `shared/ui` [UI-87]. Это вариант listbox: фокус остаётся в поле поиска, а
  активный вариант называет `aria-activedescendant`. Вид у строки один во всех окнах и выпадающих слоях,
  выбирается макетами раздела 0. Форма — компонент рядом с `ListItem` или его режим, решает разбор
  Архитектора. Активный вариант залит `secondary-container`, как уже в `SuggestBox` и `TypePicker`.
  Наведение показывает слой состояния. «Лучшее совпадение» и текущее значение помечаются знаком или
  надписью, своей заливки у них нет.
- **Окна выбора** переходят на неё: `PickerOption` (окна номенклатуры, позиции сметы, ресурса),
  `InvoicePlaceField`, `RefPickerModal`, `BaseCandidatePicker`, `ScopeGroupList` (заголовок уровня станет
  `ListGroupHeader`), `NewNomenclatureStep`. По ответу владельца — и `SuggestBox` с `TypePicker` в
  `shared/ui`, чтобы строк варианта не было две.
- **Списки карточки и окон без listbox** переходят на `ListItem`: элементы массива (`ComplexFields`),
  находки сверки (`ReconciliationsPage`), документы комплекта (`InstancePickerModal`), библиотека
  документов качества и найденное в интернете (`LinkPickerModal`), оглавление формы каталога
  (`document-sets/fields/SectionRail`), уведомления (`NotificationList`). Строка, отмеченная флажком,
  получает заливку выбранного без `aria-current`. Действия строки видны без наведения [UI-84].
- **Сторож** `list-row` расширяется. Он считает вариант `role="option"`, нарисованный вне `shared/ui`, и
  заливку выбранного, собранную в переменную того же компонента. Уровень заводится по факту, а к концу
  change опускается в ноль. Тогда тест пустого уровня (`ui-ratchet-empty-level.test.mjs`) начинает
  сторожить и `list-row`.
- **Решения владельца до кода** (раздел 0): вариант строки выбора (три макета), текущее значение в списке,
  непрочитанное уведомление, оглавление формы, `SuggestBox` и `TypePicker` в этом change.
- **Не входит:**
  - список лет в календаре (`Calendar`, `role="listbox"`) — часть выбора даты (`sharedui-md3-16-surfaces`);
  - пункты меню (`MenuItem`) — `sharedui-md3-13-containers`;
  - раскладка окна выбора номенклатуры (лист карточки, перестройка на узком месте) — `costs-picker-sheet-reflow`;
    здесь меняется только строка;
  - раскладка экрана сверки и центра активности — только строки.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `sharedui`: строка варианта выбора [UI-87], строки списков содержимого карточки [UI-84], сторож
  `list-row` по `role="option"` и заливке в переменной [UI-84].

## Impact

- **Клиент, `shared/ui`:** строка варианта (новый компонент или режим `ListItem`). Для строк карточки —
  статичная строка без цели и отмеченная строка без `aria-current`, если разбор подтвердит, что их нет.
  `ListGroupHeader` — заголовок уровня в `ScopeGroupList`. По ответу 0.4 — `SuggestBox.tsx`,
  `TypePicker.tsx`.
- **Клиент, экраны:** `costs/PickerRows.tsx`, `costs/InvoicePlaceField.tsx`, `costs/NewNomenclatureStep.tsx`,
  `document-sets/fields/RefPickerModal.tsx`, `BaseCandidatePicker.tsx`, `ScopeGroupList.tsx`,
  `InstancePickerModal.tsx`, `RefPickerArchive.tsx`, `ComplexFields.tsx`, `fields/SectionRail.tsx`,
  `document-sets/editor/LinkPickerModal.tsx`, `reconciliations/ReconciliationsPage.tsx`,
  `notifications/NotificationList.tsx`. `PointMatchDialogs.tsx` и `ResourceMatchDialog.tsx` меняются сами,
  через `PickerOption`.
- **Сторожа:** `scripts/ui-ratchet-lists.mjs` и его тест, `ui-baseline.json`, `ui-ratchet-empty-level.test.mjs`.
  Перечень правил в `CLAUDE.md` и `AGENTS.md` правится одним коммитом.
- **Размер:** `ComplexFields.tsx` (981 строка) и `RefPickerModal.tsx` (515) стоят в `file-size-baseline.json`,
  расти им нельзя.
- **Тесты:** `ListItem.test.tsx` (или тест нового компонента), тесты окон выбора, где проверяется клавиатура
  (`aria-activedescendant`, Enter).
- **Живые прогоны:** весь список (`npm run -s test:e2e:ci`), раз затронут `shared/ui` (зонтик, 5.1).
- **Пересечения:** `costs-picker-sheet-reflow` (`NomenclaturePicker`, `PickerRows`); `work-22-web-entry-ui`
  и `sharedui-date-input-back-layer` (`TypePicker`); `core-constant-document-fields` и
  `sharedui-md3-19-requisite-fields` (`ComplexFields`). Правку на новую строку переносит тот, кто
  сливается вторым.
- **Дизайн-система:** `/design-sync` после слияния (пока он не пишет в Claude — заметка в зонтике).
- **Сервер:** не меняется.
- **PR:** два, оба с меткой `version:minor`. PR 1 — строка варианта, сторож и окна выбора. PR 2 — списки
  карточки и `list-row` в ноль.
