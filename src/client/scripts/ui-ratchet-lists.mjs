// Строка списка руками для храповика интерфейса — правило `list-row` ([UI-84], sharedui-md3-18): элемент со слоем
// состояния, у которого заливка выбранного ставится условием, и вариант выбора руками (`role="option"`,
// sharedui-md3-21). Своим модулем, как ui-ratchet-cards.mjs: lib и его тест стоят у порога размера. Элемент — из `countElements` (ui-ratchet-lib.mjs, передаётся в `listRules`).

import ts from 'typescript';

const TAGS = new Set(['button', 'a', 'div', 'label', 'li']);
// Слой состояния строки — сам `state-layer` (не `-group` рейла и не `-row` общего ListItem) или заливка наведения.
const STATE = /(?<![\w:-])state-layer(?![\w-])|(?<![\w-])hover:bg-/;
// Заливка выбранного основным видом: без модификатора (hover:, data-[state=open]: — не выбор) и без прозрачности
// (`/70` — подложка наведения у чипа, BaseInstanceChip). Границы — как у SURFACE_FILL в ui-ratchet-cards.mjs.
const FILL = /(?<![\w:/-])bg-(?:primary|secondary)-container(?![\w/-])/;
// Заливка выбранного вариантом состояния — условие уже в самом классе. Открытое меню (`data-[state=open]:`) — не выбор.
const VARIANT_FILL = /(?:aria-current|aria-selected|data-\[(?!state=open\])[^\]]*\]):bg-(?:primary|secondary)-container(?![\w/-])/;

/** Заливка выбранного стоит в ветке условия (`a ? '…' : ''`, `a && '…'`, `a ?? '…'`) внутри выражения `className`. */
function conditionalFill(init) {
  let found = false;
  const visit = (node, underCondition) => {
    if (found) return;
    const cond = underCondition || ts.isConditionalExpression(node)
      || (ts.isBinaryExpression(node) && [ts.SyntaxKind.AmpersandAmpersandToken, ts.SyntaxKind.BarBarToken, ts.SyntaxKind.QuestionQuestionToken].includes(node.operatorToken.kind));
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

// Вариант listbox руками: `role="option"` атрибутом или литералом в выражении (`role={x ? 'option' : …}`).
const OPTION_ROLE = /^(?:"option"|'option'|\{[\s\S]*(['"`])option\1[\s\S]*\})$/;

/**
 * Инициализаторы `const` той же функции, чьи имена стоят в выражении `className` (`const tone = a ? 'bg-…' : …`).
 * Названные пропуски: spread из `const`, переменная из другой функции и из пропов.
 */
function constInits(element, expr) {
  const names = new Set();
  const collect = node => { if (ts.isIdentifier(node)) names.add(node.text); ts.forEachChild(node, collect); };
  collect(expr);
  let fn = element.parent;
  while (fn && !ts.isFunctionLike(fn)) fn = fn.parent;
  if (!fn || names.size === 0) return [];
  const out = [];
  // Только своя функция: одноимённый `const` соседнего обработчика или вложенного компонента — другая переменная.
  const visit = node => {
    if (node !== fn && ts.isFunctionLike(node)) return;
    if (ts.isVariableDeclaration(node) && node.initializer && ts.isIdentifier(node.name) && names.has(node.name.text)
      && ts.isVariableDeclarationList(node.parent) && (node.parent.flags & ts.NodeFlags.Const)) out.push(node.initializer);
    ts.forEachChild(node, visit);
  };
  visit(fn);
  return out;
}

/**
 * Своя строка списка: тег строки со слоем состояния и условной заливкой выбранного — в `className` или в `const`
 * той же функции, — или вариант выбора руками (`role="option"`). Элемент с обоими признаками — один счёт.
 */
export function isListRow({ tag, cls, attrs, element, sf }) {
  if (OPTION_ROLE.test(attrs?.get('role') ?? '')) return true;
  if (!TAGS.has(tag)) return false;
  const init = classNameOf(element, sf);
  const expr = init && ts.isJsxExpression(init) ? init.expression : undefined;
  const consts = expr ? constInits(element, expr) : [];
  // Инициализаторы — к тексту класса ДО проверки слоя: `const tone = … 'state-layer'` иначе недостижим.
  const text = [cls, ...consts.map(c => c.getText(sf))].join(' ');
  if (!STATE.test(text)) return false;
  if (VARIANT_FILL.test(text)) return true;
  return (!!expr && conditionalFill(expr)) || consts.some(conditionalFill);
}

export const listRules = countElements => [
  {
    id: 'list-row',
    // Строка списка руками ([UI-84]): у каждого экрана своя пилюля или прямоугольник, своя высота и заливка
    // выбранного; и вариант выбора руками ([UI-87]) — `role="option"`, нарисовать его правильно нельзя (D9).
    // Область по умолчанию (#1469) — shared/ui рисует сам ListItem и ListOption. Экраны переведены в md3-18 PR 2,
    // остаток (списки выбора в окнах, списки карточки) — в sharedui-md3-21; уровень только опускается.
    count: text => countElements(text, isListRow),
    instead: 'строка списка — ListItem (shared/ui/ListItem: headline, supporting, icon, leading, trailing, action, selected, checked, onClick, to или passive); вариант выбора под полем поиска — ListOption (shared/ui/ListOption: form list в окне, menu в слое под полем; active, current, disabled, action); группа — ListGroupHeader; пункт раздела уровня — NavItem (docs/UI.md, «Списки list-detail» и «Списки выбора»)',
  },
];
