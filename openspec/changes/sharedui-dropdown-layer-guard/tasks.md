# Tasks: «+ Вставить поле» — слой с порталом; сторож ручных выпадающих списков

Один PR с «Closes #1537». Исправление (bug) — PATCH, метка `version:minor` не нужна. Идёт раньше
`sharedui-page-menus-radix` (#1538).

## 0. До кода

- [ ] 0.1 Разбор формы правила `manual-dropdown` с Архитектором (D2) — комментарием к issue. Проверка —
  ссылка на комментарий в описании PR

## 1. Список «+ Вставить поле»

- [ ] 1.1 `features/settings/FieldBuilder.tsx`: список → Radix `Popover` с порталом в body, вид слоя
  `bg-surface` / `border-stroke` / `shadow16`, фокус в фильтр при открытии, на кнопку — при закрытии,
  в `textarea` — после выбора [UI-49]. Проверка — тест `FieldBuilder`: Esc закрывает список и не зовёт
  закрытие окна; выбор вставляет `get("<ключ>")` в позицию курсора
- [ ] 1.2 Живой прогон `types`: окно «Новый тип документа» → расчётное поле → «+ Вставить поле»;
  `elementFromPoint` в центре последнего пункта — внутри списка; Esc — список закрыт, окно открыто
  [UI-49]. Проверка — `npm run test:e2e:types` зелёный
- [ ] 1.3 Уровень размера `FieldBuilder` — по факту. Проверка — `FileSizeRatchet` зелёный

## 2. Правило храповика

- [ ] 2.1 `manual-dropdown` в `ui-ratchet-lib.mjs` по D2: `dirs: ['features', 'shared']`, `instead` с
  выходом; комментарий правила называет, чего оно не видит [UI-50]. Проверка — `ui-ratchet.test.mjs`:
  четыре сегодняшние формы считаются (в том числе `PdfGroupingEditor` без `top-full`), наложения
  `dragOver &&` / `dragging &&` и `asking &&` — нет
- [ ] 2.2 Уровень `manual-dropdown` — три места (`EditorPanel` 2, `PdfGroupingEditor` 1) [UI-50].
  Проверка — `npm run ui:ratchet` без роста, `ui-baseline.json` в коммите

## 3. Документы

- [ ] 3.1 `docs/UI.md`, «Кто сторожит» — строка о `manual-dropdown` (что считает, чего не видит,
  уровень — три места #1538); «Слои над окном» — ссылка на правило; `CLAUDE.md` и `AGENTS.md` — пункт
  «ручной выпадающий список вместо слоя Radix (#1537)» в строку храповика интерфейса, одной правкой,
  тем же коммитом [UI-50]. Проверка — `AgentRulesCopyTests` зелёный

## 4. Верификация

- [ ] 4.1 `cd src/client && npx tsc -b` — без ошибок
- [ ] 4.2 `cd src/client && npm test` — зелёный (в том числе `scripts/ui-ratchet.test.mjs`)
- [ ] 4.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~FileSizeRatchet|FullyQualifiedName~AgentRulesCopy"` — зелёный
