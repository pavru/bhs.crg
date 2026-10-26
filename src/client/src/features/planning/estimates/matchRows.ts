import type { EstimateRow, RevisionSection } from '@/shared/api/planningEstimates';
import type { MatchMemory } from '@/shared/api/planningMatches';
import { formatCount } from '@/shared/format/format';
import { sectionKey } from './estimateRows';

/**
 * Правила окна сопоставления раздела (change `plan-09-estimate-ui`, макет П1 1г): что ждёт, в каких единицах и
 * сколько станет в плане. Без отрисовки — это то, что проверяет тест.
 *
 * Ждёт сопоставления строка-работа с пометкой сервера `unmatched` (D17): строка-материал вида работы не требует,
 * запомненная и сопоставленная уже стоят на своём.
 */

export const waiting = (row: EstimateRow) => row.kind === 'work' && row.marks.some(m => m.code === 'unmatched');

/** Строки раздела в порядке файла. */
export function sectionRows(rows: readonly EstimateRow[], code: string | null): EstimateRow[] {
  const key = sectionKey(code);
  return rows.filter(r => sectionKey(r.lsrSection?.code) === key);
}

/**
 * Единицы расценок у ждущих строк раздела — чипами над формой (макет 1г): раздел целиком берёт одну единицу,
 * строки других единиц остаются ждать. Порядок — по числу строк, больше первой.
 */
export function waitingUnits(rows: readonly EstimateRow[]): { unit: string | null; rows: number }[] {
  const counts = new Map<string | null, number>();
  for (const row of rows.filter(waiting)) counts.set(row.sourceUnit, (counts.get(row.sourceUnit) ?? 0) + 1);
  return [...counts].map(([unit, n]) => ({ unit, rows: n })).sort((a, b) => b.rows - a.rows);
}

/** Коэффициент, набранный человеком: «100», «2,5», «1 000». Не число или не больше нуля — `null`. */
export function parseFactor(text: string): number | null {
  const normalized = text.replace(/\s/g, '').replace(',', '.');
  if (!/^\d+(\.\d+)?$/.test(normalized)) return null;
  const value = Number(normalized);
  return value > 0 ? value : null;
}

/** Объём в плане: объём расценки × коэффициент. Объёма у строки нет — и в плане его не будет. */
export const planVolume = (row: EstimateRow, factor: number) =>
  (row.sourceVolume === null ? null : row.sourceVolume * factor);

/**
 * Следующий раздел, где ещё ждут, — после текущего в порядке файла («применить и к следующему разделу», П1 из
 * варианта 3). По строкам, а не по счётчику раздела: строки перечитываются вместе со счётчиками, но текущий
 * раздел пропускается и тогда, когда его счётчик ещё не догнал правку.
 */
export function nextWaitingSection(
  sections: readonly RevisionSection[], rows: readonly EstimateRow[], current: string | null,
): RevisionSection | null {
  const at = sections.findIndex(s => sectionKey(s.code) === sectionKey(current));
  return sections.slice(at + 1).find(s => sectionRows(rows, s.code).some(waiting)) ?? null;
}

/** Что запомнилось — словами для уведомления; `failed` назван отдельно: сопоставлено, а запомнить не удалось. */
export function memoryText(memory: MatchMemory): string {
  if (memory.failed) return ' · запомнить не удалось — следующий импорт не подставит сам';
  if (memory.remembered === 0) return '';
  return ` · запомнено${memory.replaced ? `, заменено прежнее: ${formatCount(memory.replaced)}` : ''}`;
}

/** Единица классификатора из единицы расценки: «100 шт» → «шт» — множитель расценки в имени единицы не живёт. */
export const unitWord = (sourceUnit: string | null) => (sourceUnit ?? '').replace(/^[\d\s.,]+/, '').trim();
