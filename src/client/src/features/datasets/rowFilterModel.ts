import type { FilterCondition, FilterGroup, FilterNode, FilterOp } from '@/shared/api/types';
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
  // Пустые места введённым не считаются: у «между» с одной только верхней границей первое из
  // введённого — она, а не пустое «от».
  const entered = [...(values ?? []), value ?? ''].filter(v => v !== '');

  switch (opArity(op)) {
    case 'none':
      return { ...rest, op };
    case 'one':
      return { ...rest, op, value: entered[0] ?? '' };
    case 'two':
      return { ...rest, op, values: [entered[0] ?? '', entered[1] ?? ''] };
    case 'list':
      return { ...rest, op, values: entered };
  }
}

/** «Значения нет» под двумя именами: «пусто» у текста и колонки без вида, «не определено» — у остальных. */
const PRESENCE_TWIN: Partial<Record<FilterOp, FilterOp>> = {
  is_empty: 'is_null', is_null: 'is_empty', is_not_empty: 'is_not_null', is_not_null: 'is_not_empty',
};

/**
 * Сменить колонку. Оператор, который новой колонке не подходит, заменяется: «пусто» — тем же
 * вопросом под именем новой колонки, прочее — первым подходящим. Условие только что начато заново,
 * и оставить его заведомо негодным было бы хуже.
 *
 * Значение, которое у новой колонки не разбирается («5» у даты), не переносится. Оставь его — поле
 * даты показало бы чужое «5» текстом, и дату пришлось бы набирать руками вместо выбора.
 */
export function withColumn(cond: FilterCondition, name: string, columns: FilterColumn[]): FilterCondition {
  const column = columns.find(c => c.name === name);
  const allowed = operatorsFor(column);
  const twin = PRESENCE_TWIN[cond.op];
  const op = allowed.includes(cond.op) || allowed.length === 0 ? cond.op
    : twin && allowed.includes(twin) ? twin : allowed[0];
  const renamed = { ...cond, column: name };
  const moved = op === cond.op ? renamed : withOperator(renamed, op);

  const fits = (v: string) => valueFits(column?.kind, v);
  if (moved.value !== undefined) return fits(moved.value) ? moved : { ...moved, value: '' };
  if (moved.values === undefined) return moved;
  return {
    ...moved,
    values: opArity(op) === 'two' ? moved.values.map(v => (fits(v) ? v : '')) : moved.values.filter(fits),
  };
}

const KIND_NAMES: Record<string, string> = {
  text: 'текст', number: 'число', date: 'дата', boolean: 'да / нет', list: 'перечень',
};

/**
 * Почему сервер это условие, скорее всего, не выполнит; null — возражений нет. Это ПОДСКАЗКА под
 * условием, а не запрет: решает сервер при сохранении (issue #1137), и его отказ диалог показывает
 * сам. Копия правил здесь нужна, чтобы назвать причину сразу и у самого условия; запирать сохранение
 * ей нельзя — разойдясь с сервером, она заперла бы годный отбор (так было с «пусто» у даты).
 *
 * Сохранённое негодное условие здесь не чинится и не прячется: оно показывается как есть и названо.
 */
export function conditionProblem(cond: FilterCondition, columns: FilterColumn[]): string | null {
  if (!cond.column.trim()) return null;
  const column = columns.find(c => c.name === cond.column);

  if (column?.unavailable)
    return `колонка пришла без значений — ${column.unavailable}; отбирать по ней нельзя`;

  // «Пусто» и «не определено» сервер принимает у колонки любого вида, под обоими именами: отборы,
  // сохранённые до #1090, спрашивали «пусто» у числа и даты, и они исполняются.
  if (column?.operators && opArity(cond.op) !== 'none' && !column.operators.includes(cond.op))
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
 * знак (запятая и «1e3» числом не считаются), дата — ГГГГ-ММ-ДД и существующая: «2026-02-30» сервер
 * датой не считает. Поля ввода дают такую запись сами; проверка нужна значению, сохранённому раньше
 * или пришедшему мимо диалога.
 */
export function valueFits(kind: string | undefined, value: string): boolean {
  switch (kind) {
    case 'number': return /^-?[0-9]+(\.[0-9]+)?$/.test(value);
    case 'date': return isDate(value);
    case 'boolean': return value === 'true' || value === 'false';
    default: return true;
  }
}

function isDate(value: string): boolean {
  const parts = /^([0-9]{4})-([0-9]{2})-([0-9]{2})$/.exec(value);
  if (!parts) return false;
  const [year, month, day] = [Number(parts[1]), Number(parts[2]), Number(parts[3])];
  // Через setUTCFullYear, а не конструктор: тот читает годы 0–99 как 1900-е.
  const date = new Date(0);
  date.setUTCFullYear(year, month - 1, day);
  return year >= 1 && date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
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

// ─── Черновик диалога ─────────────────────────────────────────────────────────

/**
 * Узел дерева, пока он открыт в диалоге: тот же узел плюс ключ строки. Без ключа строки условий
 * различались бы порядковым номером, и при удалении условия следующее заняло бы его место вместе с
 * тем, что строка помнит сама, — недобранным значением списка и видом поля.
 */
export type DraftCondition = FilterCondition & { key: number };
export type DraftGroup = Omit<FilterGroup, 'children'> & { key: number; children: DraftNode[] };
export type DraftNode = DraftCondition | DraftGroup;

let lastKey = 0;
const nextKey = () => ++lastKey;

export function newCondition(): DraftCondition {
  return { type: 'condition', column: '', op: 'eq', value: '', key: nextKey() };
}

export function newGroup(children: DraftNode[] = []): DraftGroup {
  return { type: 'group', logic: 'and', children, key: nextKey() };
}

/**
 * Сохранённое дерево — в черновик. Заодно приводится то, что сервер читает по умолчанию, а диалог
 * иначе не показал бы: условие без оператора — это «равно». Условие без колонки сервер отвергает;
 * здесь оно становится условием с невыбранной колонкой — его видно, и его есть чем исправить
 * (сохранение условия без колонки выбрасывает). Сюда приходят именно по отказу сервера, поэтому
 * упасть на таком дереве диалогу нельзя.
 */
export function toDraft(node: FilterNode): DraftNode {
  if (node.type === 'group')
    return { ...node, key: nextKey(), children: (Array.isArray(node.children) ? node.children : []).map(toDraft) };
  return { ...node, column: typeof node.column === 'string' ? node.column : '', op: node.op ?? 'eq', key: nextKey() };
}

/** Черновик — обратно в дерево, которое уходит на сервер: без ключей строк. */
export function fromDraft(node: DraftNode): FilterNode {
  if (node.type === 'group') {
    const { key, ...group } = node;
    return { ...group, children: node.children.map(fromDraft) };
  }
  const { key, ...condition } = node;
  return condition;
}
