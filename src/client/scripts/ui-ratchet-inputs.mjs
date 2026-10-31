// Свои поля и радиокнопки для храповика интерфейса — правила `native-radio` и `raw-field` ([UI-69],
// sharedui-md3-11) и то, что они считают: элемент — из `countElements` (ui-ratchet-lib.mjs, передаётся в
// `inputRules`). Отдельно от него по той же причине, что ui-ratchet-buttons.mjs: там разбор дерева и общий
// перечень правил, здесь — что считать своим контролом; иначе lib и его тест переходят порог размера.
import ts from 'typescript';

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

// Класс поля часто живёт константой файла (`const inputCls = 'border …'`) — её текст подставляется по имени.
// Константа другого модуля не видна: общая `FIELD_CLS` (shared/filter) — плотная коробка [UI-69], а не самоделка.
// Константа — строка, шаблон, склейка (`base + ' w-full'`) или условие (`err ? 'border-error' : 'border'`),
// в скобках и с `as const`; имена в её тексте раскрываются дальше. Одно имя в разных областях видимости —
// все его тексты: рамка хоть в одном считается.
const localClasses = new WeakMap();
const CLASS_EXPR = n => ts.isStringLiteralLike(n) || ts.isTemplateExpression(n) || ts.isBinaryExpression(n)
  || ts.isConditionalExpression(n) || ts.isParenthesizedExpression(n) || ts.isAsExpression(n);
function classConstants(sf) {
  if (!localClasses.has(sf)) {
    const map = new Map();
    const visit = node => {
      if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer && CLASS_EXPR(node.initializer))
        map.set(node.name.text, [...(map.get(node.name.text) ?? []), node.initializer.getText(sf)]);
      ts.forEachChild(node, visit);
    };
    visit(sf);
    localClasses.set(sf, map);
  }
  return localClasses.get(sf);
}

/** Есть ли своя рамка в тексте класса — прямо или в константах файла, названных в нём (с раскрытием цепочки). */
function hasOwnBorder(text, consts, seen = new Set()) {
  if (OWN_BORDER.test(text)) return true;
  return [...text.matchAll(/\b[A-Za-z_]\w*\b/g)].some(([id]) => consts.has(id) && !seen.has(id)
    && (seen.add(id), consts.get(id).some(t => hasOwnBorder(t, consts, seen))));
}

/** Сырое поле со своей рамкой: `<input>` или `<textarea>` разметки с классом рамки — строкой или константой файла. */
export function isRawField({ tag, cls, attrs, sf }) {
  if (tag !== 'input' && tag !== 'textarea') return false;
  if (tag === 'input' && NOT_A_FIELD.has(literal(attrs.get('type')))) return false;
  return hasOwnBorder(cls, sf ? classConstants(sf) : new Map());
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
      // MD3 обходит их стороной. Уровень — текущий; ноль — в sharedui-md3-17.
      count: text => countElements(text, isRawField),
      instead: 'поле с рамкой — TextField / TextAreaField (shared/ui: поле формы 56) или density="dense" (плотное 40: строка настроек, панель, отбор); поле в клетке таблицы — без рамки, её рисует сетка (docs/UI.md, «Плотность»)',
    },
  ];
}
