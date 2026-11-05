// Свои поля и радиокнопки для храповика интерфейса — правила `native-radio` и `raw-field` ([UI-69],
// sharedui-md3-11) и то, что они считают: элемент — из `countElements` (ui-ratchet-lib.mjs, передаётся в
// `inputRules`). Отдельно от него по той же причине, что ui-ratchet-buttons.mjs: там разбор дерева и общий
// перечень правил, здесь — что считать своим контролом; иначе lib и его тест переходят порог размера.
import ts from 'typescript';
import { posix } from 'node:path';

/** Значение атрибута как текст: `"radio"`, `'radio'`, `{'radio'}`, `{"radio"}` → `radio`. */
const literal = raw => /^\{?\s*(['"`])(.*)\1\s*\}?$/.exec(raw ?? '')?.[2] ?? null;

/** Своя радиокнопка — нативная `<input type="radio">` или элемент разметки с `role="radio"`. */
export function isNativeRadio({ tag, attrs }) {
  return (tag === 'input' && literal(attrs.get('type')) === 'radio') || literal(attrs.get('role')) === 'radio';
}

// Своя рамка — класс border в основном виде или с вариантом (focus:border-primary), кроме снятой
// (border-0, border-none, border-transparent): поле без рамки — клетка таблицы или строка внутри
// оболочки, и рамку ему рисует место. Флажок, радиокнопку, файл и скрытое поле считают свои правила или
// не считает никто.
const OWN_BORDER = /(?<![\w-])(?:[\w-]+:)*border(?![\w-]*(?:-0|-none|-transparent)(?![\w-]))(?=[\s'"`}-]|$)/;
const NOT_A_FIELD = new Set(['checkbox', 'radio', 'file', 'hidden', 'range', 'color']);

/**
 * Объявленные исключения — модуль (путь от `src/`) и имя ЭКСПОРТА, а не текст имени (D4 sharedui-md3-19): иначе
 * прошли бы `import { FIELD_CLS as X }` и своя локальная `const FIELD_CLS = 'border …'`. Снимается вклад только
 * самой константы: `${FIELD_CLS} border-b` остаётся красным.
 */
const ALLOWED = new Map([
  // Общая коробка условия отбора — плотное поле [UI-69] (h-10, rounded-xs, border-outline, фокус кольцом), той
  // же высоты, что плотный Select рядом с ним в строке отбора (#1469), а не самоделка. Перевод отбора на
  // TextField density="dense" — отдельной задачей, не храповиком.
  ['shared/filter/fieldClass.ts#FIELD_CLS', 'плотная коробка условия отбора [UI-69]'],
]);

// Класс поля живёт не только строкой в разметке (D3 sharedui-md3-19, [UI-69]):
// - константой файла (`const inputCls = 'border …'`) — строка, шаблон, склейка, условие, другое имя, в скобках и
//   с `as const`;
// - результатом функции файла — `const cls = f(x)` или `className={f(x)}`: раскрываются `return` и тело стрелки `f`;
// - константой или функцией другого модуля `src/` — по импорту (алиас `@/`, относительный путь, `.ts`/`.tsx`/
//   `index`, именованные реэкспорты `export { X } from`). Пакеты вне `src/` не раскрываются.
// Имена в найденном тексте раскрываются дальше. Одно имя в разных областях видимости — все его тексты: рамка хоть
// в одном считается. Функция раскрывается только вызовом (`f(` в тексте): компонент по имени — не класс.
// Слепые места названы в docs/UI.md: класс, пришедший пропом, и `export *`.
const CLASS_EXPR = n => ts.isStringLiteralLike(n) || ts.isTemplateExpression(n) || ts.isBinaryExpression(n)
  || ts.isConditionalExpression(n) || ts.isParenthesizedExpression(n) || ts.isAsExpression(n) || ts.isCallExpression(n)
  || ts.isIdentifier(n);
const FUNC_EXPR = n => ts.isArrowFunction(n) || ts.isFunctionExpression(n);

/** Тексты, которые функция отдаёт: тело стрелки-выражения или `return` её собственного тела (не вложенных функций). */
function returns(fn, sf) {
  if (!fn.body) return [];
  if (!ts.isBlock(fn.body)) return [fn.body.getText(sf)];
  const out = [];
  const visit = node => {
    if (ts.isReturnStatement(node) && node.expression) out.push(node.expression.getText(sf));
    if (!ts.isFunctionLike(node)) ts.forEachChild(node, visit);
  };
  ts.forEachChild(fn.body, visit);
  return out;
}

/** Разбор модуля: значения и функции по имени, импорты, локальные экспорты под другим именем и реэкспорты. */
function scan(sf) {
  const mod = { values: new Map(), fns: new Map(), imports: new Map(), exportAs: new Map(), reexports: new Map() };
  const add = (map, name, texts) => map.set(name, [...(map.get(name) ?? []), ...texts]);
  const visit = node => {
    if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer) {
      const init = ts.isAsExpression(node.initializer) ? node.initializer.expression : node.initializer;
      if (FUNC_EXPR(init)) add(mod.fns, node.name.text, returns(init, sf));
      else if (CLASS_EXPR(init)) add(mod.values, node.name.text, [node.initializer.getText(sf)]);
    } else if (ts.isFunctionDeclaration(node) && node.name) add(mod.fns, node.name.text, returns(node, sf));
    else if (ts.isImportDeclaration(node) && ts.isStringLiteral(node.moduleSpecifier)) {
      const bindings = node.importClause?.namedBindings;
      if (bindings && ts.isNamedImports(bindings))
        for (const el of bindings.elements)
          mod.imports.set(el.name.text, { spec: node.moduleSpecifier.text, name: (el.propertyName ?? el.name).text });
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

const CANDIDATES = ['', '.ts', '.tsx', '.js', '.jsx', '/index.ts', '/index.tsx', '/index.js'];
/** Путь модуля от `src/` по импорту файла `from`, или null — пакет либо файла нет. */
function resolve(spec, from, read) {
  const base = spec.startsWith('@/') ? spec.slice(2)
    : spec.startsWith('.') ? posix.normalize(posix.join(posix.dirname(from), spec)) : null;
  if (base == null || base.startsWith('..')) return null;
  return CANDIDATES.map(ext => base + ext).find(p => moduleAt(p, read)) ?? null;
}

/**
 * Есть ли своя рамка в тексте класса — прямо или в именах, названных в нём, с раскрытием по файлу и импортам.
 * `seen` — по ключу `модуль#имя`: голое имя (`cls`) совпадает между модулями.
 */
function hasOwnBorder(text, ctx, seen) {
  if (OWN_BORDER.test(text)) return true;
  return [...text.matchAll(/\b[A-Za-z_$][\w$]*\b/g)].some(m =>
    nameHasBorder(m[0], /^\s*\(/.test(text.slice(m.index + m[0].length)), ctx, seen));
}

function nameHasBorder(name, called, { path, mod, read }, seen) {
  // Вызов — свой ключ: имя, увиденное значением, функцией ещё не раскрыто.
  const key = `${path}#${name}${called ? '()' : ''}`;
  if (seen.has(key)) return false;
  seen.add(key);
  const local = [...(mod.values.get(name) ?? []), ...(called ? mod.fns.get(name) ?? [] : [])];
  if (local.some(t => hasOwnBorder(t, { path, mod, read }, seen))) return true;
  const imported = mod.imports.get(name);
  return !!imported && exportHasBorder(imported, called, path, read, seen);
}

/** Экспорт `name` модуля `spec` (по импорту файла `from`): своё объявление, `export { a as name }` или реэкспорт. */
function exportHasBorder({ spec, name }, called, from, read, seen) {
  const path = resolve(spec, from, read);
  if (path == null || ALLOWED.has(`${path}#${name}`)) return false;
  const mod = moduleAt(path, read);
  const re = mod.reexports.get(name);
  if (re) return exportHasBorder(re, called, path, read, seen);
  return nameHasBorder(mod.exportAs.get(name) ?? name, called, { path, mod, read }, seen);
}

/** Сырое поле со своей рамкой: `<input>` или `<textarea>` разметки с классом рамки — где бы он ни лежал (D3). */
export function isRawField({ tag, cls, attrs, sf, file, read = NO_READ }) {
  if (tag !== 'input' && tag !== 'textarea') return false;
  if (tag === 'input' && NOT_A_FIELD.has(literal(attrs.get('type')))) return false;
  if (OWN_BORDER.test(cls)) return true;
  return !!sf && hasOwnBorder(cls, { path: file ?? '', mod: ownModule(sf), read }, new Set());
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
