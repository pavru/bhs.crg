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
/** Модуль `Button` и `IconButton`: `@/shared/ui/Button` или относительный путь к нему. */
const BUTTON_MODULE = /^(?:@\/shared\/ui|\.{1,2}(?:\/[\w.-]+)*)\/Button$/;
/** Кнопка по имени: сами `Button`, `IconButton` и обёртки (`PrimaryActionButton`) — форму они отдают `Button`. */
const BUTTON_NAME = /^(?:[A-Z]\w*)?Button$/;

/**
 * Какие компоненты файла — кнопки: `Button` и `IconButton` под любым именем импорта (`as B`) и всё,
 * что зовётся `…Button`. Ответ — для `countElements` (опция `components`).
 */
export function buttonComponents(sf) {
  const names = new Set();
  for (const st of sf.statements) {
    const named = ts.isImportDeclaration(st) && BUTTON_MODULE.test(st.moduleSpecifier.text) && st.importClause?.namedBindings;
    if (named && ts.isNamedImports(named))
      for (const el of named.elements)
        if (['Button', 'IconButton'].includes((el.propertyName ?? el.name).text)) names.add(el.name.text);
  }
  return tag => names.has(tag) || BUTTON_NAME.test(tag);
}

/**
 * Своя половина split-кнопки: кнопка с односторонним скруглением — разметки (`button`, `a`) или
 * компонент, который `countElements` пропустил сюда как кнопку (`buttonComponents`).
 */
export function isSplitHalf({ tag, cls }) {
  return (tag === 'button' || tag === 'a' || /^[A-Z]/.test(tag)) && ONE_SIDED_RADIUS.test(cls);
}
