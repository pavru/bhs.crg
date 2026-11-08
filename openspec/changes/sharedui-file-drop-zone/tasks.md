# Tasks

## 0. Макеты и выбор

- [x] 0.1 Три макета в Claude — холст «Площадка файла · FileDropZone»
  (https://claude.ai/artifact/DkC1pS2r15uGtos1SS5m56), листы 0, А, Б, В; выбор владельца — **Б «Площадка»**
  (08.11.2026), записан в design D1. [UI-89] Проверка — D1 называет макет, вариант и дату.

## 1. Проверка файла [UI-89]

- [x] 1.1 `shared/ui/fileDrop.ts`: постоянная `FOLDER_REFUSAL` и `zoneRefusal(dropped, check)` — порядок папка →
  несколько → `check`, отказ «имя — причина» (design D2). `features/costs/scanBatch.ts` берёт `FOLDER_REFUSAL`
  оттуда, текст не меняется. Проверка:
  - `fileDrop.test.ts`: каждая причина и порядок; `check` зовётся только для одного файла и не для папки;
  - тесты `scanBatch` зелёные без правки ожиданий.

## 2. Компонент [UI-89]

- [x] 2.1 `shared/ui/FileDropZone.tsx` по design D1–D2 с доккомментарием (вариант Б, ссылка на макет). Состояния:
  - покой, наготове, отпустите (кнопка `invisible`, не убрана);
  - выбран — строка 72;
  - отказ — `role="alert"` под площадкой, узел с `key` попытки;
  - загрузка — `CircularProgress` 40, `dropEffect = 'none'`, кнопка выключена, без «наготове»;
  - выключена — причина строкой, без «наготове»;
  - телефон (`(pointer: coarse)`) — без «перетащите», кнопка — `pickLabel`.

  «Над площадкой» — флаг по `relatedTarget`, сброс на `drop` и при погасшем `useFileDragActive`. У кнопки
  `aria-describedby` на `label` и `hint` (имя `label` — только у `input`, design D2), живая область `polite`
  объявляет выбранный файл и `busy`. Проверка — `FileDropZone.test.tsx`:
  - бросок и выбор отдают один и тот же файл;
  - тот же файл второй раз подряд;
  - отказ `check`, нескольких файлов, папки — `onFile` не зван, текст «имя — причина» виден;
  - тот же отказ дважды — узел alert новый;
  - следующий годный файл снимает отказ;
  - в загрузке бросок не принят, «наготове» нет;
  - тащат не файл — площадка не меняется;
  - выбранный файл заменяется броском;
  - `dragleave` за край снимает «отпустите», `drop` и `dragend` в окне — тоже;
  - у кнопки описание `label` и `hint`, живая область называет выбранный файл;
  - имя `label` — только у скрытого поля: `getByLabelText` находит ровно его.
- [x] 2.2 Храповик и словарь темы: только роли MD3, шкала радиусов, `Button` и `Icon` из `shared/ui`, ни
  `dark:`, ни своих размеров. Проверка — `npm run ui:ratchet` без роста и `npm test`
  (`scripts/theme-classes.test.mjs`) зелёные.

## 3. Потребитель `ImportDialog` [UI-89] [PLAN-6]

- [x] 3.1 `features/planning/estimates/ImportDialog.tsx`: `FilePick` удалён, на его месте `FileDropZone` (design D3).
  Вместе с ним — `estimateFileRefusal` («файл пуст»; «не .gsfx или .xml» только при чужом расширении) и
  `busy`, пока идёт `start`. Шаги, `isDirty` и имя «Файл сметы» те же. Проверка:
  - vitest: `estimateFileRefusal` — пустой, `.pdf`, без расширения, `.GSFX`; окно в `start.isPending` не
    принимает брошенный файл;
  - `FilePick`, `border-dashed` и `carriesFiles` в файле больше не встречаются;
  - файл похудел (FileSizeRatchet).
- [x] 3.2 Живой прогон `planning-estimates-smoke.mjs`: выбор по «Файл сметы» как был, и новый шаг — `.pdf` даёт
  отказ «не .gsfx или .xml», разбор не начинается. Стенд — свои порты (5069/5183), модуль `plan` включён.
  Проверка — `npm run -s test:e2e:planning-estimates` зелёный на стенде.

## 4. Документы [UI-89]

- [x] 4.1 `docs/UI.md`: строка таблицы компонентов «Площадка файла — `FileDropZone`» и раздел «Площадка файла»:
  когда площадка, когда скрытый выбор из «⋮» (`ScanFileInput`), когда цель на всё окно (14) (design D4).
  Проверка — текст совпадает со спецификацией и design D1.

## 5. Верификация

- [x] 5.1 `cd src/client && npx tsc -b`, `npm test`, `npm run lint:ratchet`, `npm run ui:ratchet` — зелёные.
- [x] 5.2 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — файлы
  не перешагнули порог; уровень `ImportDialog.tsx`, если он в `src/file-size-baseline.json`, опущен.

## Workflow follow-up

- Код — отдельным PR с меткой `version:minor`, `<Version>` не трогать. После коммита бота с версией — архив
  `/opsx:archive` отдельным PR и отметка 2.4a в `costs-invoice-screen-00-epic/tasks.md` (`[x]` и строка «слит
  #…, заархивирован …»).
- Окна «Приложить файл…» и «Заменить файл…» на `FileDropZone` — PR 2 у `costs-invoice-screen-14-file-drop`,
  не здесь.
