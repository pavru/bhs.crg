# Tasks

## 0. Макеты и выбор

- [x] 0.1 Три макета в Claude — холст «Площадка файла · FileDropZone»
  (https://claude.ai/artifact/DkC1pS2r15uGtos1SS5m56), листы 0, А, Б, В; выбор владельца — **Б «Площадка»**
  (08.11.2026), записан в design D1. [UI-89] Проверка — D1 называет макет, вариант и дату.

## 1. Проверка файла [UI-89]

- [ ] 1.1 `shared/ui/fileDrop.ts`: `matchesAccept(file, accept)` и `refusalOf(dropped, rules)`. Порядок причин —
  папка → несколько → пустой → вид → размер → `check`; слова отказа — по спецификации (design D2). Проверка —
  `fileDrop.test.ts`:
  - `.ext` без учёта регистра, `type/*`, точный тип, пустой `accept`;
  - каждая причина отказа и порядок причин;
  - предел в словах `formatBytes`.

## 2. Компонент [UI-89]

- [ ] 2.1 `shared/ui/FileDropZone.tsx` по design D1–D2 с доккомментарием (вариант Б, ссылка на макет). Состояния:
  покой, наготове, отпустите, выбран (строка 72), отказ (`role="alert"` под площадкой), загрузка
  (`CircularProgress` 40, `dropEffect = 'none'`, кнопка выключена), выключена (причина строкой), телефон
  (`(pointer: coarse)`: без «перетащите», кнопка — `pickLabel`). Проверка — `FileDropZone.test.tsx`:
  - бросок и выбор отдают один и тот же файл;
  - тот же файл второй раз подряд;
  - отказ по виду, размеру, нескольким, папке — `onFile` не зван, текст виден;
  - следующий годный файл снимает отказ;
  - в загрузке бросок не принят;
  - тащат не файл — площадка не меняется;
  - выбранный файл заменяется броском;
  - у скрытого поля имя `label`.
- [ ] 2.2 Храповик и словарь темы: только роли MD3, шкала радиусов, `Button` и `Icon` из `shared/ui`, ни
  `dark:`, ни своих размеров. Проверка — `npm run ui:ratchet` без роста и `npm test`
  (`scripts/theme-classes.test.mjs`) зелёные.

## 3. Потребитель `ImportDialog` [UI-89] [PLAN-6]

- [ ] 3.1 `features/planning/estimates/ImportDialog.tsx`: `FilePick` удалён, на его месте `FileDropZone` (design D3);
  шаги, `isDirty` и имя «Файл сметы» те же. Проверка:
  - vitest окна зелёный;
  - `FilePick`, `border-dashed` и `carriesFiles` в файле больше не встречаются;
  - файл похудел (FileSizeRatchet).
- [ ] 3.2 Живой прогон `planning-estimates-smoke.mjs`: выбор по «Файл сметы» как был, и новый шаг — `.pdf` даёт
  отказ «не файл сметы», разбор не начинается. Стенд — свои порты (5069/5183), модуль `plan` включён.
  Проверка — `npm run -s test:e2e:planning-estimates` зелёный на стенде.

## 4. Документы [UI-89]

- [ ] 4.1 `docs/UI.md`: строка таблицы компонентов «Площадка файла — `FileDropZone`» и раздел «Площадка файла»:
  когда площадка, когда скрытый выбор из «⋮» (`ScanFileInput`), когда цель на всё окно (14) (design D4).
  Проверка — текст совпадает со спецификацией и design D1.

## 5. Верификация

- [ ] 5.1 `cd src/client && npx tsc -b`, `npm test`, `npm run lint:ratchet`, `npm run ui:ratchet` — зелёные.
- [ ] 5.2 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — файлы
  не перешагнули порог; уровень `ImportDialog.tsx`, если он в `src/file-size-baseline.json`, опущен.

## Workflow follow-up

- Код — отдельным PR с меткой `version:minor`, `<Version>` не трогать. После коммита бота с версией — архив
  `/opsx:archive` отдельным PR и отметка 2.4a в `costs-invoice-screen-00-epic/tasks.md` (`[x]` и строка «слит
  #…, заархивирован …»).
- Окна «Приложить файл…» и «Заменить файл…» на `FileDropZone` — PR 2 у `costs-invoice-screen-14-file-drop`,
  не здесь.
