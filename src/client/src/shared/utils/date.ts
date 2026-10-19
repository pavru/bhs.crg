/** Converts ISO date (YYYY-MM-DD) → Russian display format (DD.MM.YYYY). Passes through anything else. */
export function isoToRu(iso: string | null | undefined): string {
  if (!iso) return '';
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  return m ? `${m[3]}.${m[2]}.${m[1]}` : iso;
}

/** Converts Russian format (DD.MM.YYYY) → ISO (YYYY-MM-DD). Passes through incomplete/other strings. */
export function ruToISO(ru: string): string {
  const m = /^(\d{2})\.(\d{2})\.(\d{4})$/.exec(ru.trim());
  return m ? `${m[3]}-${m[2]}-${m[1]}` : ru;
}

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
 * Номер дня внутри недели языка: 0 — её первый день, 6 — последний. `firstDay` — 1 (понедельник) …
 * 7 (воскресенье), как у `firstDayOfWeek`. Одна формула на сетку месяца и Home/End календаря:
 * разойдись они, Home вёл бы не к первой колонке.
 */
export function dayInWeek(date: Date, firstDay = 1): number {
  // getDay(): 0=Вс..6=Сб; firstDay 7 (воскресенье) — это 0.
  return (date.getDay() - (firstDay % 7) + 7) % 7;
}

/**
 * Матрица недель месяца для сетки дней. Каждая ячейка — число дня месяца или null (пустышка добивки
 * до полной недели). m — 1..12. `firstDay` — первый день недели языка (issue #1461): 1 — понедельник
 * (по умолчанию, ISO 8601) … 7 — воскресенье (en-US); названия дней — `weekdayNames` из shared/format.
 */
export function monthMatrix(y: number, m: number, firstDay = 1): (number | null)[][] {
  const total = daysInMonth(y, m);
  const firstDow = dayInWeek(new Date(y, m - 1, 1), firstDay);
  const cells: (number | null)[] = Array(firstDow).fill(null);
  for (let d = 1; d <= total; d++) cells.push(d);
  while (cells.length % 7 !== 0) cells.push(null);
  const weeks: (number | null)[][] = [];
  for (let i = 0; i < cells.length; i += 7) weeks.push(cells.slice(i, i + 7));
  return weeks;
}
