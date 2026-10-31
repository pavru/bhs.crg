# Tasks: поля и выбор по MD3, шкала плотности

## 0. До кода

- [x] 0.1 Предусловия слиты:
  - зонтик 0.2 — четыре change перевода самоделок заархивированы;
  - зонтик 2.1, 2.3–2.5 — значки, состояние, форма, текст;
  - для `Select variant="chip"` — PR 2 `sharedui-md3-10-actions` (`Chip menu`).

  Проверка — уровень `native-checkbox` в `ui-baseline.json` равен нулю; 10 PR 2 слит.
  Сверено 31.10.2026 по master `3cf78446`: `native-checkbox` — 0; 10 слит (#1708, #1709) и
  заархивирован (#1710); `sharedui-checkbox-screens` в архиве с 30.10.2026. Нативных радиокнопок — 5
  в 2 файлах, как в design.
- [x] 0.2 Разбор с Дизайнером и Архитектором (слой `shared/ui`, новое правило) — комментарием к задаче
  (`docs/UI.md`, «Новый экран: порядок», шаг 1). Проверка — ссылка на разбор в задаче.
  Сделано 31.10.2026. Сводка — лист «0 Разбор и вопрос» проекта Claude Design
  https://claude.ai/design/p/d208fc78-d214-44e3-ac95-4cdebe612d2e?file=00+%D0%A0%D0%B0%D0%B7%D0%B1%D0%BE%D1%80+%D0%B8+%D0%B2%D0%BE%D0%BF%D1%80%D0%BE%D1%81.html.
  Оба независимо рекомендуют: отбор — чип, «только для чтения» — контур `outline-variant`, плотное поле вне
  сетки — с рамкой, в клетке — без (решение 14). Находки, меняющие план (числа мест, третья категория
  «строка настроек 48 / поле 40», `Radio` на `useRadioGroup` вместо нативного, слой флажка на `input`,
  знак ошибки через ширину хвоста, `native-radio` отдельным модулем, PR 1 надвое, правило `raw-field` в
  зонтике, e2e под угрозой), вносятся через `/opsx:update` в 0.4.
- [x] 0.3 Три макета в Claude Design — разные решения:
  - (А) текст поля 16 в формах, плотное поле 40 с рамкой в клетках, `Select` отбора — чипом;
  - (Б) текст 14 везде, плотное поле 40 без рамки с подчёркиванием, как в Г;
  - (В) текст 16, `Select` отбора — плотным полем 40 с рамкой, без чипа.

  Каждый — с вариантом «только для чтения» (design) и знаком на ручке переключателя или без него. На
  каждом, в обеих темах, 1366×768 и 375 px:
  - форма счёта;
  - окно с полями и ошибкой;
  - строка отбора реестра;
  - клетка матрицы разноски;
  - флажки в таблице;
  - группа радиокнопок;
  - переключатели свойств типа.

  Проверка — ссылки на три макета в задаче.
  Сделано 31.10.2026, проект «Поля и выбор MD3 — плотность и текст поля (В5, В6)». По разбору 0.2
  добавлены поповер условия отбора и строки настроек в окне; поле в клетке сетки во всех трёх — без
  рамки (решение 14), так что Б отличается полем без рамки вне сетки. «Только для чтения» (а/б/в) и знак
  на ручке — переключателями на каждом листе:
  - (А) https://claude.ai/design/p/d208fc78-d214-44e3-ac95-4cdebe612d2e?file=%D0%90+-+16+%D0%B8+%D1%87%D0%B8%D0%BF.html
  - (Б) https://claude.ai/design/p/d208fc78-d214-44e3-ac95-4cdebe612d2e?file=%D0%91+-+14+%D0%B8+%D0%BF%D0%BE%D0%B4%D1%87%D1%91%D1%80%D0%BA%D0%B8%D0%B2%D0%B0%D0%BD%D0%B8%D0%B5.html
  - (В) https://claude.ai/design/p/d208fc78-d214-44e3-ac95-4cdebe612d2e?file=%D0%92+-+16+%D0%B1%D0%B5%D0%B7+%D1%87%D0%B8%D0%BF%D0%B0.html
- [x] 0.4 Выбор владельца (pavru) и решения В5, В6 записаны в зонтике с датой. Выбор перенесён в
  `design.md` и [UI-69], [UI-70] через `/opsx:update sharedui-md3-11-inputs`. Проверка —
  `openspec validate sharedui-md3-11-inputs --strict`; в design назван макет и вариант.
  Сделано 31.10.2026. Выбор — вариант А:
  - «только для чтения» — контур `outline-variant`;
  - знака на ручке переключателя нет;
  - В5 — да, со строкой настроек 48/40.

  Тем же `/opsx:update` вписаны три ответа владельца:
  - радиокнопка — на `useRadioGroup`;
  - `raw-field` — в 11, ноль — в 17;
  - «Строк на странице» — `inline`.

  Вписаны и находки разбора 0.2 — design, [UI-69] и разделы 1–2 ниже.
- [x] 0.5 После архива `sharedui-checkbox-screens` — дельта дополнена через `/opsx:update`: MODIFIED
  [UI-40], в сценарии клетки таблицы `Checkbox size="sm"` → `Checkbox` 18 px. Проверка —
  `openspec validate … --strict`; в дельте нет `size="sm"` у флажка.
  Сделано 31.10.2026 вместе с 0.4.
- [x] 0.6 Зонтик: правило `raw-field` вписано в таблицу сторожей D11, перевод самодельных полей до нуля — в
  состав 17 (`/opsx:update sharedui-md3-00-epic`). Проверка — `openspec validate sharedui-md3-00-epic
  --strict`; D11 называет `raw-field`, у 17 есть задача перевода.
  Сделано 31.10.2026: строки `native-radio` (уровень 2 до 13) и `raw-field` в D11, пункт 4.1 зонтика
  называет `raw-field` для 17. Задачу в `tasks.md` самого 17 вносит его сессия.

## 1. PR 1a — вид оболочки поля

- [x] 1.1 `fieldBox(density)` рядом с `OutlinedField` — высота, отступы и роль текста. `TextField`, `Select`
  с `label`, `TypePickerField`, `DateField` и `TimeField` берут их отсюда. `OutlinedField`:
  - форма 4 (`rounded-xs`);
  - подпись в покое — `body-large`; в вырезе и текст под полем — `body-small` `on-surface-variant`;
  - обводка `outline` / `on-surface` / 2 px `primary`;
  - «только для чтения» — обводка `outline-variant` без подложки, `-z-10 isolate` уходит.

  Свойство `density: 'form' | 'dense'` [UI-69]. У `dense` нет выреза, подпись — `sr-only` `<label>`, рамка
  есть. Проверка — vitest `OutlinedField`:
  - у `form` высота 56 и текст `body-large`;
  - у `dense` — 40, `body-medium`, выреза нет, а `getByLabelText` находит поле;
  - у `readOnly` нет подложки.
- [x] 1.2 Знак ошибки ставит оболочка — перед `trailing`, `aria-hidden` [UI-69]:
  - ширину хвоста оболочка отдаёт переменной CSS, контролы берут из неё отступ;
  - стрелка `Select` переезжает в слот оболочки;
  - у `form` знак 24, текст ошибки — под полем; у `dense` знак 20 с `Tooltip`;
  - текст ошибки — в `aria-describedby`;
  - `SegmentProblemMark` у `DateInput` сведён с этим знаком.

  Сделано так: место знака оболочка отдаёт переменной `--field-mark`. Знак — один компонент
  `FieldErrorMark`. У `DateField` хвост (крестик, календарь) живёт внутри `DateInput`, поэтому оболочке
  говорят `markInside`: знак она отдаёт контролу через `FieldMarkContext`, и `DateInput` ставит его перед
  хвостом. Когда знак есть и какого он размера, решает только оболочка. `SegmentProblemMark` рисует тот же
  `FieldErrorMark` плотного вида. Слот знака стоит по первой строке поля (у `TextAreaField` — не посреди
  текста), подпись в покое кончается до знака и хвоста. `aria-describedby` собирает `fieldDescribedBy`.

  Проверка — vitest:
  - при `error` знак есть и стоит перед `trailing`;
  - без `error` знака нет;
  - у поля даты с ошибкой ровно один знак;
  - диктор получает текст через `aria-describedby`.
- [x] 1.3 Поля на оболочке — текст `body-large` в `form`, `body-medium` в `dense`.
  - `compact` у `SuggestField` и `DateInput` не меняется: вид клетки — у 14.
  - У `DateField` и `TimeField` отключённое — `disabled-content` [UI-64] вместо `opacity-50`.
  - `NumberInput` (7 мест, все в `EditGrid`) — сразу видом клетки 14 [UI-69]: плотное поле 40 без рамки,
    подчёркивание, фокус 2 px `primary`, единица суффиксом.

  Проверка — `npx tsc -b`; vitest полей зелёный; в `NumberInput.tsx` нет своей рамки.
- [x] 1.4 `Select` с `label` — exposed dropdown: стрелка поворачивается при открытии [UI-69]. Проверка —
  `Select.test.tsx`: `data-state=open` поворачивает стрелку; клавиатура выбора прежняя.

## 2. PR 1b — выбор без подписи и плотность

- [x] 2.1 `Select` без `label`: `size` удалён [UI-69].
  - Тип — объединение: обязателен один из `variant="chip"`, `variant="inline"`, `density="dense"`.
  - `chip` — `RS.Trigger asChild` → `Chip`; требует `emptyItem`.
  - В `Chip` дописаны поворот стрелки по `data-state=open` и признак `selected`.

  Проверка — `Select.test.tsx`:
  - у чипа роль `combobox`;
  - чип выбран, когда значение не пусто;
  - стрелка поворачивается;
  - без `emptyItem` `chip` не собирается (`tsc`).

  Сделано так: плотное поле без подписи — то же поле на оболочке `OutlinedField`, подпись из `aria-label`
  (только для диктора), и ширину ему задаёт обёртка — `className` ложится на неё. Выбранный чип с меню
  помечен `data-selected`: `aria-pressed` у него нет, а роль `combobox` состояния «выбран» не знает.
  Стрелка чипа поворачивается по `data-state=open` триггера (`group/chip`).
- [x] 2.2 `size` удалён у `DateInput`, `DateRange` и `TypePickerField`. Места переведены по категориям
  design, «Выбор без подписи — по месту»:
  - отбор (5) — `chip`;
  - строки настроек и шапки колонок с вводом — `dense`;
  - внутри фразы и «Строк на странице» — `inline`;
  - поле с подписью снаружи — `label`.

  Пять мест задают высоту или рамку через `className` — их переводят поимённо: `SiteCostsPage:92`,
  `TypeGroupAccordion:120`, `RowFilterValue:68`, `PrimitiveInput:91`, `FieldMappingPage:333`. Пометка
  рамкой в `FieldBuilder:565` переходит на способ оболочки [UI-69].

  Проверка:
  - `npx tsc -b`;
  - `rg '<(Select|DateInput|DateRange|TypePickerField)[^>]*size=' src/client/src` — пусто;
  - в пяти местах нет `h-8`, `min-h-7` и своих `border-*`;
  - перечень решений по местам — в PR.

  Сделано так: `DateInput` вместо `size` — `density="dense"` (одна рамка 40, форма 4; «только для чтения»
  — обводкой, без подложки); `DateRange` — всегда плотные поля; `TypePickerField` без подписи — плотное
  поле на оболочке. Пять мест: «Стройка» `SiteCostsPage` — чип; `TypeGroupAccordion`, `RowFilterValue` —
  плотное без своих `h-8` / `min-h-7`; `PrimitiveInput` и `FieldMappingPage` — `invalid` плотного поля
  вместо своей рамки `border-error` / `border-warning`. Пометка `FieldBuilder` (`border-primary-container!`)
  — словами в пункте «значения нет»: «от родителя: …». Текстовое поле условия отбора (`FIELD_CLS`) —
  коробкой плотного поля, иначе в строке условия рядом стояли бы 28 и 40.
- [x] 2.3 Поправлены подсказки `instead` храповика, которые называют удалённый `size` (`select`,
  `native-date`, `date-frame`), и фикстуры с `DateInput size="sm"` в `ui-ratchet.test.mjs`. Проверка:
  - `node --test scripts/ui-ratchet.test.mjs` зелёный;
  - в подсказках `ui-ratchet-lib.mjs` нет `size="sm"`, `h-9`, `h-7`.

  Сделано так: `ui-ratchet.test.mjs` — тест vitest, запускается `npx vitest run scripts/ui-ratchet.test.mjs`
  (`node --test` его не читает). `size="sm"` в подсказке `native-checkbox` остаётся до 3.1: это размер
  `Checkbox`, и он пока есть.
- [x] 2.4 Живые прогоны, чувствительные к раскладке, правятся тем же PR по факту прогона:
  - `site-costs-smoke` — строка выше сгиба;
  - `allocation-matrix-smoke` — прокрутка по ширине;
  - `invoice-layout-checks` — одна строка и обрезка;
  - `invoice-payment-smoke` — календарь на 375;
  - `table-chips-smoke` — поповер условия.

  Проверка — каждое изменённое ожидание названо в PR, весь список зелёный.

  Сделано так: пять названных прогонов раскладкой не задеты. Поправлены два других, по факту прогона:
  `dialogs-smoke` искал выбор типа документа качества атрибутом `aria-label` — теперь ролью и именем (имя
  плотному полю даёт подпись оболочки); `table-chips-smoke` уходил из поля «по» двумя Tab — у плотного поля
  даты появился крестик, Tab стало три. По ревью крестик в отборе снят (`noClear`: значение снимает крестик
  чипа), и прогон вернулся к двум Tab.
- [x] 2.5 `docs/UI.md`:
  - «Плотность» [UI-70]:
    - таблица D7 с высотами и строкой настроек 48/40;
    - «кнопкам не применяется»;
    - ссылка на «Отступления от MD3»;
  - в «Отступления от MD3» — `inline`;
  - «Язык формы» [UI-69] — строки:
    - «Поле ввода»: два размера, «только для чтения» — контуром;
    - «Выбор из списка»: три категории, чип в отборе;
    - «Дата» и «Подсказка»;
  - абзац обвязки даты [UI-69].

  Проверка — ревью; в `docs/UI.md` нет «рабочий и плотный», «подпись стоит над полем», `h-9`, `h-7`.

## 3. PR 2 — флажок, радиокнопка, переключатель, сторожа

- [ ] 3.1 `Checkbox` [UI-69]:
  - `input` 40×40: `appearance: none`, `state-layer`, поля `-m-[11px]` — след в раскладке 18;
  - квадрат 18 — соседний `span` по `peer-*`: форма 2, обводка 2 px, `primary` у выбранного и частичного,
    `error`;
  - «только для чтения» — обводка `outline-variant` вместо `opacity-70`;
  - `sm` удалён из типа.

  Проверка — vitest:
  - `indeterminate` по-прежнему у `input`;
  - роль `checkbox`;
  - `aria-readonly` работает;
  - `getBoundingClientRect` следа — 18.

  `npx tsc -b` находит места `size="sm"`.
- [ ] 3.2 `Radio` и `RadioGroup` на `useRadioGroup` [UI-69]:
  - пункт — `button role="radio"` с `RadioMark` 20 и слоем 40;
  - цель нажатия — строка с подписью;
  - группа — `radiogroup` с видимым именем, в строку или столбиком.

  Проверка — vitest:
  - стрелка двигает фокус по APG;
  - Tab входит в группу один раз;
  - у группы есть имя;
  - у пункта есть `aria-checked`.
- [ ] 3.3 На `RadioGroup` переведены:
  - `MaterializationDialog` — у обеих групп появляется видимое имя;
  - `BackupSection`;
  - рисованные кружки `AppearanceSection` и `VariantPicker`. У `VariantPicker` с `confirm` фокус
    движется без выбора.

  Проверка — e2e `fields-smoke`, `dialogs-smoke` проходят без правок ожиданий `aria-checked`.
- [ ] 3.4 Модуль `scripts/ui-ratchet-inputs.mjs` с тестом `ui-ratchet-inputs.test.mjs` [UI-69]:
  - `native-radio` — `<input type="radio">` и `role="radio"` вне `shared/ui`. Уровень 2: `SitesFilter` и
    `LayoutChooser` — пункты меню, их переводит 13.
  - `raw-field` — сырой `<input>` или `<textarea>` со своей рамкой вне `shared/ui`. Уровень — текущий.
  - В подсказке `native-checkbox` больше нет `size="sm"`.

  Проверка — тест модуля:
  - фикстура с `<input type="radio">` красная, со своим `role="radio"` — тоже;
  - с `RadioGroup` — зелёная;
  - фикстура `raw-field` красная;
  - `npm run ui:ratchet` и `FileSizeRatchet` зелёные.
- [ ] 3.5 `Switch` [UI-69]:
  - 52×32, ручка 16 → 24, при нажатии 28;
  - у выключенного — контур 2 px;
  - знака на ручке нет;
  - `sm` удалён;
  - в `PropertiesEditor` и `InheritedFieldsPanel` — в конце строки высотой 48.

  Проверка — vitest: `aria-checked`, `role="switch"`, знака на ручке нет; `npx tsc -b`.
- [ ] 3.6 Перечень правил храповика в `CLAUDE.md` и `AGENTS.md` дополнен новыми `native-radio` и
  `raw-field` — одним коммитом. В `docs/UI.md`:
  - строки «Флажок», «Радиокнопка» (с границей от `SegmentedButton`) и «Переключатель»;
  - оба правила — в «Кто сторожит».

  Проверка — `dotnet test … --filter AgentRulesCopy` зелёный.
- [ ] 3.7 `/design-sync` (D12): превью `TextField`, `Select`, `Checkbox`, `Switch` и README дизайн-системы —
  по новому виду. Проверка — README называет два размера поля, три вида выбора без подписи и
  `RadioGroup`.

## 4. Верификация

- [ ] 4.1 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный (каждый PR).
- [ ] 4.2 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста (каждый PR).
- [ ] 4.3 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FileSizeRatchet|AgentRulesCopy"`
  — зелёный.
- [ ] 4.4 `npm run -s test:e2e:ci` — весь список, зелёный (каждый PR; зонтик, 5.1).
- [ ] 4.5 Снимки 5–7 ключевых экранов до и после, обе темы, 1366×768 и 375 px (зонтик, 5.2). Проверка —
  описание PR.
- [ ] 4.6 Все три PR — с меткой `version:minor`. В описании — макет, выбранный вариант А и ссылка на проект
  макетов.
