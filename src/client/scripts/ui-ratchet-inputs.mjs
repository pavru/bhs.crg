// Свои поля и радиокнопки для храповика интерфейса — правила `native-radio` и `raw-field` ([UI-69],
// sharedui-md3-11) и то, что они считают: элемент — из `countElements` (ui-ratchet-lib.mjs, передаётся в
// `inputRules`). Отдельно от него по той же причине, что ui-ratchet-buttons.mjs: там разбор дерева и общий
// перечень правил, здесь — что считать своим контролом; иначе lib и его тест переходят порог размера.
import ts from 'typescript';
import { importBase, moduleCandidates } from './ui-ratchet-imports.mjs';

/** Значение атрибута как текст: `"radio"`, `'radio'`, `{'radio'}`, `{"radio"}` → `radio`. */
const literal = raw => /^\{?\s*(['"`])(.*)\1\s*\}?$/.exec(raw ?? '')?.[2] ?? null;

/** Своя радиокнопка — нативная `<input type="radio">` или элемент разметки с `role="radio"`. */
export function isNativeRadio({ tag, attrs }) {
  return (tag === 'input' && literal(attrs.get('type')) === 'radio') || literal(attrs.get('role')) === 'radio';
}

// Своя рамка — класс border в основном виде или с вариантом (focus:border-primary), кроме снятой
// (border-0, border-none, border-transparent): поле без рамки — клетка таблицы или строка внутри
// оболочки, и рамку ему рисует место. Флажок, радиокнопку, файл и скрытое поле считают свои правила или
// не считает никто. Ищется в тексте строк и кусков шаблона, а не в коде: переменная `border` — не класс.
const OWN_BORDER = /(?<![\w-])(?:[\w-]+:)*border(?![\w-]*(?:-0|-none|-transparent)(?![\w-]))(?=[\s'"`}-]|$)/;
const NOT_A_FIELD = new Set(['checkbox', 'radio', 'file', 'hidden', 'range', 'color']);

/**
 * Объявленные исключения: модуль (путь от `src/`) и имя в нём, а не текст имени (D4 sharedui-md3-19) — иначе
 * прошли бы `import { FIELD_CLS as X }` и своя локальная `const FIELD_CLS = 'border …'` другого файла.
 * Снимается вклад только самой константы, где бы на неё ни сослались, в том числе внутри её модуля:
 * `${FIELD_CLS} border-b` остаётся красным.
 */
const ALLOWED = new Set([
  // Общая коробка условия отбора — плотное поле [UI-69] (h-10, rounded-xs, border-outline, фокус кольцом), той
  // же высоты, что плотный Select рядом с ним в строке отбора (#1469), а не самоделка. Перевод отбора на
  // TextField density="dense" — отдельной задачей, не храповиком.
  'shared/filter/fieldClass.ts#FIELD_CLS',
]);

// Класс поля живёт не только строкой в разметке (D3 sharedui-md3-19, [UI-69]):
// - константой файла — строка, шаблон, склейка, условие, другое имя, вызов, в скобках, с `as` и `satisfies`;
// - результатом функции файла — `const cls = f(x)`, `className={f<T>(x)}`, `f?.()`: раскрываются `return` и тело
//   стрелки `f`. Функция раскрывается только вызовом: компонент, названный по имени, — не класс;
// - свойством объекта — `styles.input` при `const styles = { input: '…' }`;
// - константой, функцией или объектом другого модуля `src/` по импорту — именованному, по умолчанию и
//   пространства имён (`ns.box`); путь — ui-ratchet-imports.mjs, реэкспорты `export { X } from` раскрываются.
//   Пакеты вне `src/` не раскрываются.
// Код обходится деревом разбора: рамка ищется в строках, имена — в идентификаторах, так что слово класса
// (`'grid gap-2'`) за имя не принимается. Одно имя в разных областях видимости — все его значения: рамка хоть
// в одном считается. Слепые места названы в docs/UI.md: класс пропом, `export *`, `a.b.c` и `a[k]`.
const unwrap = n => (ts.isParenthesizedExpression(n) || ts.isAsExpression(n) || ts.isSatisfiesExpression(n)
  ? unwrap(n.expression) : n);
const CLASS_EXPR = n => ts.isStringLiteralLike(n) || ts.isTemplateExpression(n) || ts.isBinaryExpression(n)
  || ts.isConditionalExpression(n) || ts.isCallExpression(n) || ts.isIdentifier(n);
const FUNC_EXPR = n => ts.isArrowFunction(n) || ts.isFunctionExpression(n);
const isDefault = n => ts.getCombinedModifierFlags(n) & ts.ModifierFlags.Default;

/** Что функция отдаёт: тело стрелки-выражения или `return` её собственного тела (не вложенных функций). */
function returns(fn) {
  if (!fn.body) return [];
  if (!ts.isBlock(fn.body)) return [fn.body];
  const out = [];
  const visit = node => {
    if (ts.isReturnStatement(node) && node.expression) out.push(node.expression);
    if (!ts.isFunctionLike(node)) ts.forEachChild(node, visit);
  };
  ts.forEachChild(fn.body, visit);
  return out;
}

/**
 * Разбор модуля — узлы, а не тексты: значения, функции и объекты по имени, импорты (именованные, по умолчанию,
 * пространства имён), локальные экспорты под другим именем и реэкспорты.
 */
function scan(sf) {
  const mod = { sf, values: new Map(), fns: new Map(), objects: new Map(), imports: new Map(), namespaces: new Map(),
    exportAs: new Map(), reexports: new Map() };
  const add = (map, name, nodes) => map.set(name, [...(map.get(name) ?? []), ...nodes]);
  const declare = (name, init) => {
    const inner = unwrap(init);
    if (FUNC_EXPR(inner)) add(mod.fns, name, returns(inner));
    else if (ts.isObjectLiteralExpression(inner)) {
      if (!mod.objects.has(name)) mod.objects.set(name, new Map());
      const props = mod.objects.get(name);
      for (const p of inner.properties)
        if (ts.isPropertyAssignment(p) && (ts.isIdentifier(p.name) || ts.isStringLiteral(p.name)))
          add(props, p.name.text, FUNC_EXPR(unwrap(p.initializer)) ? returns(unwrap(p.initializer)) : [p.initializer]);
        else if (ts.isShorthandPropertyAssignment(p)) add(props, p.name.text, [p.name]);
    } else if (CLASS_EXPR(inner)) add(mod.values, name, [inner]);
  };
  const visit = node => {
    if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer) declare(node.name.text, node.initializer);
    else if (ts.isFunctionDeclaration(node) && node.body) {
      if (node.name) add(mod.fns, node.name.text, returns(node));
      if (isDefault(node)) add(mod.fns, 'default', returns(node));
    } else if (ts.isExportAssignment(node) && !node.isExportEquals) {
      if (ts.isIdentifier(node.expression)) mod.exportAs.set('default', node.expression.text);
      else declare('default', node.expression);
    } else if (ts.isImportDeclaration(node) && ts.isStringLiteral(node.moduleSpecifier)) {
      const spec = node.moduleSpecifier.text, clause = node.importClause;
      if (clause?.name) mod.imports.set(clause.name.text, { spec, name: 'default' });
      const bindings = clause?.namedBindings;
      if (bindings && ts.isNamespaceImport(bindings)) mod.namespaces.set(bindings.name.text, spec);
      else if (bindings) for (const el of bindings.elements)
        mod.imports.set(el.name.text, { spec, name: (el.propertyName ?? el.name).text });
    } else if (ts.isExportDeclaration(node) && node.exportClause && ts.isNamedExports(node.exportClause))
      for (const el of node.exportClause.elements) {
        const inner = (el.propertyName ?? el.name).text;
        if (node.moduleSpecifier && ts.isStringLiteral(node.moduleSpecifier))
          mod.reexports.set(el.name.text, { spec: node.moduleSpecifier.text, name: inner });
        else mod.exportAs.set(el.name.text, inner);
      }
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return mod;
}

// Модули кешируются на прогон — по функции чтения, а не по пути: тесты с разными фикстурами по одному пути не
// смешиваются. Своё дерево файла уже разобрано lib — его разбор кешируется по нему самому.
const NO_READ = () => null;
const byRead = new WeakMap();
const bySource = new WeakMap();
function moduleAt(path, read) {
  if (!byRead.has(read)) byRead.set(read, new Map());
  const cache = byRead.get(read);
  if (!cache.has(path)) {
    const text = read(path);
    cache.set(path, text == null ? null
      : scan(ts.createSourceFile(path, text, ts.ScriptTarget.Latest, true, /x$/.test(path) ? ts.ScriptKind.TSX : ts.ScriptKind.TS)));
  }
  return cache.get(path);
}
function ownModule(sf) {
  if (!bySource.has(sf)) bySource.set(sf, scan(sf));
  return bySource.get(sf);
}

/** Путь модуля от `src/` по импорту файла `from`, или null — пакет либо файла нет. */
function resolve(spec, from, read) {
  const base = importBase(spec, from);
  return base == null ? null : moduleCandidates(base).find(p => moduleAt(p, read)) ?? null;
}

const isCallee = n => ts.isCallExpression(n.parent) && n.parent.expression === n;
/** Имя, которое объявляют, а не читают: параметр, переменная, свойство объекта, атрибут, `a.имя`. */
const isDeclaredName = n => n.parent && n.parent.name === n && !ts.isShorthandPropertyAssignment(n.parent);

/**
 * Есть ли своя рамка в узле: в его строках и кусках шаблона — или в значениях имён, на которые он ссылается
 * (по файлу и импортам). `seen` — по ключу `модуль#имя`: голое имя (`cls`) совпадает между модулями.
 */
function nodeHasBorder(node, ctx, seen) {
  let found = false;
  const visit = n => {
    if (found || ts.isTypeNode(n)) return;
    if (ts.isStringLiteralLike(n) || ts.isTemplateHead(n) || ts.isTemplateMiddle(n) || ts.isTemplateTail(n))
      found = OWN_BORDER.test(n.text);
    else if (ts.isPropertyAccessExpression(n) && ts.isIdentifier(n.expression))
      found = refHasBorder({ name: n.expression.text, member: n.name.text, called: isCallee(n) }, ctx, seen);
    else if (ts.isIdentifier(n)) found = !isDeclaredName(n) && refHasBorder({ name: n.text, called: isCallee(n) }, ctx, seen);
    else ts.forEachChild(n, visit);
  };
  visit(node);
  return found;
}

/** Ссылка `name` (или `name.member`) в модуле `ctx`: своё значение, функция, свойство объекта или импорт. */
function refHasBorder({ name, member, called }, ctx, seen) {
  const { path, mod } = ctx;
  if (ALLOWED.has(`${path}#${name}`)) return false;
  // Вызов и свойство — свои ключи: имя, увиденное значением, функцией и объектом ещё не раскрыто.
  const key = `${path}#${name}${member ? '.' + member : ''}${called ? '()' : ''}`;
  if (seen.has(key)) return false;
  seen.add(key);
  const local = member ? mod.objects.get(name)?.get(member) ?? []
    : [...(mod.values.get(name) ?? []), ...(called ? mod.fns.get(name) ?? [] : [])];
  if (local.some(n => nodeHasBorder(n, ctx, seen))) return true;
  const ns = member && mod.namespaces.get(name);
  if (ns) return exportHasBorder({ spec: ns, name: member }, { called }, ctx, seen);
  const imported = mod.imports.get(name);
  return !!imported && exportHasBorder(imported, { member, called }, ctx, seen);
}

/** Экспорт `name` модуля `spec` (по импорту из `ctx`): своё объявление, `export { a as name }` или реэкспорт. */
function exportHasBorder({ spec, name }, ref, ctx, seen) {
  const path = resolve(spec, ctx.path, ctx.read);
  if (path == null) return false;
  const mod = moduleAt(path, ctx.read);
  const re = mod.reexports.get(name);
  const next = { path, mod, read: ctx.read };
  if (re) return exportHasBorder(re, ref, next, seen);
  return refHasBorder({ ...ref, name: mod.exportAs.get(name) ?? name }, next, seen);
}

/** Сырое поле со своей рамкой: `<input>` или `<textarea>` разметки с классом рамки — где бы он ни лежал (D3). */
export function isRawField({ tag, attrs, element, sf, file, read = NO_READ }) {
  if (tag !== 'input' && tag !== 'textarea') return false;
  if (tag === 'input' && NOT_A_FIELD.has(literal(attrs.get('type')))) return false;
  const opening = ts.isJsxElement(element) ? element.openingElement : element;
  const cls = opening.attributes.properties.find(p => ts.isJsxAttribute(p) && p.name.getText(sf) === 'className');
  return !!cls?.initializer && nodeHasBorder(cls.initializer, { path: file ?? '', mod: ownModule(sf), read }, new Set());
}

/**
 * Правила полей и выбора для `RULES` (ui-ratchet-lib.mjs) — здесь, рядом с тем, что они считают: счётчик
 * элементов передаётся снаружи, иначе модули импортировали бы друг друга.
 */
export function inputRules(countElements) {
  return [
    {
      id: 'native-radio',
      // Своя радиокнопка ([UI-69]): нативная — стрелка у неё выбирает всегда и вида MD3 нет; рисованная
      // `role="radio"` — каждый раз свой круг и своя клавиатура. Уровень — 2: пункты-галочки SitesFilter и
      // LayoutChooser — пункты меню, их переводит sharedui-md3-13.
      count: text => countElements(text, isNativeRadio),
      instead: 'выбор одного из 2–5 вариантов с подписью-фразой — RadioGroup (shared/ui/RadioGroup: items, value, onChange, label — видимое имя группы, orientation, confirm — выбор ведёт дальше); варианты в 1–2 слова — SegmentedButton (docs/UI.md, таблица контролов)',
    },
    {
      id: 'raw-field',
      // Сырое поле со своей рамкой ([UI-69]): высота, обводка, фокус и ошибка у каждого свои, и вид поля
      // MD3 обходит их стороной. Уровень — ноль (sharedui-md3-17, -19). Модули других файлов читает `read`
      // (путь от src/ → текст или null) — его передаёт ui-ratchet.mjs: lib файловой системы не знает.
      count: (text, file, read) => countElements(text, el => isRawField({ ...el, file, read })),
      instead: 'поле с рамкой — TextField / TextAreaField (shared/ui: поле формы 56) или density="dense" (плотное 40: строка настроек, панель, отбор); поле в клетке таблицы — без рамки, её рисует сетка (docs/UI.md, «Плотность»)',
    },
  ];
}
