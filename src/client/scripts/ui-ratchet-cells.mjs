// Поле в клетке сетки правки для храповика интерфейса — правило `cell-field` ([UI-78], sharedui-md3-14). Своим
// модулем, как ui-ratchet-inputs.mjs: lib и его тест стоят у порога размера.
//
// Клетка — значение свойства `cell` колонки `EditGrid`: колонки, переданной прямо в `columns` сетки, или
// объявленной с типом `EditGridColumn` (функция колонок экрана, `.map((…): EditGridColumn<…> => …)`). Колонки
// `DataGrid` с тем же `cell` не считаются: полей правки там нет.
//
// Клетку экран чаще рисует своим компонентом (`Cell` строк счёта, `TableCell` листа), а не полем прямо в
// колонке, — и правило заходит в него: в свой файл и в модуль `features/`, откуда он взят. На один шаг: компонент
// внутри компонента клетки — уже окно или пикер, а не клетка. Нарушение считается файлу с колонками — правило
// видит один файл за раз; отказ называет компонент.
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';
import { importBase, moduleCandidates } from './ui-ratchet-imports.mjs';

/** Поля, которым тело `EditGrid` отдаёт размер клетки само: им вредит только чужой размер. */
const PLACED = new Set(['TextField', 'Select', 'SuggestField', 'NumberInput', 'PlaceSelect']);
/** Поля формы со своей оболочкой: в клетке им не место ни в каком размере. */
const FORM_ONLY = new Set(['DateField', 'TextAreaField']);
const NOT_A_FIELD = new Set(['checkbox', 'radio', 'file', 'hidden', 'range', 'color']);
/** Слои — новое место: размер клетки туда не идёт (`FieldDensityContext` сбрасывают они сами), и поля там свои. */
const LAYERS = new Set(['Modal', 'SideSheet', 'BottomSheet', 'ConfirmDialog']);

const SRC = fileURLToPath(new URL('../src/', import.meta.url));

let parsed = { text: null, sf: null };
function parse(text) {
  if (parsed.text !== text) parsed = { text, sf: ts.createSourceFile('x.tsx', text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX) };
  return parsed.sf;
}

/** Модули с компонентами клеток — с диска, один раз за прогон. */
const modules = new Map();
function parseModule(path) {
  if (!modules.has(path))
    modules.set(path, ts.createSourceFile(path, readFileSync(path, 'utf8'), ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX));
  return modules.get(path);
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
 * `density="cell"` (размера места она не берёт), поле формы со своей оболочкой, поле со своим размером не клетки
 * (`density="dense"` дал бы рамку в клетке), голое поле подсказки (`compact`) и выбор «внутри фразы» (`variant`).
 */
function isWrongCellField(element, sf) {
  const tag = element.tagName.getText(sf);
  if (tag === 'input') return !NOT_A_FIELD.has(attr(element, 'type', sf));
  if (tag === 'textarea' || FORM_ONLY.has(tag)) return true;
  if (tag === 'DateInput') return attr(element, 'density', sf) !== 'cell';
  if (!PLACED.has(tag)) return false;
  const density = attr(element, 'density', sf);
  if (density !== undefined && density !== 'cell') return true;
  return (tag === 'SuggestField' && attr(element, 'compact', sf) !== undefined)
    || (tag === 'Select' && attr(element, 'variant', sf) !== undefined);
}

/** Объявление компонента `name` в модуле: `function Name`, `const Name = …`. */
function declarationOf(sf, name) {
  for (const s of sf.statements) {
    if (ts.isFunctionDeclaration(s) && s.name?.text === name) return s;
    if (ts.isVariableStatement(s))
      for (const d of s.declarationList.declarations)
        if (ts.isIdentifier(d.name) && d.name.text === name && d.initializer) return d.initializer;
  }
  return null;
}

/**
 * Модуль на диске по пути импорта — только `features/`: поля и примитивы `shared/` свой вид держат сами. Путь
 * разрешается так же, как у `raw-field` (ui-ratchet-imports.mjs).
 */
function resolveImport(spec, file) {
  const base = importBase(spec, file || null);
  if (base == null || !base.startsWith('features/')) return null;
  return moduleCandidates(base).map(p => join(SRC, p)).find(existsSync) ?? null;
}

/** Где объявлен компонент клетки: в этом же файле или в импортированном модуле `features/`. */
function componentOf(name, sf, file) {
  const local = declarationOf(sf, name);
  if (local) return { node: local, sf };
  for (const s of sf.statements) {
    if (!ts.isImportDeclaration(s) || !s.importClause?.namedBindings || !ts.isNamedImports(s.importClause.namedBindings)) continue;
    const spec = s.importClause.namedBindings.elements.find(e => e.name.text === name);
    if (!spec) continue;
    const path = resolveImport(s.moduleSpecifier.text, file);
    if (!path) return null;
    const theirs = parseModule(path);
    const node = declarationOf(theirs, (spec.propertyName ?? spec.name).text);
    return node ? { node, sf: theirs } : null;
  }
  return null;
}

/** Сколько полей в клетках сетки правки — не размера клетки. */
export function countCellFields(text, file) {
  if (!text.includes('cell')) return 0;
  const sf = parse(text);
  let n = 0;
  const seen = new Set();
  // `follow` — заходить ли в компоненты: только из самой клетки, не из компонента клетки.
  const inCell = (node, where, follow) => {
    if (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) {
      const tag = node.tagName.getText(where);
      if (LAYERS.has(tag)) return;
      if (isWrongCellField(node, where)) n++;
      else if (follow && /^[A-Z]/.test(tag) && !PLACED.has(tag) && !seen.has(tag)) {
        seen.add(tag);
        const component = componentOf(tag, where, file);
        if (component) inCell(component.node, component.sf, false);
      }
    }
    if (ts.isJsxElement(node) && LAYERS.has(node.openingElement.tagName.getText(where))) return;
    ts.forEachChild(node, child => inCell(child, where, follow));
  };
  const visit = node => {
    if ((ts.isPropertyAssignment(node) || ts.isMethodDeclaration(node)) && node.name.getText(sf) === 'cell'
      && inEditColumn(node, sf)) {
      inCell(node, sf, true);
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
    instead: 'поле в клетке EditGrid — общее поле размера клетки: TextField, Select (без подписи — density="cell"), NumberInput, SuggestField берут его от сетки сами, DateInput — density="cell"; своей рамки, density="dense", compact, variant="inline", DateField и TextAreaField в клетке нет. Смотрите и компонент, которым колонка рисует клетку (Cell, TableCell…), — он может жить в другом файле (docs/UI.md, «Плотность»)',
  },
];
