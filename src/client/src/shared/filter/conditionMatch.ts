import type { FilterOp } from '@/shared/api/types';

/**
 * Правило, которым сервер сверяет названия в колонке-перечне (`TableConditions.Compile`, вид `list`), —
 * копия для пометки «нет в справочнике» (UI-56, D4 change `costs-filter-place-hints`). Пометка обязана
 * говорить ровно то, что сделает сервер: мягче — промолчала бы, а сервер не нашёл бы; строже — обвинила
 * бы годное значение. Поэтому строка целиком, регистр снимается, ё и е различаются, пробелы значимы.
 *
 * <p>Только для колонок вида `list`: их названия сервер сверяет в памяти этим правилом. У текстовой
 * колонки сравнение идёт в SQL (`upper()` базы), и копия здесь за него не отвечает.</p>
 *
 * <p>Согласие с сервером держит общий набор `conditionMatchCases.json`: его считают этот тест и
 * `ConditionMatchCasesTests` против `TableConditions.Matches`. Правка правила на одной стороне краснит
 * отставшую.</p>
 */

/**
 * Регистр — как `ToUpperInvariant()` у сервера. `toUpperCase()` расходится с ним только у символов вроде
 * «ß» и турецкой «ı», которых в названиях строек, разделов и статей не бывает.
 */
export const upper = (text: string) => text.toUpperCase();

const POSITIVE: Partial<Record<FilterOp, FilterOp>> = { neq: 'eq', not_in: 'in', not_contains: 'contains' };

function textMatches(op: FilterOp, item: string, value: string): boolean {
  switch (op) {
    case 'eq': return item === value;
    case 'contains': return item.includes(value);
    case 'starts_with': return item.startsWith(value);
    case 'ends_with': return item.endsWith(value);
    default: return false;
  }
}

/**
 * Подходит ли клетка-перечень (`items`) под условие. Отрицания — «не подошло под положительное», как у
 * сервера: «не равно Ленина 5» у перечня без неё истинно.
 */
export function listMatches(op: FilterOp, values: readonly string[], items: readonly string[]): boolean {
  switch (op) {
    case 'is_empty': case 'is_null': return items.length === 0;
    case 'is_not_empty': case 'is_not_null': return items.length > 0;
    case 'neq': case 'not_in': case 'not_contains': return !listMatches(POSITIVE[op]!, values, items);
    case 'in': return values.some(v => listMatches('eq', [v], items));
  }
  const wanted = upper(values[0] ?? '');
  return items.some(item => textMatches(op, upper(item), wanted));
}

/**
 * Мягкий ключ названия — для предложения замены, а не для пометки (UI-56): ё = е, без пробелов по краям,
 * двойные пробелы как один, без пробелов вокруг «/». Нечёткого поиска нет нарочно: «Комарова36» —
 * не «Комарова 36», пока это не скажет человек.
 */
export function softKey(text: string): string {
  return upper(text).replace(/Ё/g, 'Е').trim().replace(/\s+/g, ' ').replace(/ ?\/ ?/g, '/');
}

/** Единственное название перечня, мягко совпавшее со значением; два и больше — `undefined`: выбирать не нам. */
export function softTwin(value: string, names: readonly string[]): string | undefined {
  const key = softKey(value);
  const found = [...new Set(names.filter(n => softKey(n) === key))];
  return found.length === 1 ? found[0] : undefined;
}
