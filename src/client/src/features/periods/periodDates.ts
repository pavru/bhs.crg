/**
 * Даты экрана закрытия периода. Только ПРЕДЛОЖЕНИЯ по умолчанию и показ: что закрыто и с какого дня
 * начинается следующий период, считает сервер.
 */

/** ISO-день → ДД.ММ.ГГГГ. Разбор строкой, а не через Date: тот сдвинул бы день поясом браузера. */
export function ruDate(iso: string): string {
  const [y, m, d] = iso.split('-');
  return `${d}.${m}.${y}`;
}

function iso(y: number, m: number, d: number): string {
  // Date.UTC сам переносит «нулевой день» и «тринадцатый месяц» — этим и считаются края месяца.
  return new Date(Date.UTC(y, m, d)).toISOString().slice(0, 10);
}

function parts(isoDay: string): [number, number, number] {
  const [y, m, d] = isoDay.split('-').map(Number);
  return [y, m - 1, d];
}

/**
 * Какой день предложить концом периода: последний день прошлого месяца, а если он уже закрыт —
 * вчерашний. Пусто — закрывать нечего: вчера уже закрыто.
 */
export function suggestThrough(today: string, closedThrough: string | null): string {
  const [y, m, d] = parts(today);
  const lastMonthEnd = iso(y, m, 0);
  if (!closedThrough || lastMonthEnd > closedThrough) return lastMonthEnd;

  const yesterday = iso(y, m, d - 1);
  return yesterday > closedThrough ? yesterday : '';
}

/**
 * Даты периода названы «наоборот»: конец раньше начала. ISO-дни сравниваются строками.
 *
 * <p>Бывает не только от опечатки: начало следующего периода задаёт граница контура, и она сдвигается
 * под открытым диалогом — своим удавшимся закрытием или чужим. Конец, выбранный раньше, остаётся позади.</p>
 */
export function inverted(from: string, through: string): boolean {
  return !!from && !!through && from > through;
}

/** Начало первого закрытия контура — первый день месяца, которым период заканчивается. */
export function suggestFirstFrom(through: string): string {
  if (!through) return '';
  const [y, m] = parts(through);
  return iso(y, m, 1);
}
