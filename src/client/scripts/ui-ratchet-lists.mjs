// Строка списка руками для храповика интерфейса — правило `list-row` ([UI-84], sharedui-md3-18): элемент со слоем
// состояния, у которого заливка выбранного ставится условием. Своим модулем, как ui-ratchet-cards.mjs: lib и его
// тест стоят у порога размера. Элемент — из `countElements` (ui-ratchet-lib.mjs, передаётся в `listRules`).

import ts from 'typescript';

const TAGS = new Set(['button', 'a', 'div', 'label', 'li']);
// Слой состояния строки — сам `state-layer` (не `-group` рейла и не `-row` общего ListItem) или заливка наведения.
const STATE = /(?<![\w:-])state-layer(?![\w-])|(?<![\w-])hover:bg-/;
// Заливка выбранного основным видом: без модификатора (hover:, data-[state=open]: — не выбор) и без прозрачности
// (`/70` — подложка наведения у чипа, BaseInstanceChip). Границы — как у SURFACE_FILL в ui-ratchet-cards.mjs.
const FILL = /(?<![\w:/-])bg-(?:primary|secondary)-container(?![\w/-])/;
// Заливка выбранного вариантом состояния — условие уже в самом классе. Открытое меню (`data-[state=open]:`) — не выбор.
const VARIANT_FILL = /(?:aria-current|aria-selected|data-\[(?!state=open\])[^\]]*\]):bg-(?:primary|secondary)-container(?![\w/-])/;

/** Заливка выбранного стоит в ветке условия (`a ? '…' : ''`, `a && '…'`) внутри выражения `className`. */
function conditionalFill(init) {
  let found = false;
  const visit = (node, underCondition) => {
    if (found) return;
    const cond = underCondition || ts.isConditionalExpression(node)
      || (ts.isBinaryExpression(node) && [ts.SyntaxKind.AmpersandAmpersandToken, ts.SyntaxKind.BarBarToken].includes(node.operatorToken.kind));
    if ((ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node) || ts.isTemplateHead(node)
      || ts.isTemplateMiddle(node) || ts.isTemplateTail(node)) && cond && FILL.test(node.text)) found = true;
    ts.forEachChild(node, child => visit(child, cond));
  };
  visit(init, false);
  return found;
}

const classNameOf = (element, sf) => {
  const opening = ts.isJsxSelfClosingElement(element) ? element : element.openingElement;
  return opening.attributes.properties.find(p => ts.isJsxAttribute(p) && p.name.getText(sf) === 'className')?.initializer;
};

/** Своя строка списка: тег строки со слоем состояния и условной заливкой выбранного. */
export function isListRow({ tag, cls, element, sf }) {
  if (!TAGS.has(tag) || !STATE.test(cls)) return false;
  if (VARIANT_FILL.test(cls)) return true;
  const init = classNameOf(element, sf);
  return !!init && ts.isJsxExpression(init) && !!init.expression && conditionalFill(init.expression);
}

export const listRules = countElements => [
  {
    id: 'list-row',
    // Строка списка руками ([UI-84]): у каждого экрана своя пилюля или прямоугольник, своя высота и заливка
    // выбранного. Область по умолчанию (#1469) — shared/ui рисует сам ListItem. Экраны переводятся в md3-18
    // PR 2, остаток (списки выбора в окнах, списки карточки) — в sharedui-md3-21; уровень только опускается.
    count: text => countElements(text, isListRow),
    instead: 'строка списка — ListItem (shared/ui/ListItem: headline, supporting, icon, leading, trailing, action, selected, onClick или to); группа — ListGroupHeader; пункт раздела уровня — NavItem (docs/UI.md, «Списки list-detail»)',
  },
];
