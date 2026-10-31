import ts from 'typescript';

// Свои чипы и пометки для храповика интерфейса (правила `chip` и `badge`, #1416): по классам элемента
// разметки — пилюля или форма чипа MD3. Отдельно от ui-ratchet-lib.mjs: форма растёт с эпиком sharedui-md3
// (sharedui-md3-06 добавил 8 px), а разбор синтаксического дерева — там.

// Классы оформления пишутся строкой, шаблоном или выражением — правило читает текст значения
// className целиком. Модификаторы (hover:bg-…) — не основной вид: пилюля без своего фона только
// подсвечивается при наведении и пометкой не выглядит. Форма — пилюля или форма чипа MD3, small 8 px
// (rounded-sm, [UI-66]): самоделка в новой форме не проходит мимо правила, ловившего её пилюлей. Но
// 8 px — и у плашек, панелей, самодельных кнопок и пунктов списка (бывшие rounded-md): форма чипа
// считается у чипа только с рамкой и не во всю ширину, у пометки — только с мелким текстом, полями
// по бокам и без полей выше py-0.5 (замер — design sharedui-md3-06, «Сторожа»).
const PILL = /(?<![\w-])rounded-(?:full|sm)\b/;
const ROUND_PILL = /(?<![\w-])rounded-full\b/;
const OUTLINE = /(?<![\w:/-])border(?![\w-]*(?:-0|-none|-transparent)\b)/;
const FULL_WIDTH = /(?<![\w:-])w-full\b/;
// Мелкий текст — роли MD3 12 и 11 px (sharedui-md3-07, [UI-67]). Прежних ступеней (text-xs, text-caption)
// здесь нет: их переводит md3:migrate (в CI — --check) и не пускает legacy-type.
const SMALL_TEXT = /(?<![\w:-])text-(?:body-small|label-small|label-medium)(?![\w-])/;
const ROOMY = /(?<![\w:-])(?:p|py|pt|pb)-(?:[1-9]|\[)/;
const SIZED = /(?<![\w-])(?:(?:w|h|size)-(?:\d|\[|px\b)|inset-)/;
const SIDE_PADDING = /(?<![\w:-])px-/;
const SURFACE = /(?<![\w:/-])(?:bg-(?!transparent\b)|border(?![\w-]*(?:-0|-none|-transparent)\b))/;
const INTERACTIVE = new Set(['button', 'a', 'label']);
const PLAIN = new Set(['span', 'div', 'p', 'small', 'em', 'strong', 'mark', 'li']);

/** Свой чип: нажимаемая пилюля или форма чипа с рамкой, либо aria-pressed. Элемент — из `countElements` (ui-ratchet-lib.mjs). */
export function isChip({ tag, cls, attrs }) {
  if (!INTERACTIVE.has(tag)) return false;
  if (attrs.has('aria-pressed')) return true;
  if (!PILL.test(cls) || !SIDE_PADDING.test(cls) || SIZED.test(cls)) return false;
  return ROUND_PILL.test(cls) || (OUTLINE.test(cls) && !FULL_WIDTH.test(cls));
}

/** Есть ли в поддереве элемента кнопка разметки — и в `.map` тоже: стрелочная функция — тоже потомок. */
function holdsButton(node, sf) {
  let found = false;
  const visit = n => {
    if (found) return;
    if ((ts.isJsxOpeningElement(n) || ts.isJsxSelfClosingElement(n)) && n.tagName.getText(sf) === 'button') found = true;
    else ts.forEachChild(n, visit);
  };
  ts.forEachChild(node, visit);
  return found;
}

const GROUP_ROLE = /^["'{`]*(?:radio)?group["'}`]*$/;
const ROUNDED = /(?<![\w-])rounded(?:-|(?=[\s'"`}]))/;

/**
 * Свой ряд сегментов ([UI-68]): группа (`role="group"` / `"radiogroup"`) в рамке и со скруглением, а внутри
 * кнопки — так были нарисованы `ThemeToggle` и `VariantPicker`, и мимо `chip` и `tabs` они прошли: у кнопок
 * сегмента нет ни своих полей пилюли, ни ролей вкладок. Элемент — из `countElements` (ui-ratchet-lib.mjs).
 */
export function isSegmentRow({ tag, cls, attrs, element, sf }) {
  if (!PLAIN.has(tag) || !GROUP_ROLE.test(attrs.get('role') ?? '')) return false;
  return OUTLINE.test(cls) && ROUNDED.test(cls) && holdsButton(element, sf);
}

/** Своя пометка: пилюля с фоном или рамкой, либо форма чипа с мелким текстом. */
export function isBadge({ tag, cls }) {
  if (!PLAIN.has(tag) || !PILL.test(cls) || !SURFACE.test(cls) || SIZED.test(cls)) return false;
  return ROUND_PILL.test(cls) || (SMALL_TEXT.test(cls) && SIDE_PADDING.test(cls) && !ROOMY.test(cls));
}
