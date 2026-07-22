import type { DatePrecision } from '@/shared/api/types';

/** Converts ISO date (YYYY-MM-DD) → Russian display format (DD.MM.YYYY). Passes through anything else. */
export function isoToRu(iso: string | null | undefined): string {
  if (!iso) return '';
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  return m ? `${m[3]}.${m[2]}.${m[1]}` : iso;
}

/**
 * Форматирует ISO-дату для показа человеку с учётом точности типа (issue #60):
 * 'year' → «2026», 'month' → «07.2026», 'day'/по умолчанию → «01.07.2026».
 * Хранение всегда полный ISO; здесь лишь скрываем допоненные части. Непонятный вход — как есть.
 */
export function formatDateRu(iso: string | null | undefined, precision: DatePrecision = 'day'): string {
  if (!iso) return '';
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  if (!m) return iso;
  const [, y, mo, d] = m;
  if (precision === 'year') return y;
  if (precision === 'month') return `${mo}.${y}`;
  return `${d}.${mo}.${y}`;
}

/** Converts Russian format (DD.MM.YYYY) → ISO (YYYY-MM-DD). Passes through incomplete/other strings. */
export function ruToISO(ru: string): string {
  const m = /^(\d{2})\.(\d{2})\.(\d{4})$/.exec(ru.trim());
  return m ? `${m[3]}-${m[2]}-${m[1]}` : ru;
}

// ─── RU-локаль календаря (issue #338) ─────────────────────────────────────────
/** Названия месяцев (именительный) — для заголовка/сетки месяцев. */
export const MONTHS_RU = [
  'Январь', 'Февраль', 'Март', 'Апрель', 'Май', 'Июнь',
  'Июль', 'Август', 'Сентябрь', 'Октябрь', 'Ноябрь', 'Декабрь',
];
/** Короткие названия месяцев — для компактной сетки 3×4. */
export const MONTHS_RU_SHORT = [
  'Янв', 'Фев', 'Мар', 'Апр', 'Май', 'Июн', 'Июл', 'Авг', 'Сен', 'Окт', 'Ноя', 'Дек',
];
/** Заголовки дней недели, неделя с ПОНЕДЕЛЬНИКА. */
export const WEEKDAYS_RU = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];

/** Разбирает полный ISO (YYYY-MM-DD) в {y,m,d} (m — 1..12) или null. */
export function parseISO(iso: string | null | undefined): { y: number; m: number; d: number } | null {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso ?? '');
  return m ? { y: +m[1], m: +m[2], d: +m[3] } : null;
}

/** Собирает ISO YYYY-MM-DD из чисел (m — 1..12), с нулевым паддингом. */
export function toISO(y: number, m: number, d: number): string {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(y)}-${p(m)}-${p(d)}`;
}

/** Число дней в месяце (m — 1..12). */
export function daysInMonth(y: number, m: number): number {
  return new Date(y, m, 0).getDate();
}

/**
 * Матрица недель месяца (Monday-first) для сетки дней. Каждая ячейка — число дня месяца или null
 * (пустышка добивки до полной недели). m — 1..12.
 */
export function monthMatrix(y: number, m: number): (number | null)[][] {
  const total = daysInMonth(y, m);
  // getDay(): 0=Вс..6=Сб → сдвигаем к Monday-first (Пн=0..Вс=6).
  const firstDow = (new Date(y, m - 1, 1).getDay() + 6) % 7;
  const cells: (number | null)[] = Array(firstDow).fill(null);
  for (let d = 1; d <= total; d++) cells.push(d);
  while (cells.length % 7 !== 0) cells.push(null);
  const weeks: (number | null)[][] = [];
  for (let i = 0; i < cells.length; i += 7) weeks.push(cells.slice(i, i + 7));
  return weeks;
}
