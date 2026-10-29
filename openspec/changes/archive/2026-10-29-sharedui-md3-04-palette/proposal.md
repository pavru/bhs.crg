# Proposal: палитра из seed, свои роли и две роли текста

Дочерний change зонтика `sharedui-md3-00-epic`, общая архитектура — `../sharedui-md3-00-epic/design.md`
(D4, D10, D11). После `sharedui-md3-02-class-codemod`; решения владельца В1–В3.

## Why

После 01 палитра — рукописные значения 2022 года под именами MD3. Они не из одной тональной схемы
(`--f-bg3` `#eef0f4` выпадает из нейтральной палитры), тёмная тема неполна, а контраст никто не
проверяет: `text-fg4` даёт 4,36:1 на `surface` (888 мест мелкого текста ниже WCAG AA), `text-success` на
своём контейнере — 3,61:1. Четыре серых для текста — словарь Fluent: в MD3 у текста две роли.

## What Changes

- **Генератор в режиме `seed`:** `@material/material-color-utilities` — devDependency с точной версией;
  схема обеих тем из seed `#1d5fb0` по варианту, выбранному владельцем (лист с тремя вариантами);
  primary — по В2 (рекомендация — закрепить равным seed).
- **Свои роли `success` и `warning`** — по четыре на каждую (`X`, `on-X`, `X-container`,
  `on-X-container`) в обеих темах, гармонизация с primary — по В3.
- **Тест контраста** в `md3-theme.test.mjs`: `on-X` на `X` ≥ 4,5; `on-surface-variant` на каждом
  `surface-container-*` ≥ 4,5; `outline` на поверхностях ≥ 3; обе темы, свои роли тоже.
- **Две роли текста (В1):** `text-fg3`, `text-fg4` и `text-stroke-strong` → `text-on-surface-variant`;
  иерархию несут роль шрифта и вес. Текст на контейнере состояния — парной ролью `on-*-container`.
  Обводка по статусу (`warning-border`) и `stroke-subtle` сводятся к ролям по листу.
- **Значения сервера** — из генератора; сторож 01 их сверяет.
- **Уровень `legacy-class`** опускается: имена, которые снимает этот change, — в ноль.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `sharedui`: палитра из seed с проверкой контраста [UI-62]; две роли цвета текста [UI-63].

## Impact

- **Клиент:** `src/client/src/theme/md3-theme.json` (режим `seed`) и три сгенерированных файла,
  `scripts/md3-theme.mjs`, `scripts/md3-theme.test.mjs`, карта `md3-class-map.json` (строки этого
  change), ~1550 мест `text-fg3`/`text-fg4` и текста на контейнерах, `ui-baseline.json`; `package.json` —
  плюс `@material/material-color-utilities` в devDependencies.
- **Сервер:** `BrandIcons.cs` (`BackgroundColor`), `BrandIconComposer.cs` (`DarkTile`) — новые значения.
  `theme-color` `#109e92` (знак) не трогается.
- **Документы:** `docs/UI.md` «Цвет» — палитра из seed, контраст, две роли текста.
- **Claude Design:** лист с тремя вариантами до кода; `/design-sync` после слияния.
- **PR:** два — палитра и перевод текста; метка `version:minor`.
