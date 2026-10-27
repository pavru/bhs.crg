# Design: слой состояния, фокус, отключённое и движение

Общая архитектура — `../sharedui-md3-00-epic/design.md`: слой состояния (D6), макеты (D10), сторожа (D11).
Описание темы — `../sharedui-md3-01-tokens/design.md`; карта перевода — `../sharedui-md3-02-class-codemod/design.md`.

## Context

- Наведение (разбор Дизайнера, §2): `hover:bg-brand-hover active:bg-brand-pressed` — 53 файла;
  `hover:brightness-95` — 4 (`Button.tsx:48,51`); `hover:bg-brand/10 active:bg-brand/15` у outlined/text;
  `hover:bg-black/5` 33 + `dark:hover:bg-white/10` 28; `hover:bg-base` 56, `hover:bg-muted` 45 (после 02 —
  `surface-container-low`, `surface-container`); пункт меню — четыре вида подсветки. В одном `Button.tsx`
  — четыре способа.
- Отключённое: `disabled:opacity-50` 36, `-40` 22, `-30` 3, `-38` 2, `-25` 2, ещё 60 и 80.
- Фокус: `focus-visible:ring-2 ring-brand ring-offset-2` (~75), `outline-2 outline-brand` (10),
  `ring-brand/40` в меню.
- `dark:` — 44 места: 39 — пара самодельного наведения; 2 — белый лист просмотрщика
  (`viewer/PdfPages.tsx:37`, `viewer/ImageView.tsx:92`: в тёмной теме обводка вместо тени); 1 —
  регулярка в `Tooltip.tsx:70`, а не класс.
- Движение: глобальный `transition` 0,15 s `ease` на `*` (`index.css:225-228`), `.no-transition` нигде не
  используется; явных `duration-` — 3, кривых — 0; один `@keyframes` (`toast-in`); `prefers-reduced-motion`
  — 1 место.
- Псевдонимы без роли после 04: `fg-disabled`, `brand-hover`, `brand-pressed` (`--legacy-*`).

## Goals / Non-Goals

**Goals:** одна модель состояния во всём клиенте; `raw-state`, `dark-variant`, `legacy-class` — 0;
движение задано токенами.

**Non-Goals:** анимации появления окон, листов, меню и snackbar по MD3 (дети 12, 13 — они берут токены
отсюда); вид компонентов (10–16); раскладка поверхностей.

## Decisions

### Токены
- В описании темы и `md3-tokens.css`: `--md-sys-state-hover: .08`, `-focus: .10`, `-pressed: .10`,
  `-dragged: .16`; `--md-sys-motion-easing-standard`, `-standard-decelerate`, `-standard-accelerate`,
  `-emphasized-decelerate`, `-emphasized-accelerate`; `--md-sys-motion-duration-short1…long4` (50–600 мс).
- Tailwind: `--ease-standard`, `--ease-standard-decelerate`, `--ease-emphasized-decelerate`… в `@theme`
  (D1); длительности — числом `duration-150`, `duration-200`: своего пространства имён у Tailwind для
  них нет, а заводить синонимы MD3 зонтик запрещает (D1).

### Слой состояния
- `@utility state-layer` — ровно D6: `background-image` с `color-mix(currentColor …)`, без `::before` и
  `position: relative`. `data-[highlighted]` (Radix) и `[data-dragging]` добавляются к селекторам слоя:
  иначе пункт меню с клавиатуры остался бы без него.
- Элемент, у которого цвет содержимого не равен `currentColor` (значок другим цветом в строке), задаёт
  цвет слоя переменной `--_state-color` (частная, как `--_s` в D6); по умолчанию — `currentColor`.
- `Button`, `IconButton`, `Chip`, `Tabs`, `Select` и пункты меню, `DataGrid`/`EditGrid` (строка, шапка),
  `SectionRail`, `BottomNav`, `RowActionsMenu`, `Calendar` — на `state-layer` одним PR: две модели рядом
  на одном экране хуже любой из них.
- Отключённое — пара `disabled:text-on-surface/38 disabled:bg-on-surface/12` (и то же для
  `aria-disabled`) в компонентах; у outlined — обводка `on-surface/12`. Псевдоним `fg-disabled` → нет:
  его 4 места переходят на `text-on-surface/38`, `--legacy-fg-disabled` удаляется.
- **Отвергнуто — псевдоэлемент `::before`** (предложение Дизайнера): ломает абсолютно позиционированные
  элементы и чужие `before:`; D6 зонтика.

### Фокус
- Утилита `focus-ring` — одно кольцо; толщина, отступ и роль цвета — по листу (раздел 0). Варианты:
  **А** — Material Web: 3 px `secondary`, отступ 2 px; **Б** — нынешнее 2 px `primary`, отступ 2 px;
  **В** — 3 px `primary` и внутреннее кольцо у клеток таблиц. Рекомендация — А, у поля — обводка поля
  2 px `primary` (как сейчас в `OutlinedField`).

### Затемнение и тёмная тема
- `bg-black/40` (8) → `bg-scrim/32` — значение меняется на MD3, одним шагом.
- Белый лист просмотрщика в тёмной теме: правило переезжает в CSS просмотрщика (`viewer/*.css`, где уже
  живёт `color-scheme: only light`) селектором `:root[data-theme="dark"]`. Это переключение темы в слое
  стилей, а не вариант в классах экрана; `dark-variant` его не видит и не должен.
- `dark-variant` считает класс с `dark:` по синтаксическому дереву строк классов, а не по тексту файла —
  регулярка в `Tooltip.tsx` не в счёт.

### Движение
- Глобальный `transition` на `*` и `.no-transition` удаляются. Тема переключается мгновенно:
  **отвергнуто** — переход на время переключения классом на `<html>`: та же цена на тысячах узлов, только
  реже, и спор с переходами компонентов.
- Компоненты с переходом (слой состояния, рамка поля, ползунок `Switch`) — `transition-[…] duration-150
  ease-standard`.
- `@media (prefers-reduced-motion: reduce)` в базе: `transition-duration` и `animation-duration` близки
  к нулю у всех, кроме индикатора хода (`CircularProgress` 03 — медленнее, а не стоп).

### Перевод экранов
- Карта кодмода получает строки (`same: false`): `hover:bg-black/5`, `dark:hover:bg-white/10`,
  `hover:bg-surface-container(-low)` в связке с прозрачной заливкой → `state-layer`; `disabled:opacity-*`
  → пара отключённого; `brand-hover`, `brand-pressed` с вариантом → снимаются в пользу `state-layer`;
  `text-brand-hover` без наведения (30) → `text-primary`.
- Неоднозначное (наведение заливкой другого контейнера, которое значит выбор, а не наведение) — отчётом,
  руками.

## Risks / Trade-offs

- [Наведение на светлых поверхностях бледнее нынешнего `bg-black/5` в плотных таблицах] → лист с
  вариантами на реальной таблице до кода; MD3 8 % — по спецификации.
- [Снятие глобального перехода «дёргает» элементы, которые на нём держались] → живые прогоны и снимки;
  элемент, которому переход нужен, получает его классом.
- [Слой `background-image` перекрывается фоном-картинкой элемента] → таких компонентов в `shared/ui` нет;
  новый — с `--_state-color` на дочернем элементе.

## Migration Plan

PR 1 — токены, `state-layer`, `focus-ring`, отключённое, движение, `shared/ui`, правила с уровнями. PR 2 —
`features/` кодмодом и руками, `raw-state`, `dark-variant`, `legacy-class` — 0, `docs/UI.md`. Откат —
ревёрт PR: псевдонимы держат старые имена до финала.
