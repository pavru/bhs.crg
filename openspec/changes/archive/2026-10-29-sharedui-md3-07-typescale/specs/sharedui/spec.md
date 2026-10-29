# Spec Delta: sharedui

## ADDED Requirements

### Requirement: [UI-67] Текст — роль MD3 целиком
Текст интерфейса SHALL задаваться одной из 15 ролей MD3 — display, headline, title, body, label, каждая
large, medium и small. Роль задаёт размер, высоту строки, межбуквенный интервал и начертание одним
классом `text-<роль>` (`text-body-medium`, `text-label-small`). Значения ролей MUST совпадать с базовой
шкалой MD3. Явно заданное начертание рядом с ролью MUST брать верх над начертанием роли. Заголовок
выбранного объекта или раздела MUST NOT быть крупнее заголовка страницы.

#### Scenario: Основной текст
- **WHEN** элемент несёт `text-body-medium`
- **THEN** текст 14 px, строка 20 px, начертание 400

#### Scenario: Подпись
- **WHEN** элемент несёт `text-label-small`
- **THEN** текст 11 px, строка 16 px, начертание 500

#### Scenario: Заголовок сводки
- **WHEN** элемент несёт `text-title-large`
- **THEN** текст 22 px, строка 28 px, начертание 400

#### Scenario: Начертание рядом с ролью
- **WHEN** элемент несёт `text-body-small font-medium`
- **THEN** размер и строка — от роли, начертание — 500

#### Scenario: Выбранный объект рядом со списком
- **WHEN** страница с шапкой 20 px показывает заголовок выбранной роли или документа
- **THEN** он `text-title-medium` (16/24/500), а не `text-title-large` (22 px)

### Requirement: [UI-67] Только роль — без ступеней, капса и начертаний вне MD3
В интерфейсе MUST NOT быть:
- ступеней размера `text-xs`, `text-sm`, `text-base`, `text-lg` и крупнее, `text-caption`, `text-note`;
- произвольного размера `text-[…]`;
- начертаний `font-semibold` и `font-bold`;
- `uppercase` и своего `tracking-*`;
- тех же свойств обходом: веса числом (`font-[600]`), свойства в скобках (`[letter-spacing:…]`; кроме
  `[font-weight:inherit]`, которым клетка-заголовок снимает жирность браузера), стиля объектом
  (`fontWeight`, `letterSpacing`, `textTransform`, `fontSize` строкой).

Их считает правило храповика `legacy-type` по файлам всего `src/`, уровень MUST только опускаться.
Отказ называет выход — роль MD3 и раздел «Текст» `docs/UI.md`.

#### Scenario: Новая ступень Tailwind
- **WHEN** в файле появляется `text-xs` или `font-semibold`
- **THEN** `npm run ui:ratchet` отказывает по правилу `legacy-type` и называет роли (`text-body-small`,
  `text-label-medium`, `text-title-small`)

#### Scenario: Обход именем
- **WHEN** в файле появляется `font-[600]`, `[text-transform:uppercase]` или `style={{ fontWeight: 600 }}`
- **THEN** `npm run ui:ratchet` отказывает по правилу `legacy-type`

#### Scenario: Произвольный размер
- **WHEN** в файле появляется `text-[13px]`
- **THEN** правило `text-size` отказывает и в подсказке называет роли MD3, а не прежние ступени

#### Scenario: Микрозаголовок
- **WHEN** подпанель раздела показывает заголовок группы пунктов
- **THEN** он `text-title-small` (14/20/500) строчными, без капса и разрядки

### Requirement: [UI-67] Межбуквенный интервал — по роли, везде
Межбуквенный интервал текста SHALL быть интервалом его роли MD3 (body-medium — 0,25 px, label-small —
0,5 px) — в форме, в клетках и шапке таблицы данных и сетки правки одинаково (решение владельца В10
от 29.10.2026). Своего интервала у таблиц MUST NOT быть.

#### Scenario: Текст формы
- **WHEN** в форме показан текст `text-body-medium`
- **THEN** его интервал — 0,25 px

#### Scenario: Клетка таблицы
- **WHEN** в клетке `DataGrid` или `EditGrid` показан текст `text-body-medium`
- **THEN** его интервал — 0,25 px, как у той же роли в форме

### Requirement: [UI-67] Ступени `note` нет
Сообщение формы, пояснение и строка расклада SHALL быть ролью body: `body-medium` (14) или `body-small`
(12) по месту. Ступени 13 px MUST NOT быть ни в теме, ни в коде.

#### Scenario: Сообщение формы
- **WHEN** под полем показан отказ сервера или «считаем…»
- **THEN** текст — `text-body-small` или `text-body-medium`, и в теме нет `--text-note`
