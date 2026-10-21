import { toISO } from '@/shared/utils/date';

/**
 * «Сегодня» по поясу компании (issue #1463, решение владельца): календарь подсвечивает и открывает
 * день компании, а не браузера. Иначе около полуночи на стыке поясов человек видел бы «сегодня»
 * другим днём, чем тот, которым сервер проведёт оплату и закроет период (`IModuleClock`).
 *
 * <p>Пояс держит модуль, как валюту — `format.ts`: его кладёт `AppRoutes` из ответа о доступе, и
 * спрашивать его может кто угодно, без контекста React. Хранится пояс, а не готовый день: экран,
 * открытый в полночь, переходит на новый день сам.</p>
 *
 * <p>Пояс не известен (ответ ещё не пришёл, сервер старше, имя не по форме) — день браузера: другого
 * осмысленного у клиента нет, и пустым «сегодня» не бывает.</p>
 */
/** Последнее присланное имя — и принятое, и отвергнутое: повтор того же ничего не строит. */
let asked: string | null = null;
let days: Intl.DateTimeFormat | null = null;

/**
 * Сменить пояс компании. Зовёт `AppRoutes` в каждом рендере — то же значение ничего не меняет, и
 * отвергнутое тоже (ревью PR #1548): иначе имя, которого браузер не знает, строило бы форматтер и
 * ловило отказ при каждой перерисовке.
 */
export function setCompanyTimeZone(next: string | null | undefined): void {
  const want = next || null;
  if (want === asked) return;
  asked = want;
  try {
    days = want ? new Intl.DateTimeFormat('en-CA', { timeZone: want, year: 'numeric', month: '2-digit', day: '2-digit' }) : null;
  } catch {
    // Имя, которого `Intl` не знает, роняло бы каждый календарь — остаёмся при дне браузера.
    days = null;
  }
}

/** Сегодня у компании — ISO ГГГГ-ММ-ДД. */
export function companyToday(now: Date = new Date()): string {
  if (days) {
    const parts = days.formatToParts(now);
    const part = (type: string) => parts.find(p => p.type === type)!.value;
    return `${part('year')}-${part('month')}-${part('day')}`;
  }
  return toISO(now.getFullYear(), now.getMonth() + 1, now.getDate());
}
