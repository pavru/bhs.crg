#!/usr/bin/env node
// Перевод значков lucide на набор Material Symbols ([UI-60], [UI-61], sharedui-md3-03) — перезапускаемой
// командой по таблице md3-icon-map.json, как перевод классов (md3-migrate.mjs, D3 зонтика): ветка,
// начатая до перевода, обновляется механически. Зовётся из `npm run md3:migrate`.
//
// Что переводит:
//   import { Trash2 } from 'lucide-react'      → import { Delete } from '@/shared/ui/icons' (+ Icon)
//   <Trash2 size={14} strokeWidth={2} />       → <Icon icon={Delete} size={18} />  (размер — на ступень шкалы)
//   icon: Trash2, icon={Trash2}                → icon: Delete, icon={Delete}
//   LucideIcon                                 → IconDef
//   <Loader2 className="animate-spin" />       → <CircularProgress />
//   <Icon size={14} /> и <it.icon />, где тег — значок lucide в переменной → <Icon icon={Icon} … />
//
// Значок в переменной узнаётся по ТИПУ (TypeScript), а не по имени: `icon: Icon` в пропсах, `const Icon =
// it.icon`, `<it.icon />`. Пакет lucide-react для этого не нужен — модуль подменяет заглушка из таблицы,
// поэтому команда работает и на ветке, обновлённой после удаления пакета.
//
// Значок вне таблицы — отказ с перечнем до записи: угаданная замена хуже отказа. Что нельзя перевести
// без смысла места (вращение по условию, размер выражением, `fill`) — отчёт с файлом и строкой.

import { readFileSync } from 'node:fs';
import { dirname, join, posix, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';
import { ICON_SCALE } from './ui-ratchet-lib.mjs';
import iconMap from './md3-icon-map.json' with { type: 'json' };

const clientRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
/** Путь заглушки — с прямыми косыми: так пути видит TypeScript. */
const STUB = join(clientRoot, '__lucide-react__.d.ts').replaceAll('\\', '/');

/** Ступень шкалы для прежнего размера: ближайшая не меньше (9…18 → 18, 22 → 24, 28…36 → 40). */
export const toScale = px => ICON_SCALE.find(s => px <= s) ?? ICON_SCALE.at(-1);

/** Модуль lucide-react для проверки типов — из имён таблицы: значок — `LucideIcon`, тип — он сам. */
export function lucideStub(map = iconMap.icons) {
  return [
    'export interface LucideProps { size?: number | string; strokeWidth?: number | string; absoluteStrokeWidth?: boolean; className?: string; [attr: string]: unknown }',
    'export interface LucideIcon { (props: LucideProps): any; displayName?: string }',
    ...Object.keys(map).filter(k => !map[k].type).map(k => `export declare const ${k}: LucideIcon;`),
  ].join('\n');
}

/**
 * Программа TypeScript над файлами `files` (абсолютные пути) с заглушкой вместо lucide-react.
 * `read(path)` отдаёт текст файла проекта (в тестах — фикстуру), остальное — с диска.
 */
export function iconProgram(files, { read = path => readFileSync(path, 'utf8'), map = iconMap.icons } = {}) {
  const options = { jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2023, module: ts.ModuleKind.ESNext,
    moduleResolution: ts.ModuleResolutionKind.Bundler, strict: true, noEmit: true, skipLibCheck: true,
    allowImportingTsExtensions: true, baseUrl: clientRoot, paths: { '@/*': ['./src/*'] }, types: [] };
  const host = ts.createCompilerHost(options);
  const own = new Set(files.map(f => f.replaceAll('\\', '/')));
  const stub = lucideStub(map);
  host.readFile = path => (path === STUB ? stub : own.has(path.replaceAll('\\', '/')) ? read(path) : ts.sys.readFile(path));
  host.fileExists = path => path === STUB || own.has(path.replaceAll('\\', '/')) || ts.sys.fileExists(path);
  host.resolveModuleNameLiterals = (literals, containing, _r, opts) => literals.map(lit => lit.text === 'lucide-react'
    ? { resolvedModule: { resolvedFileName: STUB, extension: ts.Extension.Dts } }
    : ts.resolveModuleName(lit.text, containing, opts, host));
  return ts.createProgram({ rootNames: files, options, host });
}

/** Значок lucide по типу: `LucideIcon` заглушки, в том числе в объединении с undefined. */
function isLucideType(type) {
  return (type.isUnion() ? type.types : [type]).some(t => (t.aliasSymbol ?? t.symbol)?.name === 'LucideIcon'
    && (t.aliasSymbol ?? t.symbol).declarations?.some(d => d.getSourceFile().fileName === STUB));
}

const SIZE_CLASS = /^(?:size|w|h)-(\d+(?:\.5)?)$/;
const SPIN_CLASS = /^(?:animate-spin|motion-reduce:animate-none)$/;

/** Значение строкового атрибута (`"…"`, `{'…'}`, `{`…`}` без подстановок) или null. */
function staticString(init) {
  const node = init && ts.isJsxExpression(init) ? init.expression : init;
  return node && (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) ? node.text : null;
}

/** Размер выражением: литерал — на ступень, условие с литералами — по ветвям; иначе null. */
function scaleExpr(node, sf) {
  if (ts.isParenthesizedExpression(node)) return scaleExpr(node.expression, sf);
  if (ts.isNumericLiteral(node)) return String(toScale(Number(node.text)));
  if (ts.isConditionalExpression(node)) {
    const a = scaleExpr(node.whenTrue, sf), b = scaleExpr(node.whenFalse, sf);
    if (a === null || b === null) return null;
    return a === b ? a : `${node.condition.getText(sf)} ? ${a} : ${b}`;
  }
  return null;
}

/** Импорт из shared/ui: внутри shared/ui (и его подпапок) — относительным путём, снаружи — через `@/`. */
const uiModule = (file, name) => {
  if (!file.startsWith('shared/ui/')) return `@/shared/ui/${name}`;
  const rel = posix.relative(posix.dirname(file), `shared/ui/${name}`);
  return rel.startsWith('.') ? rel : `./${rel}`;
};

/** Импорт lucide, который команда не переводит: целиком, по умолчанию, реэкспорт из пакета. */
function refusedForm(st) {
  if (ts.isExportDeclaration(st)) return `export … from 'lucide-react'`;
  const clause = st.importClause;
  if (clause?.name) return `import ${clause.name.text} from 'lucide-react'`;
  if (clause?.namedBindings && ts.isNamespaceImport(clause.namedBindings)) return `import * as ${clause.namedBindings.name.text} from 'lucide-react'`;
  return null;
}

/**
 * Перевод одного файла. `file` — путь от src/ (для `files` таблицы и путей импорта). Возвращает текст,
 * число замен, отчёт (`{ line, note }`), имена lucide вне таблицы и формы импорта, которые не переводятся.
 */
export function migrateIconFile(sf, checker, { file, map = iconMap.icons }) {
  const text = sf.text;
  const edits = [];
  const notes = [];
  const unknown = [];
  let changes = 0;
  const lineOf = node => sf.getLineAndCharacterOfPosition(node.getStart(sf)).line + 1;
  const note = (node, msg) => notes.push({ line: lineOf(node), note: msg });
  const edit = (start, end, to) => edits.push({ start, end, to });

  // Импорт целиком или по умолчанию — отказ: `L.Trash2` по имени не узнать, а удалённый импорт оставил бы
  // его висеть.
  const lucideStatements = sf.statements.filter(st => (ts.isImportDeclaration(st) || ts.isExportDeclaration(st))
    && st.moduleSpecifier && ts.isStringLiteral(st.moduleSpecifier) && st.moduleSpecifier.text === 'lucide-react');
  const refused = lucideStatements.map(refusedForm).filter(Boolean);
  if (refused.length) return { text, changes: 0, notes: [], unknown: [], refused };

  // Импорты lucide: символ привязки → запись таблицы (с поправкой места из `files`).
  const lucideImports = lucideStatements.filter(ts.isImportDeclaration);
  const bound = new Map();
  for (const decl of lucideImports)
    for (const el of decl.importClause?.namedBindings?.elements ?? []) {
      const name = (el.propertyName ?? el.name).text;
      const entry = map[name];
      if (!entry) { unknown.push(name); continue; }
      bound.set(checker.getSymbolAtLocation(el.name), { name, ...entry, ...entry.files?.[file] });
    }
  // Символ под именем; у `{ Icon }` — переменная, а не свойство.
  const symOf = id => (ts.isShorthandPropertyAssignment(id.parent) && id.parent.name === id
    ? checker.getShorthandAssignmentValueSymbol(id.parent) : checker.getSymbolAtLocation(id));
  const lucideOf = id => bound.get(symOf(id));

  // Имена в области файла, кроме lucide. Имя свойства (`a.Search`, `{ Search: 1 }`, атрибут) имени в
  // области не занимает — его не считаем.
  const MEMBER = [ts.isPropertyAccessExpression, ts.isPropertyAssignment, ts.isPropertySignature, ts.isPropertyDeclaration,
    ts.isMethodDeclaration, ts.isJsxAttribute];
  const member = node => (MEMBER.some(is => is(node.parent)) && node.parent.name === node)
    || (ts.isQualifiedName(node.parent) && node.parent.right === node)
    || (ts.isBindingElement(node.parent) && node.parent.propertyName === node);
  const names = new Map();
  const collect = node => {
    if (ts.isIdentifier(node) && !member(node) && !bound.has(symOf(node))) (names.get(node.text) ?? names.set(node.text, new Set()).get(node.text)).add(symOf(node));
    ts.forEachChild(node, collect);
  };
  sf.statements.filter(st => !lucideImports.includes(st)).forEach(collect);

  // Переменная со значком под именем компонента (`const Icon = it.icon`, `{ icon: Icon }`) — со строчной:
  // после перевода это значение, а не компонент, и имя `Icon` нужно самому компоненту. Экспортируемые
  // и те, чьё новое имя занято, не трогаем: тогда компонент получит псевдоним. Имя, занятое только такими
  // же переименованными (`Icon` в двух функциях), свободно: каждая получает `icon` в своей области.
  const renamed = new Map();
  const declared = node => {
    if ((ts.isVariableDeclaration(node) || ts.isBindingElement(node) || ts.isParameter(node))
      && ts.isIdentifier(node.name) && /^[A-Z]/.test(node.name.text)) {
      const sym = checker.getSymbolAtLocation(node.name);
      const to = node.name.text[0].toLowerCase() + node.name.text.slice(1);
      const exported = ts.isVariableDeclaration(node) && ts.getCombinedModifierFlags(node) & ts.ModifierFlags.Export;
      const holders = names.get(to);
      if (sym && !exported && (!holders || [...holders].every(h => renamed.get(h) === to))
        && isLucideType(checker.getTypeOfSymbolAtLocation(sym, node.name))) {
        renamed.set(sym, to);
        (holders ?? names.set(to, new Set()).get(to)).add(sym);
      }
    }
    ts.forEachChild(node, declared);
  };
  declared(sf);
  const taken = new Set([...names].filter(([, syms]) => [...syms].some(sym => !renamed.has(sym))).map(([n]) => n));
  for (const to of renamed.values()) taken.add(to);
  const exprText = node => (ts.isIdentifier(node) && renamed.get(symOf(node))) || node.getText(sf);
  // Импорты файла из shared/ui (ветка, начатая после перевода, уже их знает) — дописываются, а не дублируются.
  const MODULES = ['Icon', 'CircularProgress', 'icons'];
  const moduleOf = name => (MODULES.includes(name) ? name : 'icons');
  const uiImports = mod => sf.statements.filter(st => ts.isImportDeclaration(st)
    && [uiModule(file, mod), `@/shared/ui/${mod}`].includes(st.moduleSpecifier.text)
    && st.importClause?.namedBindings && ts.isNamedImports(st.importClause.namedBindings));
  /** Локальное имя `name`, уже импортированное из своего модуля (значение — не из `import type`). */
  const imported = name => {
    for (const decl of uiImports(moduleOf(name)))
      for (const el of decl.importClause.namedBindings.elements)
        if ((el.propertyName ?? el.name).text === name && (name === 'IconDef' || !(decl.importClause.isTypeOnly || el.isTypeOnly)))
          return el.name.text;
    return null;
  };
  const reused = new Set();
  // Псевдоним при совпадении: `alt`, затем `alt2`, `alt3`… Первое обращение решает за весь файл.
  const aliases = new Map();
  const local = (name, alt) => {
    if (!aliases.has(name) && imported(name)) { aliases.set(name, imported(name)); reused.add(name); }
    if (!aliases.has(name)) {
      let alias = name;
      for (let n = 1; taken.has(alias); n++) alias = n === 1 ? alt : `${alt}${n}`;
      aliases.set(name, alias);
    }
    return aliases.get(name);
  };
  const values = new Set();
  const value = name => { values.add(name); return local(name, name.endsWith('Icon') ? name.replace(/Icon$/, 'Glyph') : `${name}Icon`); };
  let usesIcon = false, usesSpinner = false, usesType = false;
  const handled = new Set();

  /** Атрибуты значка: размер на ступень, `strokeWidth` долой, размер классом — в `size` (его и вернёт). */
  const attributes = (node, spinner) => {
    const props = node.attributes.properties;
    const byName = n => props.find(p => ts.isJsxAttribute(p) && p.name.getText(sf) === n);
    const drop = p => edit(p.getFullStart(), p.end, '');
    let classSize = null;
    for (const p of props) {
      if (ts.isJsxSpreadAttribute(p)) { note(p, 'значок с {...пропсами} — сверить, что в них нет size и strokeWidth'); continue; }
      const name = p.name.getText(sf);
      if (name === 'strokeWidth' || name === 'absoluteStrokeWidth') drop(p);
      else if (name === 'size' && p.initializer) {
        const expr = ts.isJsxExpression(p.initializer) ? p.initializer.expression : p.initializer;
        const scaled = expr && scaleExpr(expr, sf);
        if (scaled === null) note(p, `размер выражением ${p.initializer.getText(sf)} — тип IconSize, ступень сверить руками`);
        else edit(p.initializer.getStart(sf), p.initializer.end, `{${scaled}}`);
      } else if (name === 'className' && p.initializer) {
        const cls = staticString(p.initializer);
        if (cls === null) {
          const expr = p.initializer.getText(sf);
          if (/(?<![\w-])animate-spin(?![\w-])/.test(expr))
            note(p, spinner ? 'вращение по условию у индикатора — сверить' : 'вращение значка по условию — показать CircularProgress на время хода руками');
          if (/(?<![\w:-])(?:size|w|h)-\d/.test(expr))
            note(p, 'размер классом в выражении className — перенести в проп size руками');
          continue;
        }
        const tokens = cls.split(/\s+/).filter(Boolean);
        const sizes = tokens.map(t => SIZE_CLASS.exec(t)?.[1]).filter(Boolean);
        const keep = tokens.filter(t => !SIZE_CLASS.test(t) && !(spinner && SPIN_CLASS.test(t)));
        if (!spinner && tokens.some(t => t === 'animate-spin')) note(p, 'вращающийся значок — показать CircularProgress руками');
        if (keep.length === tokens.length) continue;
        if (sizes.length) classSize = toScale(Math.max(...sizes.map(Number)) * 4);
        if (keep.length) edit(p.initializer.getStart(sf), p.initializer.end, JSON.stringify(keep.join(' ')));
        else drop(p);
      } else if (name === 'fill' || name === 'width' || name === 'height') {
        drop(p);
        note(p, `${name} у значка снят — заливка только у навигации, размер только пропом size`);
      } else if (spinner && name === 'aria-hidden') drop(p);
      else if (spinner && name === 'aria-label') edit(p.name.getStart(sf), p.name.end, 'label');
      else if (spinner) note(p, `атрибут ${name} у индикатора хода — у CircularProgress его нет`);
    }
    if (classSize !== null && !byName('size')) edit(node.tagName.end, node.tagName.end, ` size={${classSize}}`);
    return classSize;
  };

  /** Значок в низком контейнере: один из четырёх ближних предков с `h-N` ниже нового размера — отчёт для глаз. */
  const lowContainer = (node, size) => {
    let el = node.parent;
    for (let depth = 0; el && depth < 4; el = el.parent) {
      if (!ts.isJsxElement(el)) continue;
      depth++;
      // Текст значения целиком: класс часто собирают шаблоном (`${className}`) — высота в нём та же.
      const cls = el.openingElement.attributes.properties.find(p => ts.isJsxAttribute(p) && p.name.getText(sf) === 'className')?.initializer?.getText(sf);
      const h = cls && /(?<![\w:-])h-(\d+(?:\.5)?)(?![\w.])/.exec(cls);
      if (h && Number(h[1]) * 4 < size) return note(node, `значок ${size} px в контейнере h-${h[1]} (${Number(h[1]) * 4} px) — проверить на глаз`);
    }
  };

  const visit = node => {
    if ((ts.isJsxSelfClosingElement(node) || ts.isJsxOpeningElement(node)) && !handled.has(node)) {
      const tag = node.tagName;
      const entry = ts.isIdentifier(tag) ? lucideOf(tag) : undefined;
      const dynamic = !entry && /^[A-Z]|\./.test(tag.getText(sf)) && isLucideType(checker.getTypeAtLocation(tag));
      if (entry || dynamic) {
        handled.add(tag);
        const closing = ts.isJsxOpeningElement(node) ? node.parent.closingElement : null;
        if (entry?.spinner) {
          usesSpinner = true;
          edit(tag.getStart(sf), tag.end, local('CircularProgress', 'Progress'));
          if (closing) { handled.add(closing.tagName); edit(closing.tagName.getStart(sf), closing.tagName.end, local('CircularProgress', 'Progress')); }
          attributes(node, true);
        } else {
          usesIcon = true;
          const iconTag = local('Icon', 'Glyph');
          edit(tag.getStart(sf), tag.end, `${iconTag} icon={${entry ? value(entry.export) : exprText(tag)}}`);
          if (closing) { handled.add(closing.tagName); edit(closing.tagName.getStart(sf), closing.tagName.end, iconTag); }
          const classSize = attributes(node, false);
          const size = node.attributes.properties.find(p => ts.isJsxAttribute(p) && p.name.getText(sf) === 'size');
          const lit = size?.initializer && ts.isJsxExpression(size.initializer) && size.initializer.expression;
          lowContainer(node, lit && ts.isNumericLiteral(lit) ? toScale(Number(lit.text)) : classSize ?? 24);
        }
        changes++;
      }
    } else if (ts.isIdentifier(node) && !handled.has(node) && renamed.has(symOf(node)) && !member(node)) {
      const to = renamed.get(symOf(node));
      const el = node.parent;
      if (ts.isBindingElement(el) && el.name === node && el.propertyName?.getText(sf) === to)
        edit(el.propertyName.getStart(sf), node.end, to);   // `{ icon: Icon }` → `{ icon }`
      else {
        // `{ Icon }` в деструктуризации и `{ Icon }` в литерале объекта — имя свойства остаётся прежним.
        const shorthand = ts.isShorthandPropertyAssignment(el)
          || (ts.isBindingElement(el) && el.name === node && !el.propertyName && ts.isObjectBindingPattern(el.parent));
        edit(node.getStart(sf), node.end, shorthand ? `${node.text}: ${to}` : to);
      }
    } else if (ts.isExportSpecifier(node) && !node.parent.parent.moduleSpecifier) {
      // `export { Trash2 }` — наружу под прежним именем: потребители узнают значок по типу и переводятся
      // тем же прогоном.
      const own = node.propertyName ?? node.name;
      const entry = bound.get(checker.getExportSpecifierLocalTargetSymbol(node) ?? checker.getSymbolAtLocation(own));
      if (entry && !entry.type && !entry.spinner) {
        const to = value(entry.export);
        edit(own.getStart(sf), own.end, node.propertyName ? to : `${to} as ${node.name.text}`);
        note(node, `значок ${node.name.text} экспортируется под прежним именем — теперь это IconDef`);
        changes++;
      } else if (entry) note(node, `${node.name.text} экспортируется — перевести руками`);
      return;
    } else if (ts.isIdentifier(node) && !handled.has(node) && !ts.isImportSpecifier(node.parent)) {
      const entry = lucideOf(node);
      // `typeof Trash2` — тип значка, а не конкретного: IconDef, иначе `typeof Database` потребовал бы заливку.
      const query = entry && !entry.type && !entry.spinner && ts.isTypeQueryNode(node.parent) ? node.parent : null;
      if (entry?.type || query) {
        usesType = true;
        edit((query ?? node).getStart(sf), (query ?? node).end, local('IconDef', 'IconDefType'));
        changes++;
      }
      else if (entry?.spinner) note(node, 'Loader2 значением — индикатор хода ставят элементом <CircularProgress />, руками');
      else if (entry) {
        const to = value(entry.export);
        edit(node.getStart(sf), node.end, ts.isShorthandPropertyAssignment(node.parent) ? `${node.text}: ${to}` : to);
        changes++;
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);

  // Импорты: недостающие имена — в уже стоящий импорт модуля, иначе новой строкой на место первого
  // lucide; остальные lucide — долой.
  const spec = n => (local(n) === n ? n : `${n} as ${local(n)}`);
  const lines = [];
  for (const [mod, vals, types] of [['Icon', usesIcon ? ['Icon'] : [], []],
    ['CircularProgress', usesSpinner ? ['CircularProgress'] : [], []], ['icons', [...values].sort(), usesType ? ['IconDef'] : []]]) {
    const v = vals.filter(n => !reused.has(n)), t = types.filter(n => !reused.has(n));
    if (!v.length && !t.length) continue;
    const decls = uiImports(mod);
    const into = decls.find(d => !d.importClause.isTypeOnly) ?? (v.length ? undefined : decls[0]);
    if (into) {
      const list = [...v.map(spec), ...t.map(n => (into.importClause.isTypeOnly ? spec(n) : `type ${spec(n)}`))].join(', ');
      const els = into.importClause.namedBindings.elements;
      if (els.length) edit(els.at(-1).end, els.at(-1).end, `, ${list}`);
      else edit(into.importClause.namedBindings.getStart(sf) + 1, into.importClause.namedBindings.end - 1, ` ${list} `);
    } else if (v.length) lines.push(`import { ${[...v.map(spec), ...t.map(n => `type ${spec(n)}`)].join(', ')} } from '${uiModule(file, mod)}';`);
    else lines.push(`import type { ${t.map(spec).join(', ')} } from '${uiModule(file, mod)}';`);
  }
  if (lucideImports.length) {
    lucideImports.forEach((decl, i) => (i === 0 && lines.length
      ? edit(decl.getStart(sf), decl.end, lines.join('\n'))
      : i === 0 ? edit(decl.getStart(sf), decl.end + (/^\r?\n/.exec(text.slice(decl.end))?.[0].length ?? 0), '')
        : edit(decl.getFullStart(), decl.end, '')));
  } else if (lines.length) {
    const last = sf.statements.filter(ts.isImportDeclaration).at(-1);
    edit(last ? last.end : 0, last ? last.end : 0, `${last ? '\n' : ''}${lines.join('\n')}${last ? '' : '\n'}`);
  }

  edits.sort((a, b) => b.start - a.start || b.end - a.end);
  let out = text;
  for (const e of edits) out = out.slice(0, e.start) + e.to + out.slice(e.end);
  return { text: out, changes, notes, unknown, refused: [] };
}

/**
 * Перевод файлов `paths` (абсолютные). Файлы без lucide и без значков в переменных не меняются; если
 * lucide нигде нет, программа TypeScript не строится вовсе — повторный запуск мгновенный.
 */
export function migrateIcons(paths, { read: readFile = path => readFileSync(path, 'utf8'), srcRoot = join(clientRoot, 'src'), map = iconMap.icons } = {}) {
  // Каждый файл читается один раз: и для проверки «есть ли lucide», и программой TypeScript.
  const texts = new Map();
  const read = path => (texts.has(path) ? texts.get(path) : texts.set(path, readFile(path)).get(path));
  const code = paths.filter(p => /\.tsx?$/.test(p) && !p.endsWith('.d.ts'));
  if (!code.some(p => read(p).includes('lucide-react'))) return { files: [], unknown: [], refused: [] };
  const program = iconProgram(code, { read, map });
  const checker = program.getTypeChecker();
  const files = [];
  const unknown = new Map();
  const refused = [];
  for (const path of code) {
    const sf = program.getSourceFile(path);
    if (!sf) continue;
    const file = relative(srcRoot, path).replaceAll('\\', '/');
    const result = migrateIconFile(sf, checker, { file, map });
    for (const name of result.unknown) (unknown.get(name) ?? unknown.set(name, []).get(name)).push(file);
    for (const form of result.refused) refused.push({ form, file });
    if (result.changes || result.notes.length) files.push({ path, file, ...result });
  }
  return { files, unknown: [...unknown].map(([name, where]) => ({ name, where })), refused };
}

/** Отчёт текстом: число замен, затем места для глаз с `файл:строка`. */
export function formatIconReport({ files }, { check = false } = {}) {
  const changed = files.filter(f => f.changes);
  const out = [`md3:migrate, значки: замен ${changed.reduce((n, f) => n + f.changes, 0)}${check ? ' (не записано, --check)' : ''} в ${changed.length} файлах.`];
  const notes = files.flatMap(f => f.notes.map(n => `  ${f.file}:${n.line} ${n.note}`));
  if (notes.length) out.push('', `Проверить руками (${notes.length}):`, ...notes);
  return out.join('\n');
}
