// Карточка руками для храповика интерфейса — правило `hand-card` ([UI-77], sharedui-md3-13): блочный элемент
// с фоном поверхности, рамкой и скруглением на одном элементе. Своим модулем рядом с ui-ratchet-plaque.mjs:
// граница «карточка или плашка» своя, а lib и его тест стоят у порога размера. Элемент — из `countElements`
// (ui-ratchet-lib.mjs, передаётся в `cardRules`).

// Фон поверхности основным видом: без модификатора (hover:bg-… — наведение, а не карточка) и без
// прозрачности (`/N` — подложка, а не поверхность). `bg-surface-variant` и `bg-surface-tint` — не контейнер.
const SURFACE_FILL = /(?<![\w:/-])bg-surface(?:-container(?:-(?:lowest|low|high|highest))?)?(?![\w/-])/;
// Рамка — с шириной, как у `notice-plaque`: один цвет (`border-outline`) рамки в Tailwind v4 не рисует.
const OUTLINE = /(?<![\w:/-])border(?:-[xytrblse])?(?:-(?:[1-9]\d*|\[[^\]\s]+\]))?(?![\w[/-])/;
const ROUNDED = /(?<![\w:-])rounded(?!-none\b)(?:-|(?=[\s'"`}]))/;
// Пунктир — место для перетаскивания, а не карточка.
const DASHED = /(?<![\w:-])border-dashed\b/;
// Блочные теги: поле (`input`, `textarea`), кнопка и пометка (`span`) карточкой не бывают, а слой Radix —
// компонент (`Popover.Content`), и `countElements` его не видит вовсе.
const BLOCKS = new Set(['div', 'section', 'article', 'p', 'dl']);

/** Своя карточка: блочный тег с фоном поверхности, рамкой и скруглением в одной строке классов. */
export function isHandCard({ tag, cls }) {
  return BLOCKS.has(tag) && SURFACE_FILL.test(cls) && OUTLINE.test(cls) && ROUNDED.test(cls) && !DASHED.test(cls);
}

export const cardRules = countElements => [
  {
    id: 'hand-card',
    // Карточка руками ([UI-77]): у каждого экрана своя рамка, свой радиус и свой фон. Только features/ —
    // shared/ui рисует сами поля и слои. Переводятся в sharedui-md3-17 — уровень только опускается.
    count: text => countElements(text, isHandCard),
    dirs: ['features'],
    instead: 'карточка — Card (shared/ui/Card: variant — outlined, filled, elevated; title, actions, onClick или to — кликабельная); черта между частями — Divider (docs/UI.md, «Карточки»)',
  },
];
