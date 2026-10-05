import { formatCount } from '@/shared/format/format';
import type { TableTotal } from '@/shared/api/tables';
import { cellText, plural } from './tableCells';
import type { Aggregate, ColumnTotal } from './tableViewState';

/**
 * Итоговая строка таблицы (ТЗ CORE-33; задача G1e, issue #1092). Чистое: что стоит под колонкой,
 * проверяется тестом.
 *
 * Считает итог СЕРВЕР, и по всему отбору, а не по странице, — здесь только выбор, какой итог
 * колонке положен, и слова, которыми он показан.
 */

export const AGGREGATES: Record<Aggregate, { label: string; sign: string }> = {
  sum: { label: 'Сумма', sign: 'Σ' },
  avg: { label: 'Среднее', sign: 'ср.' },
  min: { label: 'Наименьшее', sign: 'мин.' },
  max: { label: 'Наибольшее', sign: 'макс.' },
  count: { label: 'Количество', sign: 'кол-во' },
};

/** Какие итоги бывают у колонки: у числа все, у даты — края и количество, у остального — количество. */
export function aggregatesFor(kind: string): Aggregate[] {
  if (kind === 'number') return ['sum', 'avg', 'min', 'max', 'count'];
  if (kind === 'date') return ['min', 'max', 'count'];
  return ['count'];
}

export interface TotalText {
  /** Сам итог: «Σ 1 234,5». */
  text: string;
  /**
   * Оговорка к итогу — и она не подсказка, а часть ответа: «не учтено 3 значения: не число». Без неё
   * сумма была бы молча меньше.
   */
  note: string | null;
  /**
   * Что итог значит под этим отбором — словами сервера: «доля: Комарова 36; период — по дате счёта,
   * …» (G4, issue #1097). Стоит ПОД ИТОГОМ, а не только в шапке: неправильно читают
   * нижнюю строку, и шапка к этому моменту уже уехала вверх.
   */
  meaning: string | null;
}

/**
 * Что показать под колонкой. null — показать нечего: сервер итога не прислал (колонка закрыта или
 * её нет в таблице — причину говорит сама колонка).
 */
export function totalText(total: TableTotal | undefined, aggregate: Aggregate, kind: string): TotalText | null {
  if (!total) return null;
  const { sign } = AGGREGATES[aggregate];

  if (!aggregatesFor(kind).includes(aggregate))
    return { text: `${sign} —`, note: 'у колонки этого вида такой итог не считается', meaning: null };

  const value = aggregate === 'count' ? total.count
    : aggregate === 'sum' ? total.sum
      : aggregate === 'avg' ? total.average
        : aggregate === 'min' ? total.min : total.max;

  const note = total.skipped > 0
    ? `не учтено ${formatCount(total.skipped)} ${plural(total.skipped, 'значение', 'значения', 'значений')}`
      + `: ${total.skippedReason ?? 'не то, что обещает вид колонки'}`
    : null;

  // Количество — всегда число, даже у даты; остальное показано так же, как клетки колонки.
  const shown = value == null ? '—' : cellText(value, aggregate === 'count' ? 'number' : kind);
  return { text: `${sign} ${shown}`, note, meaning: total.note ?? null };
}

/**
 * Есть ли что поставить в итоговую строку. Выбранный итог — ещё не показанный: по колонке, закрытой
 * правом, сервер итога не считает, и в сетке её нет. У человека без права на суммы «Реестр счетов»
 * ставит итог под две закрытые колонки — и строка без этой проверки рисовалась бы пустой полосой
 * внизу таблицы, которую нечем убрать (ревью PR #1177).
 */
export function hasShownTotals(
  chosen: ColumnTotal[], totals: Record<string, TableTotal> | null | undefined, grid: { key: string }[],
): boolean {
  return chosen.some(t => totals?.[t.column] !== undefined && grid.some(c => c.key === t.column));
}
