// Свои кнопки для храповика интерфейса — правила `icon-button` (#1417), `button-size` и `dense-scope`
// (sharedui-md3-10), `split-button` ([UI-83]) и то, что они считают: элемент — из `countElements`
// (ui-ratchet-lib.mjs, передаётся в `buttonRules`). Отдельно от него по той же причине, что
// ui-ratchet-pills.mjs: там разбор дерева и общий перечень правил, здесь — что считать своей кнопкой.
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
 * Только `Button` и `IconButton` под любым именем импорта (`as B`) — без обёрток `…Button`: у тех размер
 * свой. Ответ — для `countElements` (опция `components`).
 */
export function importedButtons(sf) {
  const names = new Set();
  for (const st of sf.statements) {
    const named = ts.isImportDeclaration(st) && BUTTON_MODULE.test(st.moduleSpecifier.text) && st.importClause?.namedBindings;
    if (named && ts.isNamedImports(named))
      for (const el of named.elements)
        if (['Button', 'IconButton'].includes((el.propertyName ?? el.name).text)) names.add(el.name.text);
  }
  return tag => names.has(tag);
}

/** Какие компоненты файла — кнопки: `importedButtons` и всё, что зовётся `…Button`. */
export function buttonComponents(sf) {
  const imported = importedButtons(sf);
  return tag => imported(tag) || BUTTON_NAME.test(tag);
}

/** Класс размера: `h-11`, `min-h-11`, `w-9`, `size-8`, с вариантом (`max-md:h-11`). `…-0` (позволить сжатие),
 *  `max-w-*` и `max-h-*` (обрезка) не меняют размер кнопки и не считаются. */
const SIZE_CLASS = /(?<![\w-])(?:[\w-]+:)*(?:min-)?(?:h|w|size)-(?!0(?![\w.]))[\w./[\]-]+/;

/** Размер в `className` у `Button` и `IconButton` — правило `button-size` ([UI-68]): тип закрыт, класс открыт. */
export function isSizedButton({ tag, cls }) {
  return /^[A-Z]/.test(tag) && SIZE_CLASS.test(cls);
}

/**
 * Своя половина split-кнопки: кнопка с односторонним скруглением — разметки (`button`, `a`) или
 * компонент, который `countElements` пропустил сюда как кнопку (`buttonComponents`).
 */
export function isSplitHalf({ tag, cls }) {
  return (tag === 'button' || tag === 'a' || /^[A-Z]/.test(tag)) && ONE_SIDED_RADIUS.test(cls);
}

/**
 * Правила кнопок для `RULES` (ui-ratchet-lib.mjs) — здесь, рядом с тем, что они считают: счётчик
 * элементов передаётся снаружи, иначе модули импортировали бы друг друга.
 */
export function buttonRules(countElements) {
  return [
    {
      id: 'icon-button',
      // Своя кнопка из одной иконки (#1417): нативный <button>, единственный ребёнок которого — компонент
      // (значок: `<Icon icon={…} />`, до перевода — компонент lucide). IconButton даёт ей размер MD3 (40, в
      // клетке таблицы — по месту), имя для скринридера и подсказку. Смотрит и shared/ (sharedui-md3-10):
      // самые плотные самоделки — «три точки» строки, крестик сетки правки — жили в shared/ui, и следующая
      // родилась бы там же. Сам Button.tsx и есть эта кнопка; законны крестик внутри пилюли (Chip.tsx: 40 px
      // раздули бы чип) и стрелка split-кнопки (SplitButton.tsx: половина с односторонним скруглением).
      count: text => countElements(text, isIconButton),
      dirs: ['features', 'shared'],
      skip: ['shared/ui/Button.tsx', 'shared/ui/Chip.tsx', 'shared/ui/SplitButton.tsx'],
      instead: 'кнопка из одной иконки — IconButton (shared/ui/Button: icon — знак, label — имя для скринридера, variant standard|filled|tonal|outlined, selected — переключатель, danger); размер не задаётся — 40, в клетке таблицы его ставит сетка; с подписью — Button variant="text"; крестик снятия в пилюле — Chip onRemove (removeLabel), у жетона значения без действия — Chip removeOnly (docs/UI.md, таблица контролов)',
    },
    {
      id: 'button-size',
      // Размер в className у Button и IconButton ([UI-68], sharedui-md3-10): свойство size снято, и тип
      // «маленькую кнопку» не пропустит — а класс пропустил бы мимо типа. Высота кнопки одна (40),
      // кнопки-иконки в клетке таблицы — по переменной места (dataTable.ts), цель касания — touch-target.
      // Ширина во всю строку — свойство fullWidth. `…-0`, max-w-*, max-h-* размера не меняют.
      count: text => countElements(text, isSizedButton, { components: importedButtons }),
      dirs: ['features', 'shared'],
      instead: 'размер кнопки не задаётся: высота одна — 40 (docs/UI.md, таблица контролов); во всю ширину — fullWidth; цель касания 48 даёт сама кнопка (touch-target); кнопка-иконка в клетке таблицы — по месту (DataGrid, EditGrid)',
    },
    {
      id: 'dense-scope',
      // Переменная размера кнопки-иконки ([UI-68], В7 = Б) и класс места ICON_BUTTON_XS: их ставят только
      // места XS — клетка таблицы (dataTable.ts) и кнопка внутри поля (хвост OutlinedField, DateInput,
      // линза реквизита), — а читает только IconButton. Поставленные экраном, они вернули бы «маленькую
      // кнопку» в шапки и панели мимо типа и мимо макетов.
      pattern: /--md-comp-icon-button-|\bICON_BUTTON_XS\b/g,
      everywhere: true,
      skip: ['shared/ui/buttonClasses.ts', 'shared/ui/Button.tsx', 'shared/ui/dataTable.ts', 'shared/ui/OutlinedField.tsx',
        'shared/ui/DateInput.tsx', 'features/document-sets/editor/SourceBindingTrigger.tsx'],
      instead: 'размер кнопки-иконки задаёт место, а не экран: 32 — только в клетках DataGrid и EditGrid (shared/ui/dataTable.ts) и у кнопки внутри поля (хвост поля — trailing у TextField, DateInput, линза привязки), везде прочее — 40; плотной панели нет — понадобится, отдельным change с компонентом Toolbar (docs/UI.md, таблица контролов)',
    },
    {
      id: 'split-button',
      // Своя половина split-кнопки ([UI-83]): одностороннее скругление у кнопки — нативной <button>, ссылки
      // или Button / IconButton с перекрытой формой. Вид такой половины держится на порядке утилит в
      // собранном CSS, и правка кнопки разъединяет пару молча. Плашки и панели (div, span) не считаются.
      count: text => countElements(text, isSplitHalf, { components: buttonComponents }),
      instead: 'действие с вариантами — SplitButton (shared/ui/SplitButton: onClick — основное действие, items — варианты в меню, menuLabel — имя стрелки); без вариантов — обычная Button (docs/UI.md, таблица контролов)',
    },
  ];
}
