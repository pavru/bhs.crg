# Tasks: поля и выбор по MD3, шкала плотности

## 0. До кода

- [ ] 0.1 Предусловия слиты:
  - зонтик 0.2 — четыре change перевода самоделок заархивированы;
  - зонтик 2.1, 2.3–2.5 — значки, состояние, форма, текст;
  - для `Select variant="chip"` — PR 2 `sharedui-md3-10-actions` (`Chip menu`).

  Проверка — уровень `native-checkbox` в `ui-baseline.json` равен нулю; 10 PR 2 слит.
- [ ] 0.2 Разбор с Дизайнером и Архитектором (слой `shared/ui`, новое правило) — комментарием к задаче
  (`docs/UI.md`, «Новый экран: порядок», шаг 1). Проверка — ссылка на разбор в задаче.
- [ ] 0.3 Три макета в Claude Design — разные решения:
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
- [ ] 0.4 Выбор владельца (pavru) и решения В5, В6 записаны в зонтике с датой. Выбор перенесён в
  `design.md` и [UI-69], [UI-70] через `/opsx:update sharedui-md3-11-inputs`. Проверка —
  `openspec validate sharedui-md3-11-inputs --strict`; в design назван макет и вариант.
- [ ] 0.5 После архива `sharedui-checkbox-screens` — дельта дополнена через `/opsx:update`: MODIFIED
  [UI-40], в сценарии клетки таблицы `Checkbox size="sm"` → `Checkbox` 18 px. Проверка —
  `openspec validate … --strict`; в дельте нет `size="sm"` у флажка.

## 1. PR 1 — поля, выбор, плотность

- [ ] 1.1 `OutlinedField`: форма 4, подпись и текст под полем `body-small` `on-surface-variant`,
  обводка `outline` / `on-surface` / 2 px `primary`, вид «только для чтения» по выбору 0.4. Свойство
  `density: 'form' | 'dense'` [UI-69]. Проверка — vitest `OutlinedField`:
  - у `dense` нет выреза и высота 40;
  - у `form` — 56.
- [ ] 1.2 Знак ошибки — оболочкой, перед `trailing`, `aria-hidden`; текст ошибки — в
  `aria-describedby` [UI-69]. Проверка — vitest:
  - при `error` знак есть и стоит перед `trailing`;
  - без `error` знака нет;
  - диктор получает текст через `aria-describedby`.
- [ ] 1.3 Поля на оболочке: `TextField`, `TextAreaField`, `DateField`, `TimeField`, `TypePickerField`,
  `SuggestField` (через `TextField`) — текст `body-large` в `form`, `body-medium` в `dense`; `compact` →
  `dense`. `NumberInput` (своя рамка ~24 px `text-xs`, 7 мест) — плотное поле 40 с единицей суффиксом
  [UI-69]. Проверка — `npx tsc -b`; vitest полей зелёный; в `NumberInput.tsx` нет своей рамки.
- [ ] 1.4 `Select` с `label` — exposed dropdown: стрелка поворачивается при открытии [UI-69]. Проверка —
  `Select.test.tsx`: `data-state=open` поворачивает стрелку; клавиатура выбора прежняя.
- [ ] 1.5 `Select` без `label`: `size` удалён, есть `variant="chip"` и `density="dense"`; `inline` —
  как был. `DateInput size` → `chip` или `dense`. 29 мест `sm` и места `md` переведены по месту [UI-69].
  Проверка:
  - `rg '<(Select|DateInput)[^>]*size=' src/client/src` — пусто;
  - перечень решений — в PR;
  - `npx tsc -b`.
- [ ] 1.6 Живые прогоны с высотами полей и выбора правятся тем же PR: `fields-smoke`,
  `allocation-matrix-smoke`, `invoice-layout-checks`, `invoice-payment-smoke` — по факту прогона.
  Проверка — каждое ожидание названо в PR, весь список зелёный.
- [ ] 1.7 `docs/UI.md`:
  - «Плотность» — таблица D7 с высотами, «кнопкам не применяется», ссылка на раздел отступлений [UI-70];
  - «Язык формы» — строки «Поле ввода» и «Выбор из списка» (два размера, чип в отборе, `inline` —
    отступление) [UI-69].

  Проверка — ревью; в `docs/UI.md` нет «рабочий и плотный», `h-9`, `h-7`.

## 2. PR 2 — флажок, радиокнопка, переключатель

- [ ] 2.1 `Checkbox`: нативный `input` с `appearance: none`, рисунок 18 px, форма 2, обводка 2 px,
  `primary` у выбранного и частичного, `error`, слой 40. `sm` удалён из типа [UI-69]. Проверка — vitest:
  - `indeterminate` по-прежнему у `input`;
  - роль `checkbox`;
  - `aria-readonly` работает.

  `npx tsc -b` находит места `size="sm"`.
- [ ] 2.2 `Radio` и `RadioGroup`: нативный `input type="radio"` с рисунком 20 px, слой 40,
  `radiogroup` с именем [UI-69]. Проверка — vitest: стрелка выбирает следующий, Tab входит в группу один
  раз, у группы есть имя.
- [ ] 2.3 `MaterializationDialog` и `BackupSection` переведены на `RadioGroup`. Правило `native-radio` в
  `ui-ratchet-lib.mjs` — по образцу `native-checkbox`, уровень 0 [UI-69]. Проверка — `ui-ratchet.test.mjs`:
  - фикстура с `<input type="radio">` красная;
  - с `Radio` — зелёная;
  - `npm run ui:ratchet` зелёный.
- [ ] 2.4 `Switch`: 52×32, ручка 16 → 24 (28 при нажатии), контур 2 px у выключенного; `sm` удалён
  [UI-69]. Проверка — vitest: `aria-checked`, `role="switch"`; `npx tsc -b`.
- [ ] 2.5 Перечень правил храповика в `CLAUDE.md` и `AGENTS.md` (новое `native-radio`) — одним
  коммитом. В `docs/UI.md` — строки «Флажок», «Радиокнопка», «Переключатель» и `native-radio` в «Кто
  сторожит». Проверка — `dotnet test … --filter AgentRulesCopy` зелёный.
- [ ] 2.6 `/design-sync` (D12): превью `TextField`, `Checkbox` и README дизайн-системы — по новому виду.
  Проверка — README называет два размера поля и `RadioGroup`.

## 3. Верификация

- [ ] 3.1 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный (каждый PR).
- [ ] 3.2 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста (каждый PR).
- [ ] 3.3 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FileSizeRatchet|AgentRulesCopy"`
  — зелёный.
- [ ] 3.4 `npm run -s test:e2e:ci` — весь список, зелёный (каждый PR; зонтик, 5.1).
- [ ] 3.5 Снимки 5–7 ключевых экранов до и после, обе темы, 1366×768 и 375 px (зонтик, 5.2). Проверка —
  описание PR.
- [ ] 3.6 Оба PR с меткой `version:minor`. В описании — макет, выбранный вариант и ссылка.
