// Плашка-сообщение руками для храповика интерфейса (правило `notice-plaque`, [UI-73], sharedui-md3-12):
// по классам элемента разметки — заливка контейнера роли, рамка и скругление вместе. Своим модулем по
// образцу ui-ratchet-pills.mjs: граница «плашка или пометка» — своя, и растёт она отдельно от пилюль.

// Заливка контейнера роли основным видом: без модификатора (hover:bg-… — подсветка, а не плашка) и без
// прозрачности (`/N` — подложка строки или выделение, а не сообщение). Имён `*-subtle` в коде больше нет —
// вернувшиеся ловит `legacy-class`.
const CONTAINER_FILL = /(?<![\w:/-])bg-(?:error|warning|success|primary|secondary|tertiary)-container(?![\w/-])/;
const OUTLINE = /(?<![\w:/-])border(?![\w-]*(?:-0|-none|-transparent|-dashed)\b)/;
const ROUNDED = /(?<![\w:-])rounded(?!-none\b)(?:-|(?=[\s'"`}]))/;
// Не плашка: подпись-пометка (`text-label-*` — пилюля, территория `badge`) и пунктир — место для
// перетаскивания, а не сообщение.
const LABEL_TEXT = /(?<![\w:-])text-label-/;
const DASHED = /(?<![\w:-])border-dashed\b/;
const BLOCKS = new Set(['div', 'p', 'section']);

/**
 * Своя плашка-сообщение: `div`, `p` или `section` с заливкой контейнера роли, рамкой и скруглением. `span` не
 * смотрим — там живут пометки-пилюли. Элемент — из `countElements` (ui-ratchet-lib.mjs).
 */
export function isNoticePlaque({ tag, cls }) {
  if (!BLOCKS.has(tag) || !CONTAINER_FILL.test(cls) || !OUTLINE.test(cls) || !ROUNDED.test(cls)) return false;
  return !LABEL_TEXT.test(cls) && !DASHED.test(cls);
}
