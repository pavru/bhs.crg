// Плашка-сообщение руками для храповика интерфейса (правило `notice-plaque`, [UI-73], sharedui-md3-12):
// по классам элемента разметки — заливка контейнера роли, рамка и скругление вместе. Своим модулем по
// образцу ui-ratchet-pills.mjs: граница «плашка или пометка» — своя, и растёт она отдельно от пилюль.

// Заливка контейнера роли основным видом: без модификатора (hover:bg-… — подсветка, а не плашка) и без
// прозрачности (`/N` — подложка строки или выделение, а не сообщение). Имён `*-subtle` в коде больше нет —
// вернувшиеся ловит сторож словаря темы (theme-classes.test.mjs).
const CONTAINER_FILL = /(?<![\w:/-])bg-(?:error|warning|success|primary|secondary|tertiary)-container(?![\w/-])/;
// Рамка — с шириной: `border`, `border-2`, `border-b`, `border-x-2`. Один цвет (`border-warning`) рамки в
// Tailwind v4 не рисует — ширина у элемента 0, и плашкой его это не делает. Рамку, скругление и пунктир
// берёт и правило `hand-card` (ui-ratchet-cards.mjs).
export const OUTLINE = /(?<![\w:/-])border(?:-[xytrblse])?(?:-(?:[1-9]\d*|\[[^\]\s]+\]))?(?![\w[/-])/;
export const ROUNDED = /(?<![\w:-])rounded(?!-none\b)(?:-|(?=[\s'"`}]))/;
// Не плашка: подпись-пометка (`text-label-*` — пилюля, территория `badge`) и пунктир — место для
// перетаскивания, а не сообщение.
const LABEL_TEXT = /(?<![\w:-])text-label-/;
export const DASHED = /(?<![\w:-])border-dashed\b/;
const BLOCKS = new Set(['div', 'p', 'section']);

/**
 * Своя плашка-сообщение: `div`, `p` или `section` с заливкой контейнера роли, рамкой и скруглением. `span` не
 * смотрим — там живут пометки-пилюли. Элемент — из `countElements` (ui-ratchet-lib.mjs).
 */
export function isNoticePlaque({ tag, cls }) {
  if (!BLOCKS.has(tag) || !CONTAINER_FILL.test(cls) || !OUTLINE.test(cls) || !ROUNDED.test(cls)) return false;
  return !LABEL_TEXT.test(cls) && !DASHED.test(cls);
}

export const plaqueRules = countElements => [
  {
    id: 'notice-plaque',
    // Плашка-сообщение руками ([UI-73], sharedui-md3-12): div, p или section с заливкой контейнера роли,
    // рамкой и скруглением — у каждого экрана своя. Пометка-пилюля (span, text-label-*), подложка с
    // прозрачностью и пунктирное место для перетаскивания — не плашки. Весь src/: в shared/ui плашка была
    // своя (RereadFailedNotice). Переводятся в 17 — уровень только опускается.
    count: text => countElements(text, isNoticePlaque),
    everywhere: true,
    instead: 'сообщение области — Banner (shared/ui/Banner: tone — error, warning, info, success; actions — до двух text-действий), во всю ширину в начале страницы, панели или окна; про одно поле — текст под полем (TextField hint, error); результат действия — snackbar (docs/UI.md, «что сказать — чем»)',
  },
];
