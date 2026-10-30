// Слои над окном для храповика интерфейса (docs/UI.md, «Слои над окном»): портал в свой контейнер
// (правило `portal-container`, #1521) и ручной выпадающий список (`manual-dropdown`, #1537). Отдельно от
// ui-ratchet-lib.mjs по той же причине, что ui-ratchet-pills.mjs: там разбор текста в дерево и перечень
// правил, здесь — что считать слоем не на своём месте.
import ts from 'typescript';

/** `<….Portal container=…>` и `createPortal(узел, контейнер)` в разобранном файле. */
export function portalContainersIn(sf) {
  let n = 0;
  const visit = node => {
    if ((ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node))
      && /(?:^|\.)Portal$/.test(node.tagName.getText(sf))
      && node.attributes.properties.some(p => ts.isJsxAttribute(p) && p.name.getText(sf) === 'container'))
      n++;
    if (ts.isCallExpression(node) && node.arguments.length >= 2
      && /(?:^|\.)createPortal$/.test(node.expression.getText(sf)))
      n++;
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return n;
}

/**
 * Класс `absolute` или `fixed` без модификатора: `md:absolute` — раскладка на ширине, а не слой.
 * `fixed` — первый обход запрета: в окне с `transform` он ведёт себя как `absolute` и режется так же.
 */
const LAYERED = /(?<![\w:/-])(?:absolute|fixed)(?![\w-])/;
/**
 * Условие «открыт»: `open`, `pickOpen`, `menuOpen`. Возможность — не состояние: `canOpen`, `shouldOpen`
 * говорят, что открыть МОЖНО, и под ними рисуют кнопку или значок, а не список (`canOpen &&` — в
 * InvoiceScanPanel).
 */
const OPEN = /^(?:open|\w+Open)$/;
const ABILITY = /^(?:can|could|may|might|should|will|allow|allows)[A-Z]/;

/** Открывает ли условие слой: `open`, `xxxOpen` или цепочка `a && …`, которая оканчивается им. */
function opensOn(node) {
  while (ts.isParenthesizedExpression(node)) node = node.expression;
  if (ts.isIdentifier(node)) return OPEN.test(node.text) && !ABILITY.test(node.text);
  return ts.isBinaryExpression(node) && node.operatorToken.kind === ts.SyntaxKind.AmpersandAmpersandToken
    && opensOn(node.right);
}

/**
 * Ручной выпадающий список: элемент разметки с `absolute` (или `fixed`) в className, который рисуется
 * по условию «открыт» — правой частью `&&` (`{menuOpen && <div className="absolute …">}`) или ветвью
 * «да» тернарника (`{open ? <div …> : null}`). `top-full` не требуется: меню «Перенести в группу»
 * открывалось без него. Наложения при перетаскивании (`dragOver &&`) и вопрос окна (`asking &&`) — не
 * списки, и условие у них названо иначе (сверка — разбор Архитектора к #1537).
 */
export function isManualDropdown({ tag, cls, element }) {
  if (!/^[a-z]/.test(tag) || !LAYERED.test(cls)) return false;
  let node = element;
  while (ts.isParenthesizedExpression(node.parent)) node = node.parent;
  const cond = node.parent;
  if (ts.isConditionalExpression(cond)) return cond.whenTrue === node && opensOn(cond.condition);
  return ts.isBinaryExpression(cond) && cond.operatorToken.kind === ts.SyntaxKind.AmpersandAmpersandToken
    && cond.right === node && opensOn(cond.left);
}
