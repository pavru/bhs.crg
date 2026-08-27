// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

/** Заголовок, который можно писать в выражении как есть (кириллица в JS-идентификаторах допустима). */
const JS_IDENTIFIER = /^[A-Za-zА-Яа-яЁё_$][A-Za-zА-Яа-яЁё0-9_$]*$/;

/**
 * Слова, которые выглядят идентификаторами, но переменной быть не могут. Ключевые слова ломают
 * РАЗБОР выражения (колонка «for» → «Unexpected end of input» → вся вычисляемая колонка молча
 * пустеет), литералы `null`/`true` дают сами себя вместо ячейки, а `undefined`/`NaN`/`Infinity` —
 * глобальные свойства, переопределять которые незачем.
 */
const RESERVED = new Set([
  'break', 'case', 'catch', 'class', 'const', 'continue', 'debugger', 'default', 'delete', 'do',
  'else', 'enum', 'export', 'extends', 'false', 'finally', 'for', 'function', 'if', 'import', 'in',
  'instanceof', 'new', 'null', 'return', 'super', 'switch', 'this', 'throw', 'true', 'try', 'typeof',
  'var', 'void', 'while', 'with', 'yield', 'let', 'static', 'await',
  'undefined', 'NaN', 'Infinity',
]);

/**
 * Чем обратиться к колонке из выражения (issue #539). Заголовок-идентификатор пишется как есть,
 * остальные — через `get("…")`: заголовок вроде «1» или «Кол-во» переменной не станет, а
 * просанированное имя (`_1`, `Кол_во`) пользователю взяться неоткуда, да ещё и способно
 * столкнуться с именем соседней колонки.
 */
export function columnAccessor(name: string): string {
  return JS_IDENTIFIER.test(name) && !RESERVED.has(name) ? name : `get(${JSON.stringify(name)})`;
}
