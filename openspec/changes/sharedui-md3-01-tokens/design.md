# Design: словарь ролей MD3 и переходные псевдонимы

Общая архитектура — `../sharedui-md3-00-epic/design.md`: имена (D1), псевдонимы (D2), генератор (D4),
сторожа (D11), внешние носители (D12). Здесь — только решения уровня ребёнка. Макетов нет (D10): вид не
меняется, это доказывается механически.

## Context

Сверено по коду ветки от master `1e81da2e`.

- `src/client/src/index.css`: `--f-*` светлой темы (строки 5–79), тёмной (83–131), `@theme inline` со
  словарём Fluent (135–206), база с `var(--f-fg1)`, `var(--f-bg1)`, `var(--f-brand)` (212–263).
- `--f-*` вне `index.css` — 57 мест в 43 файлах: тени `shadow4/16/28` (48), `--f-brand` (4), `--f-fg3`
  (2, `RowFilterDialog.tsx:88`), `--f-tab-light/dark` (2, `BrandingSection.tsx`), `--f-brand-text` (1).
  Ещё — `var(--color-brand)` в 8 местах (`DataGrid`, `EditGrid`, `ComplexFields`, `LoginPage`): это имя
  слоя Tailwind, его переводит 02.
- Скрипты: `ui-ratchet-lib.mjs:238-241` (`ROLE`/`FILL` правила `white-on-fill` знают `f-brand`,
  `f-*-fg`), `ui-ratchet.test.mjs:145`, `color-classes.test.mjs:29` (фикстура `bg-[var(--f-bg3)]`) и его
  совет «`text-fg1`, `*-brand`».
- **Храповик смотрит только `.ts/.tsx/.js/.jsx` без тестов** (`ui-ratchet.mjs:10`). Чтобы `legacy-token`
  видел и стили, правилу нужен свой фильтр файлов.
- Сервер: `BrandIcons.cs:124` `BackgroundColor = "#f4f2f7"` (`--f-bg2` светлой), `BrandIconComposer.cs:37-40`
  `DarkTile = #111318` (`--f-bg1` тёмной; комментарий называет `--f-bg1`). `index.html` `theme-color
  #109e92` — цвет знака, решение владельца 13.10.2026. `Branding/Standard/tab.svg` цвета темы не несёт.
- `src/App.css` нигде не импортируется.

## Goals / Non-Goals

**Goals:** словарь MD3 существует и им можно писать (экран счёта по Г); `--f-*` нет нигде; старые имена
рисуют прежнее и не растут.

**Non-Goals:** перевод классов в коде (02); значения из seed и контраст (04); шкалы формы, тени MD3 и
текста (06, 07) — здесь у них только переменные с нынешними значениями; `dark:` и глобальный
`transition` (05).

## Decisions

### Раскладка файлов
- `src/client/src/theme/md3-theme.json` — описание: `mode: "values"`, `seed: "#1d5fb0"` (для 04),
  `roles.light/dark` — значения всех ролей, `ext` — свои роли, `static` — `tab-light/dark`,
  `elevation` — три уровня по темам, `aliases` — старое имя → роль или `{ legacy: { light, dark } }`.
- Генератор пишет три файла, все с шапкой «сгенерировано, руками не править»:
  - `theme/md3-tokens.css` — `:root` и `:root[data-theme="dark"]`: `--md-sys-color-*`, `--md-ext-color-*`,
    `--md-sys-elevation-level1..3`, `--md-ref-typeface-plain`;
  - `theme/md3-tailwind.css` — `@theme inline { --color-<роль>: var(--md-sys-color-<роль>) }`, один к
    одному;
  - `theme/md3-legacy.css` — `@theme inline` псевдонимов и значения имён без роли. Финал (20) удаляет
    файл целиком, а не правит строки.
- `index.css` импортирует три файла; радиусы `--radius-sm..xl` получают нынешние числа прямо в `@theme`
  (их шкалу меняет 06, имени MD3 у 6 и 10 px нет); `--text-caption`, `--text-note` остаются до 07;
  `--font-mono` — число в `@theme`.
- **Отвергнуто:** писать переменные в сам `index.css` — тогда дрейф генерации пришлось бы сверять по
  куску рукописного файла.

### Набор ролей и значения в режиме `values`
- Роли — полный набор схемы MD3 (как в экспорте Material Theme Builder для веба), а не только те, что
  есть сегодня: экрану по варианту Г нужны `inverse-surface`, `scrim`, `on-error-container`,
  `surface-container-lowest`. Ими никто ещё не рисует, поэтому вид не меняется.
- Значение роли, у которой сегодня есть токен, — нынешнее (`surface` = `--f-bg1`, `surface-container-low` =
  `--f-bg2`, `surface-container` = `--f-bg3`, `outline` = `--f-stroke1`…). Значения остальных ролей
  вписываются один раз из схемы MCU для seed `#1d5fb0` (вне репозитория; зависимость приходит в 04) и до
  04 предварительные.
- Своя роль `success-container` = `--f-success-bg`, `on-success` = `--f-success-text` и т. д.;
  `on-success-container`, `on-warning-container` вписываются так же, как недостающие роли.

### Псевдонимы
- Имена с ролью того же значения ссылаются на роль: `fg1 → on-surface`, `fg2 → on-surface-variant`,
  **`fg4 → outline`** (значения совпадают в обеих темах), `base → surface-container-low`, `muted →
  surface-container`, `muted-hover → surface-container-high`, `stroke → outline-variant`, `stroke-strong
  → outline`, `brand → primary`, `brand-subtle →
  primary-container`, `on-brand(-subtle) → on-primary(-container)`, `tonal`/`on-tonal` → `secondary-container`
  / `on-secondary-container`, `tertiary-subtle` → `tertiary-container`, `danger*` → `error*`,
  `success-subtle`/`warning-subtle` → `*-container`.
- **Имена без роли** — `fg3`, `fg-disabled`, `brand-hover`, `brand-pressed`, `warning-border`,
  `stroke-subtle` — держат своё значение переменной `--legacy-<имя>` в `md3-legacy.css`. `stroke-subtle`
  сюда попал сверкой: в светлой теме он равен `surface-container-highest`, а в тёмной (`#282a2f`) — уже
  `surface-container-high`. Снимают их дети: `fg3`, `warning-border`, `stroke-subtle` — 04 (В1),
  `fg-disabled`, `brand-hover`, `brand-pressed` — 05. Правило `legacy-token`
  считает и `--legacy-` вне `src/theme/`.
- `success`, `warning`, `on-success`, `on-warning`, `surface`, `tertiary` — уже имена ролей, не
  псевдонимы, и `legacy-class` их не считает.
- **Отвергнуто — свести имена без роли к ближайшей роли** (`fg3 → on-surface-variant`): это видимое
  изменение, и оно ждёт решения владельца В1 в 04.

### Замена `--f-*` вне темы
- Тени: `--f-shadow4/16/28` → `--md-sys-elevation-level1/2/3` с нынешними значениями обеих тем (уровни MD3 по
  назначению: приподнятый контрол, меню, окно). Значения MD3 и уровни 4–5 приносит 06.
- `--f-brand` → `--md-sys-color-primary`, `--f-brand-text` → `--md-sys-color-on-primary`, `--f-tab-*` →
  `--md-ext-color-tab-*`; `--f-fg3` в `DEPTH_COLORS` → `var(--color-fg3)` (псевдоним, считается в
  `legacy-class`, уходит с 04).
- Замена — одной таблицей в PR (57 мест). Перезапускаемым кодмодом её делает 02: карта 02 содержит и
  строки `--f-*`, чтобы чужая ветка с `--f-*` чинилась тем же `npm run md3:migrate`.

### Сторожа
- `scripts/md3-theme.test.mjs` (vitest, едет в `npm test`):
  - **дрейф** — генератор в памяти, сравнение с тремя файлами в git;
  - **псевдоним ≡ роль** — для каждого псевдонима с ролью `candidatesToCss([старый])` и
    `candidatesToCss([новый])` с разрешёнными переменными равны в обеих темах (движок и разбор — те же,
    что у `color-classes.test.mjs`);
  - **сервер** — значения `BrandIcons.BackgroundColor` и `BrandIconComposer.DarkTile`, прочитанные из
    `.cs` текстом, равны `light.surface-container-low` и `dark.surface`.
- Доказательство «до и после» для всего словаря — разовый прогон в PR: те же объявления по `index.css` из
  master и из ветки для каждого старого класса × утилиты `COLOR_UTILITIES`. Вывод — в описании PR. В git
  не кладётся: после 04 значения меняются нарочно.
- `legacy-token` — уровень 0, свой фильтр файлов: код, стили, кроме `src/theme/`. Сделано так:
  храповик читает и `.css`, но файл стилей считают только правила с признаком `styles`; `skip` у
  правила с `everywhere` — только его собственный (раньше к нему подмешивался пропуск `shared/ui/`
  области по умолчанию, и `native-date` молча не смотрел `shared/ui/`; нарушений там 0, уровень не
  изменился). Текст `--f-` в `src/` остаётся только в самом правиле и фикстурах его теста. `legacy-class` —
  перечень имён из `aliases` описания темы, граница класса — `CANDIDATE` из `color-classes-lib.mjs`.
- `white-on-fill`: `ROLE` — `primary|error|success|warning` и старые имена до 20; `FILL` —
  `var(--md-sys-color-(primary|error))`, `var(--md-ext-color-(success|warning))`, `var(--color-<роль>)`.
- `color-classes.test.mjs`: фикстура произвольного значения — на `--md-sys-*`, совет в сообщении — роли
  MD3.

## Risks / Trade-offs

- [Предварительные значения недостающих ролей разойдутся с тем, что даст генератор в 04] → ими пока никто
  не рисует, кроме экрана по Г; 04 перегенерирует всё, и снимки «до/после» 04 это покажут.
- [Экран счёта начнёт писать на ролях раньше 04 и увидит сдвиг цвета после 04] → это и есть план эпика:
  роль та же, значение — палитры.
- [Чужая ветка принесёт `var(--f-…)`] → `legacy-token` красный в её CI; таблица замены — в `docs/UI.md`,
  с 02 — `npm run md3:migrate`.

## Migration Plan

Один PR. Откат — ревёрт: значения прежние, псевдонимы держат старый словарь.
