# Proposal: значки Material Symbols и индикатор хода

Дочерний change зонтика `sharedui-md3-00-epic`, общая архитектура — `../sharedui-md3-00-epic/design.md`
(D5, D10, D11). После `sharedui-md3-02-class-codemod`; решение владельца В4.

## Why

Значки — второй по объёму след не-MD3: `lucide-react` в 261 файле, 183 разных значка, 1046 размеров
`size={n}` от 9 до 48 px, синонимы (`AlertTriangle` и `TriangleAlert` — один смысл, 87 мест). Набор
размазан по экранам напрямую, поэтому переход и дорог. Вращающийся значок `Loader2` (46 мест) играет роль
индикатора хода, которого в `shared/ui` нет. В варианте Г значки — Material Symbols Outlined с заливкой у
выбранного.

## What Changes

- **Набор Material Symbols в SVG, не шрифтом** (D5): devDependency `@material-symbols/svg-400` с точной
  версией; генератор по манифесту `shared/ui/icons/manifest.json` пишет в git только используемые пути и
  вариант `-fill` там, где он нужен.
- **Один вход — `shared/ui/Icon`**: `<Icon icon={Delete} size filled />`, `aria-hidden` по умолчанию,
  цвет — `currentColor`. Тип `IconDef` вместо `LucideIcon`.
- **`shared/ui/CircularProgress`** — индикатор хода MD3 вместо `Loader2 animate-spin`.
- **Таблица lucide → Symbols на 183 значка** составляет Дизайнер (смысл, склейка синонимов); перевод
  экранов — кодмодом, который запускает та же `npm run md3:migrate`.
- **Шкала размеров значка** — по листу с вариантами и выбору владельца; кодмод переводит размеры на неё.
- **Сторожа:** правила храповика `lucide-import`, `spinner-icon`, `icon-size` — в ноль в этом change;
  `lucide-react` удаляется из `package.json`.
- **Бюджет основного чанка** называется до кода и замеряется в PR.
- **BREAKING (для кода):** `lucide-react` и тип `LucideIcon` пропадают; ветки обновляются перезапуском
  `npm run md3:migrate`.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `sharedui`: значки — один компонент на наборе Material Symbols [UI-60]; индикатор хода — свой компонент
  [UI-61].

## Impact

- **Клиент:** новые `shared/ui/Icon.tsx`, `shared/ui/CircularProgress.tsx`, `shared/ui/icons/`
  (манифест, сгенерированный набор), `scripts/icons-generate.mjs`, кодмод значков; 261 файл с lucide
  (импорт, JSX, ссылки-значения в `navConfig`, `typeIcons`, `paletteActions`…), 7 тестов с lucide;
  `ui-ratchet-lib.mjs`, `ui-baseline.json`; `package.json` — минус `lucide-react`, плюс
  `@material-symbols/svg-400`.
- **Живые прогоны:** селекторов по `svg` и классам нет; размеры значков меняют геометрию — правятся в
  этом change.
- **Документы:** `docs/UI.md` — раздел «Значки»; `docs/DEV_NOTES.md` — лицензия Apache 2.0.
- **Claude Design:** лист с вариантами до кода; `/design-sync` после слияния (превью и `config.json` знают
  lucide).
- **PR:** два — основа и перевод экранов; метка `version:minor`.
