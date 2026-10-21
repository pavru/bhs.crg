import type { CompanyTimeZoneView } from '@/shared/api/companyTimeZone';
import { formatDay } from '@/shared/format/format';
import type { AccountingCurrencyView } from '@/shared/api/accountingCurrency';

/**
 * Валюта словами: «Казахстанский тенге (KZT)». Название — из списка сервера; кода, которого в
 * списке нет, не бывает (сервер отдаёт действующую валюту только из списка), но показать голый код
 * лучше, чем пустое место, если списки когда-нибудь разойдутся.
 */
export function currencyTitle(view: Pick<AccountingCurrencyView, 'currencies'>, code: string): string {
  const known = view.currencies.find(c => c.code === code);
  return known ? `${known.title} (${known.code})` : code;
}

/**
 * Значение пункта «пояс не выбран». Отдельное слово, а не пустая строка: пустое значение у пункта
 * списка Radix запрещает, а идентификатором пояса оно быть не может.
 */
export const SERVER_ZONE = 'server';

const withOffset = (name: string, offset: string) => `${name} (${offset})`;

/**
 * Пункты списка выбора пояса: «не выбран», пояса от сервера и — если выбранного в списке нет — он
 * сам, как есть. Без последнего список молча показал бы «не выбран» там, где пояс выбран запросом к
 * API, и первое же сохранение его стёрло бы.
 */
export function zoneChoices(view: CompanyTimeZoneView): { value: string; label: string }[] {
  const offered = view.timeZones.map(z => ({ value: z.id, label: withOffset(z.city, z.offset) }));
  const stored = view.timeZoneId;
  const outside = stored && !view.timeZones.some(z => z.id === stored)
    ? [{ value: stored, label: view.resolved ? withOffset(stored, view.effectiveOffset) : `${stored} — неизвестен этой машине` }]
    : [];
  return [
    // Пояс сервера назван в пояснении под полем: в самом пункте он не помещался и обрезался.
    { value: SERVER_ZONE, label: 'Не выбран — как на сервере' },
    ...outside,
    ...offered,
  ];
}

/**
 * Что сказать администратору, когда действует не то, что он, возможно, думает. `null` — пояс выбран
 * и действует: говорить нечего.
 */
export function zoneNotice(view: CompanyTimeZoneView): string | null {
  const server = withOffset(view.serverTimeZoneId, view.serverOffset);
  if (view.timeZoneId === null)
    return `Пояс не выбран — действует пояс сервера: ${server}. Если компания работает в другом поясе, `
      + 'выберите его: иначе новые сутки в учёте наступают не в полночь.';
  if (!view.resolved)
    return `Выбранный пояс «${view.timeZoneId}» этой машине неизвестен — действует пояс сервера: ${server}. `
      + 'Так бывает после переноса базы на другой сервер. Выберите пояс заново.';
  return null;
}

/**
 * По какому поясу снимается плановая копия (issue #1468) — подпись под полем времени. Пояс назван
 * всегда: «03:00» без пояса читается как время сервера, а им копия больше не живёт. Невыбранный и
 * неизвестный машине пояс сказаны прямо — время тогда действительно идёт по серверу.
 */
export function backupZoneHint(view: CompanyTimeZoneView): string {
  const server = withOffset(view.serverTimeZoneId, view.serverOffset);
  if (view.timeZoneId === null) return `Пояс компании не выбран — время по поясу сервера: ${server}`;
  if (!view.resolved) return `Пояс «${view.timeZoneId}» этой машине неизвестен — время по поясу сервера: ${server}`;
  const city = view.timeZones.find(z => z.id === view.effectiveTimeZoneId)?.city ?? view.effectiveTimeZoneId;
  return `По поясу компании: ${withOffset(city, view.effectiveOffset)}`;
}

/**
 * Ближайшая плановая копия словами: «сегодня, 04:00», «завтра, 04:00», «24.10.2026, 04:00»; срок
 * настал — «в течение минуты» (служба проверяет срок раз в минуту). Момент пришёл со смещением пояса,
 * по которому служба считает срок (`BackupScheduleService.NextRunAt`): день и час читаются из записи как
 * есть, «сегодня» — в том же смещении, а не по часам браузера. Нечитаемое — null: строки не будет.
 */
export function nextBackupText(nextRunAt: string, now: number = Date.now()): string | null {
  const m = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})(?::[\d.]+)?(Z|([+-])(\d{2}):(\d{2}))$/.exec(nextRunAt);
  if (!m) return null;
  if (Date.parse(nextRunAt) <= now) return 'в течение минуты';
  const offset = m[3] === 'Z' ? 0 : (m[4] === '-' ? -1 : 1) * (+m[5] * 60 + +m[6]) * 60_000;
  const dayAt = (shift: number) => new Date(now + offset + shift).toISOString().slice(0, 10);
  const day = m[1] === dayAt(0) ? 'сегодня' : m[1] === dayAt(DAY_MS) ? 'завтра' : formatDay(m[1]);
  return `${day}, ${m[2]}`;
}

const DAY_MS = 86_400_000;

/** «2026-10-13T14:32» → «13.10.2026, 14:32». Число и час пришли уже в поясе компании — не пересчитываем. */
export function companyNow(effectiveNow: string): string {
  const [day, time = ''] = effectiveNow.split('T');
  return `${formatDay(day)}, ${time}`;
}
