import type { FilterCondition, FilterGroup, FilterNode } from '@/shared/api/types';
import { formatDatePrecise } from '@/shared/format/format';
import {
  columnLabel, conditionProblem, opArity, operatorsFor, opLabel, valueFits, withColumn, withOperator,
  type FilterColumn,
} from './rowFilterModel';

/**
 * Чипы отбора — второе лицо того же дерева условий (ТЗ CORE-33; задача G1d, issue #1091).
 *
 * Второго языка отбора здесь нет: чип — это условие дерева, показанное одной фразой. Чипами
 * показывается только ПЛОСКИЙ отбор — условия, связанные «И»: так читается ряд чипов, и другого
 * смысла у ряда нет. Дерево с «ИЛИ» или вложенными группами чипами не подменяется: оно названо
 * «сложным» и правится в расширенном режиме. Скрытых условий не бывает — либо каждое видно чипом,
 * либо видно, что отбор сложный.
 *
 * Правила здесь чистые: экран их только рисует, а проверяет тест.
 */

export type ChipsView =
  /** Плоский отбор: каждое условие — чип. Пустой список — отбора нет. */
  | { mode: 'chips'; conditions: FilterCondition[] }
  /** Дерево, которое рядом чипов не прочитать; `count` — сколько в нём условий. */
  | { mode: 'complex'; count: number };

export function chipsView(filter: FilterNode | null | undefined): ChipsView {
  if (!filter) return { mode: 'chips', conditions: [] };
  if (filter.type === 'condition') return { mode: 'chips', conditions: [filter] };

  const children = Array.isArray(filter.children) ? filter.children : [];
  const flat = children.every(c => c.type === 'condition');
  // Логика группы из одного условия ни на что не влияет: «ИЛИ» от одного — то же условие.
  if (flat && (filter.logic === 'and' || children.length <= 1))
    return { mode: 'chips', conditions: children as FilterCondition[] };
  return { mode: 'complex', count: countConditions(filter) };
}

function countConditions(node: FilterNode): number {
  if (node.type === 'condition') return 1;
  return (Array.isArray(node.children) ? node.children : []).reduce((n, c) => n + countConditions(c), 0);
}

/** Ряд чипов — обратно в дерево, которое уходит на сервер. Чипов нет — отбора нет. */
export function fromChips(conditions: FilterCondition[]): FilterGroup | null {
  return conditions.length === 0 ? null : { type: 'group', logic: 'and', children: conditions };
}

/** Снять чип. Меняется сам отбор, а не его вид: условие уходит из дерева. */
export function withoutChip(conditions: FilterCondition[], index: number): FilterGroup | null {
  return fromChips(conditions.filter((_, i) => i !== index));
}

/**
 * Какие из предложенных мест показать (G4, issue #1097). Колонка, по которой условие уже стоит,
 * местом не предлагается: рядом с чипом «Дата счёта: 01.09 — 30.09» место «+ Дата счёта» читалось бы
 * как «период ещё не задан» — и второй период по нему сузил бы отбор до пересечения. Колонки, которой
 * у таблицы нет, тоже: места без колонки не открыть. И колонки, закрытой правом: условие по ней
 * сервер не применит, и место вело бы прямо в отказ.
 */
export function offeredColumns(suggested: string[] | undefined, columns: FilterColumn[], conditions: FilterCondition[]): string[] {
  return (suggested ?? []).filter(name => columns.some(c => c.name === name && !c.unavailable)
    && !conditions.some(c => c.column === name));
}

/**
 * Введено ли в условие хоть что-то (G4, issue #1097; ревью PR #1177). Предложенное место — ещё не
 * условие, и отбором оно становится со ЗНАЧЕНИЕМ: колонка у места уже названа, и пустое «Добавить»
 * у даты уходило бы в отказ сервера на месте таблицы, а у поставщика молча ставило бы «поставщик
 * пуст». Кому нужен именно пустой поставщик, выбирает оператор «пусто» — ему значение не нужно.
 *
 * Это не проверка годности: «между» с одной границей сюда проходит, и что с ним не так, скажет
 * подсказка под условием и сервер.
 */
export function conditionEntered(cond: FilterCondition): boolean {
  switch (opArity(cond.op)) {
    case 'none': return true;
    case 'one': return (cond.value ?? '') !== '';
    default: return (cond.values ?? []).some(v => v !== '');
  }
}

/**
 * Условие для места, которое отбор предлагает готовым (G4, issue #1097): колонка названа, значение
 * пусто. У даты — сразу «между»: место под дату в реестре называется «период», и «равно» на нём
 * пришлось бы каждый раз менять руками.
 */
export function offeredCondition(name: string, columns: FilterColumn[]): FilterCondition {
  const start = withColumn({ type: 'condition', column: '', op: 'eq', value: '' }, name, columns);
  const column = columns.find(c => c.name === name);
  return column?.kind === 'date' && operatorsFor(column).includes('between') ? withOperator(start, 'between') : start;
}

/** Поставить чип: новый — в конец, правка — на своё место. */
export function withChip(conditions: FilterCondition[], cond: FilterCondition, index?: number): FilterGroup | null {
  return fromChips(index === undefined ? [...conditions, cond] : conditions.map((c, i) => (i === index ? cond : c)));
}

/**
 * Правка отбора — изменением, а не готовым деревом: из отбора, который стоит СЕЙЧАС, — какой должен
 * стоять. Ряд чипов нарисован по отбору прошлой отрисовки, а к моменту щелчка отбор может быть уже
 * другим (экран таблицы: адрес меняется сразу, перерисовка приходит позже). Дерево, собранное из
 * нарисованного ряда, возвращало бы только что снятый чип: два крестика подряд — и первый чип снова
 * на месте.
 *
 * Чип при этом адресуется САМИМ УСЛОВИЕМ, а не местом в ряду: место после снятия соседа уже другое.
 * Условия, по которому щёлкнули, в отборе больше нет (либо отбор стал сложным) — менять нечего, и
 * отбор возвращается ТЕМ ЖЕ объектом: по этому вызывающий отличает «не изменилось».
 */
export type FilterChange = (current: FilterNode | null) => FilterNode | null;

export function chipAdded(cond: FilterCondition): FilterChange {
  return current => onChips(current, conditions => withChip(conditions, cond));
}

export function chipReplaced(shown: FilterCondition, cond: FilterCondition): FilterChange {
  return current => onChips(current, conditions => {
    const index = indexOfChip(conditions, shown);
    return index < 0 ? current : withChip(conditions, cond, index);
  });
}

export function chipRemoved(shown: FilterCondition): FilterChange {
  return current => onChips(current, conditions => {
    const index = indexOfChip(conditions, shown);
    return index < 0 ? current : withoutChip(conditions, index);
  });
}

function onChips(
  current: FilterNode | null, change: (conditions: FilterCondition[]) => FilterNode | null,
): FilterNode | null {
  const view = chipsView(current);
  return view.mode === 'chips' ? change(view.conditions) : current;
}

function indexOfChip(conditions: FilterCondition[], shown: FilterCondition): number {
  const text = JSON.stringify(shown);
  return conditions.findIndex(c => JSON.stringify(c) === text);
}

// ─── Текст чипа ───────────────────────────────────────────────────────────────

const SIGNS: Partial<Record<string, string>> = { neq: '≠', gt: '>', gte: '≥', lt: '<', lte: '≤' };
const WORDS: Partial<Record<string, string>> = {
  contains: 'содержит', not_contains: 'не содержит', starts_with: 'начинается с', ends_with: 'заканчивается на',
};
const PRESENCE: Partial<Record<string, string>> = {
  is_empty: 'пусто', is_not_empty: 'не пусто', is_null: 'не определено', is_not_null: 'определено',
};

/** Сколько значений списка назвать в чипе, прежде чем сказать «и ещё N». */
const LIST_SHOWN = 3;

/**
 * Чип одной фразой: колонка, оператор и значение — все три (ТЗ CORE-33: «каждое сужение названо»).
 * «Оплатить до: 01.10.2026 — 31.10.2026», «Состояние оплаты: Не оплачен, Частично оплачен»,
 * «Осталось дней < 0», «Оплатить до: не определено».
 *
 * Значение показано так, как его читает человек: дата — днём, флаг — «да / нет». Значение, которое
 * у колонки не разбирается, показано как есть — чип не приукрашивает негодное условие.
 */
export function chipText(cond: FilterCondition, columns: FilterColumn[]): string {
  const column = columns.find(c => c.name === cond.column);
  const name = columnLabel(column, cond.column);
  const show = (v: string) => shownValue(v, column);

  const presence = PRESENCE[cond.op];
  if (presence) return `${name}: ${presence}`;

  switch (opArity(cond.op)) {
    case 'two': {
      const [from = '', to = ''] = cond.values ?? [];
      return `${name}: ${show(from)} — ${show(to)}`;
    }
    case 'list': {
      const values = cond.values ?? [];
      const shown = values.slice(0, LIST_SHOWN).map(show).join(', ');
      const list = values.length === 0 ? '…'
        : values.length > LIST_SHOWN ? `${shown} и ещё ${values.length - LIST_SHOWN}` : shown;
      return cond.op === 'not_in' ? `${name} — кроме: ${list}` : `${name}: ${list}`;
    }
    default: {
      const value = show(cond.value ?? '');
      if (cond.op === 'eq') return `${name}: ${value}`;
      const sign = SIGNS[cond.op];
      if (sign) return `${name} ${sign} ${value}`;
      // Оператор, которого клиент не знает, показан своей подписью (или кодом), а не пропадает.
      return `${name} ${WORDS[cond.op] ?? opLabel(cond.op)} ${value}`;
    }
  }
}

function shownValue(value: string, column: FilterColumn | undefined): string {
  const kind = column?.kind;
  if (value === '') return kind === undefined || kind === 'text' || kind === 'list' ? '«»' : '…';
  if (!valueFits(kind, value, column?.options)) return `«${value}»`;
  switch (kind) {
    case 'date': return formatDatePrecise(value);
    case 'boolean': return value === 'true' ? 'да' : 'нет';
    case 'number':
    case 'choice': return value;
    default: return `«${value}»`;
  }
}

/**
 * Почему условие чипа сервер не выполнит; null — возражений нет. Битый отбор виден чипом с причиной,
 * а не молчанием (ТЗ CORE-33): причина стоит у того условия, которое негодно.
 *
 * Сверх общих правил — колонка, которой в таблице нет вовсе (поле удалили из типа, а сохранённое
 * условие осталось). Колонки таблицы известны целиком, поэтому «нет в списке» здесь — утверждение, а
 * не догадка. Пока колонки не пришли (`columns` пуст), о колонке сказать нечего.
 */
export function chipProblem(cond: FilterCondition, columns: FilterColumn[]): string | null {
  if (columns.length > 0 && cond.column.trim() && !columns.some(c => c.name === cond.column))
    return 'такой колонки в таблице нет — поле удалено из типа или переименовано';
  return conditionProblem(cond, columns);
}
