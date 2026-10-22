# Proposal: храповик интерфейса смотрит `shared/`, кроме `shared/ui`

Самостоятельный change общих компонентов интерфейса (домен `sharedui`).

## Why

Issue #1469 (храповик интерфейса смотрит `shared/` кроме `shared/ui`; нативные select в отборе →
`Select`). Храповик (`npm run ui:ratchet`) обходит только `features/`: `shared/` выключен целиком,
потому что «там живут сами DataGrid и Select». Из-за этого в `shared/filter` — отборе всех реестров —
остались три нативных `<select>`, и фраза `docs/UI.md` «нативных select нет вовсе» верна только для
`features/`. Граница проведена не там: компоненты живут в `shared/ui`, а остальной `shared/` — такая же
обвязка экранов, как `features/`.

## What Changes

- **Область правил по умолчанию** — `features/` и `shared/`, кроме `shared/ui/`. Правила со своей
  областью (`white-on-fill`, `portal-container` — и `shared/ui`; `native-date` — весь `src/`) не меняются.
- **Три нативных `<select>` в `shared/filter`** → `Select` компактного слоя обвязки:
  `ConditionEditor.tsx:33` (колонка), `ConditionEditor.tsx:62` (оператор), `RowFilterValue.tsx:49`
  (значение «да / нет» и выбор из списка).
- **Что ещё нашлось на новой области** — переводится здесь же, мест мало: свои ширины в `style`
  `ConditionEditor` (4) → классы шкалы; кнопки-иконки `FilterChips` (1), `RowFilterValue` (2),
  `SiteFilterNotes` (1) → `IconButton`; свой чип `SiteFilterNotes` (1) → `Chip`.
- **Ложное срабатывание `fixed-width`** на строке медиазапроса (`useMedia.ts:9`, `'(min-width: 768px)'`)
  устраняется в самом правиле, а не уровнем.
- `docs/UI.md`: область храповика и фраза о нативных select («нигде, кроме `shared/ui`»).

## Capabilities

### New Capabilities
- `sharedui`: общие компоненты интерфейса — `shared/ui`, оболочка, храповик интерфейса и правила
  `docs/UI.md`.

### Modified Capabilities
- Нет.

## Impact

- **Храповик:** `src/client/scripts/ui-ratchet-lib.mjs`, `ui-ratchet.mjs`, `ui-ratchet.test.mjs`;
  `src/client/ui-baseline.json` (по итогу — без мест в `shared/`).
- **Клиент:** `shared/filter/ConditionEditor.tsx`, `shared/filter/RowFilterValue.tsx`,
  `shared/filter/FilterChips.tsx`, `shared/siteFilter/SiteFilterNotes.tsx`.
- **Живые прогоны:** `table-chips` (поповер чипа с `Select` внутри), e2e отбора наборов данных
  (`RowFilterDialog`).
- **Документы:** `docs/UI.md`; перечень храповика в `CLAUDE.md` и `AGENTS.md` (одной правкой).
- **Сервер:** не меняется.
