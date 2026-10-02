import type { FilterCondition, FilterGroup, FilterNode } from '@/shared/api/types';
import { formatDateRu } from '@/shared/utils/date';
import { columnLabel, conditionProblem, opArity, opLabel, valueFits, type FilterColumn } from './rowFilterModel';

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

/** Поставить чип: новый — в конец, правка — на своё место. */
export function withChip(conditions: FilterCondition[], cond: FilterCondition, index?: number): FilterGroup | null {
  return fromChips(index === undefined ? [...conditions, cond] : conditions.map((c, i) => (i === index ? cond : c)));
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
    case 'date': return formatDateRu(value);
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
