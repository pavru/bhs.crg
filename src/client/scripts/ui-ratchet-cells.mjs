// Поле в клетке сетки правки для храповика интерфейса — правило `cell-field` ([UI-78], sharedui-md3-14). Своим
// модулем, как ui-ratchet-inputs.mjs: lib и его тест стоят у порога размера.
//
// Клетка — значение свойства `cell` колонки `EditGrid`: колонки, переданной прямо в `columns` сетки, или
// объявленной с типом `EditGridColumn` (функция колонок экрана, `.map((…): EditGridColumn<…> => …)`). Колонки
// `DataGrid` с тем же `cell` не считаются: полей правки там нет.
import ts from 'typescript';

/** Поля, которым тело `EditGrid` отдаёт размер клетки само: им вредит только чужой размер. */
const PLACED = new Set(['TextField', 'Select', 'SuggestField', 'NumberInput', 'PlaceSelect']);
const NOT_A_FIELD = new Set(['checkbox', 'radio', 'file', 'hidden', 'range', 'color']);

let parsed = { text: null, sf: null };
function parse(text) {
  if (parsed.text !== text) parsed = { text, sf: ts.createSourceFile('x.tsx', text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX) };
  return parsed.sf;
}

/** Значение атрибута строкой: `"x"`, `{'x'}`; атрибут без значения — `true`; выражение — null. */
function attr(element, name, sf) {
  const prop = element.attributes.properties.find(p => ts.isJsxAttribute(p) && p.name.getText(sf) === name);
  if (!prop) return undefined;
  const init = prop.initializer;
  if (!init) return true;
  if (ts.isStringLiteral(init)) return init.text;
  const e = init.expression;
  return e && (ts.isStringLiteral(e) || ts.isNoSubstitutionTemplateLiteral(e)) ? e.text : null;
}

/** Колонка сетки правки: объект в `columns` у `<EditGrid>` или под типом `EditGridColumn`. */
function inEditColumn(node, sf) {
  for (let a = node.parent; a && !ts.isSourceFile(a); a = a.parent) {
    if (a.type && a.type.getText(sf).includes('EditGridColumn')) return true;
    if (ts.isJsxAttribute(a) && a.name.getText(sf) === 'columns')
      return a.parent.parent.tagName.getText(sf) === 'EditGrid';
  }
  return false;
}

/**
 * Поле клетки не того вида: сырое `<input>` / `<textarea>` (у поля клетки вид общий), дата без
 * `density="cell"` (размера места она не берёт), поле со своим размером не клетки (`density="dense"` дал бы
 * рамку в клетке), голое поле подсказки (`compact`) и выбор «внутри фразы» (`variant`).
 */
function isWrongCellField(element, sf) {
  const tag = element.tagName.getText(sf);
  if (tag === 'input') return !NOT_A_FIELD.has(attr(element, 'type', sf));
  if (tag === 'textarea') return true;
  if (tag === 'DateInput') return attr(element, 'density', sf) !== 'cell';
  if (!PLACED.has(tag)) return false;
  const density = attr(element, 'density', sf);
  if (density !== undefined && density !== 'cell') return true;
  return (tag === 'SuggestField' && attr(element, 'compact', sf) !== undefined)
    || (tag === 'Select' && attr(element, 'variant', sf) !== undefined);
}

/** Сколько полей в клетках сетки правки — не размера клетки. */
export function countCellFields(text) {
  if (!text.includes('cell')) return 0;
  const sf = parse(text);
  let n = 0;
  const inCell = node => {
    if ((ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) && isWrongCellField(node, sf)) n++;
    ts.forEachChild(node, inCell);
  };
  const visit = node => {
    if ((ts.isPropertyAssignment(node) || ts.isMethodDeclaration(node)) && node.name.getText(sf) === 'cell'
      && inEditColumn(node, sf)) {
      inCell(node);
      return;
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return n;
}

export const cellRules = () => [
  {
    id: 'cell-field',
    // Поле в клетке EditGrid не размера клетки ([UI-78]): рамка в клетке, вторая черта под чертой строки,
    // у каждого экрана своя высота. Уровень — 0 (sharedui-md3-14 PR 2): клетки переведены все.
    count: countCellFields,
    instead: 'поле в клетке EditGrid — общее поле размера клетки: TextField, Select (без подписи — density="cell"), NumberInput, SuggestField берут его от сетки сами, DateInput — density="cell"; своей рамки, density="dense", compact и variant="inline" в клетке нет (docs/UI.md, «Плотность»)',
  },
];
