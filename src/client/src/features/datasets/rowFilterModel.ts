import type { FilterCondition, FilterNode, FilterOp } from '@/shared/api/types';
import { FILTER_OP_LABELS, FILTER_OPS_NO_VALUE } from '@/shared/api/types';
import type { DataSetColumn } from '@/shared/api/datasetHelpers';

/**
 * Условие отбора источника: какие операторы предложить колонке и в каком виде держать значение
 * (issue #1133).
 *
 * Зачем отдельным модулем. Диалог отбора — единственный редактор дерева условий, и до #1133 он
 * предлагал любой колонке одни и те же двенадцать операторов. У источника на таблице модуля колонки
 * типизированы: «Сумма содержит 1» сервер отвергает, а «срок между…» из интерфейса был недостижим
 * вовсе. Правила здесь чистые — диалог их только рисует, а проверяет тест.
 */

/** Колонка в диалоге отбора: колонка источника либо вычисляемая (у той вида нет). */
export type FilterColumn = Pick<DataSetColumn, 'name' | 'kind' | 'operators' | 'unavailable'>;

/**
 * Операторы колонки БЕЗ вида (файл, распознавание, вычисляемая колонка): сервер сравнивает по
 * догадке и принимает любой из них. «Не определено» сюда не входит — у такой колонки это то же
 * «пусто» под вторым именем.
 */
export const UNTYPED_OPS: FilterOp[] = [
  'eq', 'neq', 'contains', 'not_contains',
  'starts_with', 'ends_with',
  'gt', 'gte', 'lt', 'lte',
  'between', 'in', 'not_in',
  'is_empty', 'is_not_empty',
];

/** Сколько значений несёт оператор: ни одного, одно, две границы или список. */
export type OpArity = 'none' | 'one' | 'two' | 'list';

export function opArity(op: FilterOp): OpArity {
  if (FILTER_OPS_NO_VALUE.includes(op)) return 'none';
  if (op === 'between') return 'two';
  if (op === 'in' || op === 'not_in') return 'list';
  return 'one';
}

/**
 * Операторы, которые можно предложить колонке. У колонки с видом — ровно те, что прислал сервер: по
 * этому же списку он отбор и проверит.
 */
export function operatorsFor(column: FilterColumn | undefined): FilterOp[] {
  return column?.operators ? (column.operators as FilterOp[]) : UNTYPED_OPS;
}

/** Подпись оператора; оператор, которого клиент ещё не знает, показывается кодом, а не пропадает. */
export function opLabel(op: string): string {
  return FILTER_OP_LABELS[op as FilterOp] ?? op;
}

/**
 * Сменить оператор, переложив значение туда, где его ждёт сервер: одно — в `value`, границы и список
 * — в `values`. Уже введённое не теряется (issue #401): «равно 5» → «между» даёт «от 5».
 */
export function withOperator(cond: FilterCondition, op: FilterOp): FilterCondition {
  const { value, values, ...rest } = cond;
  const entered = [...(values ?? []), ...(value ? [value] : [])];

  switch (opArity(op)) {
    case 'none':
      return { ...rest, op };
    case 'one':
      return { ...rest, op, value: entered[0] ?? '' };
    case 'two':
      return { ...rest, op, values: [entered[0] ?? '', entered[1] ?? ''] };
    case 'list':
      return { ...rest, op, values: entered.filter(v => v !== '') };
  }
}

/**
 * Сменить колонку. Оператор, который новой колонке не подходит, заменяется первым подходящим:
 * условие только что начато заново, и оставить его заведомо негодным было бы хуже.
 */
export function withColumn(cond: FilterCondition, name: string, columns: FilterColumn[]): FilterCondition {
  const allowed = operatorsFor(columns.find(c => c.name === name));
  const next = { ...cond, column: name };
  return allowed.includes(cond.op) || allowed.length === 0 ? next : withOperator(next, allowed[0]);
}

const KIND_NAMES: Record<string, string> = {
  text: 'текст', number: 'число', date: 'дата', boolean: 'да / нет', list: 'перечень',
};

/**
 * Почему сервер это условие не выполнит; null — возражений нет. Говорим ДО сохранения: отказ сервера
 * приходит уже после него, чтением источника, и человек видит его не там, где ошибся.
 *
 * Сохранённое негодное условие здесь не чинится и не прячется: оно показывается как есть и названо.
 */
export function conditionProblem(cond: FilterCondition, columns: FilterColumn[]): string | null {
  if (!cond.column.trim()) return null;
  const column = columns.find(c => c.name === cond.column);

  if (column?.unavailable)
    return `колонка пришла без значений — ${column.unavailable}; отбирать по ней нельзя`;

  if (column?.operators && !column.operators.includes(cond.op))
    return `«${opLabel(cond.op)}» к колонке вида «${KIND_NAMES[column.kind ?? ''] ?? column.kind}» не применяется`;

  const arity = opArity(cond.op);
  if (arity === 'list' && (cond.values ?? []).length === 0) return 'список значений пуст';

  // У числа, даты и флага значение обязано разбираться: пустая строка — не число и не дата. У текста
  // и у колонки без вида пустое значение законно — с пустой ячейкой сравнивают намеренно.
  const values = arity === 'one' ? [cond.value ?? ''] : arity === 'none' ? [] : cond.values ?? [];
  const bad = values.find(v => !valueFits(column?.kind, v));
  if (bad !== undefined)
    return bad === '' ? (arity === 'two' ? 'не заданы обе границы' : 'значение не задано')
      : `значение «${bad}» — не ${VALUE_NAMES[column?.kind ?? ''] ?? column?.kind}`;

  return null;
}

const VALUE_NAMES: Record<string, string> = { number: 'число', date: 'дата', boolean: '«true» и не «false»' };

/**
 * Разберёт ли сервер значение у колонки этого вида. Запись та же, что у него: число — цифры, точка и
 * знак (запятая и «1e3» числом не считаются), дата — ГГГГ-ММ-ДД. Поля ввода дают её сами; проверка
 * нужна значению, сохранённому раньше или пришедшему мимо диалога.
 */
export function valueFits(kind: string | undefined, value: string): boolean {
  switch (kind) {
    case 'number': return /^-?[0-9]+(\.[0-9]+)?$/.test(value);
    case 'date': return /^[0-9]{4}-[0-9]{2}-[0-9]{2}$/.test(value);
    case 'boolean': return value === 'true' || value === 'false';
    default: return true;
  }
}

/** Есть ли в дереве условие, которое сервер не выполнит. */
export function hasProblems(node: FilterNode, columns: FilterColumn[]): boolean {
  return node.type === 'condition'
    ? conditionProblem(node, columns) !== null
    : node.children.some(c => hasProblems(c, columns));
}

/**
 * Колонки диалога: колонки источника как есть, затем вычисляемые — без вида. При совпадении имён
 * берётся колонка источника: сервер проверяет условие по ИМЕНИ колонки, и объявленный вид действует
 * на него, чем бы ни была заполнена клетка.
 */
export function filterColumns(source: DataSetColumn[], computedAliases: string[]): FilterColumn[] {
  const known = new Set(source.map(c => c.name));
  return [...source, ...[...new Set(computedAliases)].filter(a => a && !known.has(a)).map(name => ({ name }))];
}
