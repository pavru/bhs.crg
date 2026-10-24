import type { FilterCondition, FilterOp } from '@/shared/api/types';
import { listMatches, softTwin, textMatches, upper } from './conditionMatch';
import {
  conditionProblem, opArity, withOperator, type ColumnKnown, type FilterColumn, type KnownExplain,
} from './rowFilterModel';

/**
 * Пометка «отбор выполнится, но не найдёт» (UI-56, D5 change `costs-filter-place-hints`) — отдельно от
 * проблемы условия (`conditionProblem`, «сервер не выполнит»): другой смысл, другой тон (`warning`, а не
 * `danger`) и не тревога для читалки. Сохранение пометка не запирает.
 *
 * <p>Все случаи молчания — здесь, а не у тех, кто рисует: у колонки нет перечня или он известен не
 * целиком, у условия есть проблема (сначала то, что сервер отвергнет), значение пустое, оператор
 * «пусто» / «не пусто». Рисуют пометку поле значения (жетоны списка), строка под условием
 * (`ConditionHint`) и чип над таблицей.</p>
 */

/** Значение условия, которого в перечне нет, — и чем его поправить, если это видно. */
export interface NoticeMiss {
  value: string;
  /** Единственное мягко совпавшее название перечня — «Заменить на «…»». */
  replace?: string;
  /** Известное «не значение» колонки — пояснение с действиями вместо замены. */
  explain?: KnownExplain;
}

export type ConditionNotice =
  /** «Равно», «не равно», «из списка», «не из списка»: помечено каждое значение вне перечня. */
  | { kind: 'values'; misses: NoticeMiss[] }
  /** «Содержит»-семья: под условие не подходит ни одно название перечня. */
  | { kind: 'pattern' };

const VALUE_OPS: FilterOp[] = ['eq', 'neq', 'in', 'not_in'];
const PATTERN_OPS: Partial<Record<FilterOp, FilterOp>> = {
  contains: 'contains', not_contains: 'contains', starts_with: 'starts_with', ends_with: 'ends_with',
};

/** Колонка условия; имя берётся из условия — полю значения его не передают. */
type NoticeColumn = Omit<FilterColumn, 'name'>;

/** Названия перечня, поднятые в верхний регистр, — один раз на перечень, а не на каждую отрисовку. */
interface Names { list: string[]; upper: string[]; upperSet: ReadonlySet<string> }

const NAMES = new WeakMap<ColumnKnown, Names>();

/**
 * Перечень собирается заново только при смене справочника (`tableKnown`), а пометку спрашивают поле
 * значения, строка под условием и чип — на каждую клавишу. Справочник — сотни строек с разделами.
 */
function namesOf(known: ColumnKnown): Names {
  let names = NAMES.get(known);
  if (!names) {
    const list = known.items.map(i => i.value);
    const uppered = list.map(upper);
    names = { list, upper: uppered, upperSet: new Set(uppered) };
    NAMES.set(known, names);
  }
  return names;
}

export function conditionNotice(cond: FilterCondition, column: NoticeColumn | undefined): ConditionNotice | null {
  const known = column?.known;
  // Правило сверки — правило колонки-перечня (`conditionMatch`); у текста сервер сравнивает в SQL.
  if (!column || !known?.complete || column.kind !== 'list' || !cond.column.trim()) return null;
  if (conditionProblem(cond, [{ ...column, name: cond.column }])) return null;
  const names = namesOf(known);

  if (VALUE_OPS.includes(cond.op)) {
    const values = opArity(cond.op) === 'one' ? [cond.value ?? ''] : cond.values ?? [];
    // «Равно» правила сервера — совпадение строки целиком без регистра (`listMatches('eq')`).
    const misses = values.filter(v => v !== '' && !names.upperSet.has(upper(v))).map((value): NoticeMiss => {
      const explain = known.explain?.find(e => listMatches('eq', [value], [e.value]));
      return explain ? { value, explain } : { value, replace: softTwin(value, names.list) };
    });
    return misses.length > 0 ? { kind: 'values', misses } : null;
  }

  const positive = PATTERN_OPS[cond.op];
  if (!positive || !cond.value) return null;
  const wanted = upper(cond.value);
  return names.upper.some(name => textMatches(positive, name, wanted)) ? null : { kind: 'pattern' };
}

export const NOT_KNOWN = 'нет в справочнике';

/** Что сказано о значении вне перечня. */
export function missText(miss: NoticeMiss): string {
  return miss.explain?.text ?? NOT_KNOWN;
}

/** Пометка одной строкой — подсказкой чипа над таблицей и строкой под условием «содержит»-семьи. */
export function noticeText(notice: ConditionNotice): string {
  return notice.kind === 'pattern' ? 'Ни одно название справочника не подходит'
    : notice.misses.map(m => `«${m.value}» — ${missText(m)}`).join('; ');
}

/** Условие, в котором одно значение заменено другим; прочие значения списка — на месте. */
export function withValueReplaced(cond: FilterCondition, from: string, to: string): FilterCondition {
  if (opArity(cond.op) === 'one') return { ...cond, value: to };
  const values = cond.values ?? [];
  return { ...cond, values: values.includes(to) ? values.filter(v => v !== from) : values.map(v => (v === from ? to : v)) };
}

/**
 * Действия пометки значения — подписью и готовым условием. Замена на похожее меняет одно это значение.
 * Действие пояснения со своим оператором («начинается с») годится только условию с одним значением:
 * в списке и в отрицании оператор сменить нельзя, не переписав чужие значения, — там остаются действия,
 * которые меняют только значение.
 */
export function missActions(cond: FilterCondition, miss: NoticeMiss): { label: string; next: FilterCondition }[] {
  if (miss.replace) return [{ label: `Заменить на «${miss.replace}»`, next: withValueReplaced(cond, miss.value, miss.replace) }];
  return (miss.explain?.actions ?? []).flatMap(action => {
    if (cond.op === 'eq') return [{ label: action.label, next: { ...withOperator(cond, action.op), value: action.value } }];
    return action.op === 'eq' ? [{ label: action.label, next: withValueReplaced(cond, miss.value, action.value) }] : [];
  });
}
