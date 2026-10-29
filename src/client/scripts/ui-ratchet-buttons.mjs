// Свои кнопки для храповика интерфейса (правила `icon-button`, #1417, и `split-button`, [UI-83]): элемент —
// из `countElements` (ui-ratchet-lib.mjs). Отдельно от него по той же причине, что ui-ratchet-pills.mjs:
// там разбор дерева и перечень правил, здесь — что считать своей кнопкой.
import ts from 'typescript';

/** Кнопка разметки, единственный ребёнок которой — компонент-значок. */
export function isIconButton({ tag, element, sf }) {
  if (tag !== 'button' || !ts.isJsxElement(element)) return false;
  const kids = element.children.filter(c => !(ts.isJsxText(c) && !c.text.trim()));
  return kids.length === 1 && ts.isJsxSelfClosingElement(kids[0]) && /^[A-Z]/.test(kids[0].tagName.getText(sf));
}

/** Одностороннее скругление: `rounded-l-full`, `rounded-r-none`, `active:rounded-tl-xs`, `rounded-s`… */
const ONE_SIDED_RADIUS = /(?<![\w-])rounded-(?:l|r|s|e|tl|tr|bl|br|ss|se|es|ee)(?:-|(?=[\s'"`}]))/;
export const SPLIT_HALF_COMPONENTS = ['Button', 'IconButton'];
const SPLIT_HALF_TAGS = new Set(['button', 'a', ...SPLIT_HALF_COMPONENTS]);

/** Своя половина split-кнопки: кнопка (разметки или Button / IconButton) с односторонним скруглением. */
export function isSplitHalf({ tag, cls }) {
  return SPLIT_HALF_TAGS.has(tag) && ONE_SIDED_RADIUS.test(cls);
}
