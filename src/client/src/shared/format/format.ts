import { LOCALE_KEY, SYSTEM_LOCALE, resolveLocale } from '@/shared/hooks/useLocale';

/**
 * Единственное место, где клиент превращает число, сумму и дату в текст для человека (задача N2
 * этапа 2, issue #1103; ТЗ COST-7.2, CORE-25.3).
 *
 * <p>До него настройка «Внешний вид» обещала формат, и ни один экран обещание не держал: тридцать
 * вызовов `toLocaleString('ru-RU')` по месту, три самодельных форматтера дат и четыре — чисел.
 * Сменив язык форматирования, человек видел новый формат в предпросмотре самой настройки — и
 * больше нигде. Прямой вызов `toLocale*` и `Intl.*Format` вне этого каталога роняет перепись
 * (`format.census.test.ts`).</p>
 *
 * <p><b>Язык держит модуль, а не аргумент.</b> Половина потребителей — чистые функции без доступа к
 * контексту React («3 счёта на 412 500,00 ₽» собирается в `closing.ts`, подпись дубликата — в
 * `invoiceFields.ts`); протащить язык параметром значило бы дать каждому вызову возможность его
 * забыть — то есть вернуть то, от чего уходим. Значение сюда кладёт `LocaleProvider`, первый кадр
 * берёт его из зеркала в браузере — того же, каким пользуется сама настройка.</p>
 *
 * <p>⚠️ <b>Валюта учёта пока одна и вписана здесь</b> — рубль. Настройкой модуля она станет в M1
 * (issue #1070), и менять придётся только <see cref="CURRENCY" />: знак валюты нигде больше не
 * пишется.</p>
 */
const CURRENCY = 'RUB';

let stored = readMirror();

function readMirror(): string {
  try { return localStorage.getItem(LOCALE_KEY) ?? SYSTEM_LOCALE; } catch { return SYSTEM_LOCALE; }
}

/** Сменить язык форматирования. Зовёт `LocaleProvider`; тестам — свой язык на время проверки. */
export function setFormatLocale(next: string): void {
  if (next === stored) return;
  stored = next;
  numbers.clear();
  dates.clear();
}

/** Язык, которым форматирует модуль, — уже разрешённый («system» → язык браузера). */
export function formatLocale(): string {
  return resolveLocale(stored);
}

// Построение `Intl`-форматтера дороже самого форматирования на порядок, а таблица зовёт его на
// каждую ячейку — поэтому по одному на набор параметров.
const numbers = new Map<string, Intl.NumberFormat>();
const dates = new Map<string, Intl.DateTimeFormat>();

function numberFormat(key: string, options: Intl.NumberFormatOptions): Intl.NumberFormat {
  let format = numbers.get(key);
  if (!format) numbers.set(key, format = new Intl.NumberFormat(formatLocale(), options));
  return format;
}

function dateFormat(key: string, options: Intl.DateTimeFormatOptions): Intl.DateTimeFormat {
  let format = dates.get(key);
  if (!format) dates.set(key, format = new Intl.DateTimeFormat(formatLocale(), options));
  return format;
}

// ─── Числа ──────────────────────────────────────────────────────────────────────────────────────

/** Сумма со знаком валюты учёта: «412 500,00 ₽». Копейки пишутся всегда — «100 ₽» и «100,00 ₽» в
 * одной колонке читаются как разная точность. */
export function formatMoney(value: number): string {
  return numberFormat('money', {
    style: 'currency', currency: CURRENCY, currencyDisplay: 'narrowSymbol',
  }).format(value);
}

/** Сумма без знака валюты — для колонки, у которой валюта названа в заголовке: «412 500,00». */
export function formatAmount(value: number): string {
  return numberFormat('amount', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value);
}

/**
 * Количество: «1 250», «7,25». Лишних нулей нет, знаков после запятой не больше трёх — столько
 * хранит база, и четвёртый был бы выдумкой округления.
 */
export function formatQuantity(value: number): string {
  return numberFormat('quantity', { maximumFractionDigits: 3 }).format(value);
}

/**
 * Из чего язык складывает число — для разбора набранного (`parseNumber`): какой знак у него
 * десятичный и чем он делит разряды. Язык здесь называют явно: разбор читает чужой текст и вправе
 * спросить не о том языке, каким сейчас форматирует экран.
 */
export function numberParts(locale: string, sample: number): Intl.NumberFormatPart[] {
  return new Intl.NumberFormat(locale).formatToParts(sample);
}

/** Счётчик — целое с разрядами: «12 480». */
export function formatCount(value: number): string {
  return numberFormat('count', { maximumFractionDigits: 0 }).format(value);
}

/** Произвольное число с пределом знаков после запятой — для ячейки таблицы, чей смысл экран не знает. */
export function formatNumber(value: number, maxFractionDigits = 2): string {
  return numberFormat(`number:${maxFractionDigits}`, { maximumFractionDigits: maxFractionDigits }).format(value);
}

/**
 * Знаков после запятой, при которых число в поле ввода равно сохранённому. Меньше нельзя: поле с
 * округлением при первом же сохранении записало бы округлённое вместо того, что лежало в базе.
 * Больше незачем: дальше начинается шум двоичной дроби (0,1 + 0,2).
 */
const EXACT = 10;

/**
 * Число для ПОЛЯ ВВОДА: десятичный знак языка, разрядов нет — «1250,5». Разряды в поле мешают
 * править: курсор прыгает через пробел, а вставка из поля обратно в таблицу тащит его с собой.
 *
 * <p>⚠️ Предел знаков называют только там, где поле показывает РАСЧЁТНОЕ число (остаток разнесения).
 * Значение из базы идёт без предела — см. <see cref="EXACT" />.</p>
 */
export function formatInput(value: number | null | undefined, maxFractionDigits = EXACT): string {
  if (value == null) return '';
  return numberFormat(`input:${maxFractionDigits}`, {
    maximumFractionDigits: maxFractionDigits, useGrouping: false,
  }).format(value);
}

/** Сумма для поля ввода: копейки всегда, разрядов нет — «1250,50». */
export function formatInputAmount(value: number | null | undefined): string {
  if (value == null) return '';
  return numberFormat('input:amount', {
    minimumFractionDigits: 2, maximumFractionDigits: 2, useGrouping: false,
  }).format(value);
}

// ─── Даты ───────────────────────────────────────────────────────────────────────────────────────

type DateLike = Date | string | number;

const DAY: Intl.DateTimeFormatOptions = { day: '2-digit', month: '2-digit', year: 'numeric' };
const CLOCK: Intl.DateTimeFormatOptions = { hour: '2-digit', minute: '2-digit' };
const DATE_ONLY = /^(\d{4})-(\d{2})-(\d{2})$/;

/**
 * Дата без времени: «05.10.2026».
 *
 * <p>⚠️ Строка вида `2026-10-05` — календарный день, а не момент: `new Date('2026-10-05')` читает её
 * как полночь по Гринвичу, и западнее Гринвича день становится вчерашним. Поэтому такая строка
 * форматируется в том же поясе, в каком разобрана. Момент со временем — в поясе браузера.</p>
 *
 * <p>Нечитаемое возвращается как есть: показать человеку исходный текст честнее, чем «Invalid Date».</p>
 */
export function formatDate(value: DateLike): string {
  if (typeof value === 'string') {
    const day = DATE_ONLY.exec(value);
    if (day) {
      return dateFormat('day:utc', { ...DAY, timeZone: 'UTC' })
        .format(new Date(Date.UTC(+day[1], +day[2] - 1, +day[3])));
    }
  }
  return render(value, 'day', DAY);
}

/** Дата и время до минут: «05.10.2026, 14:32». */
export function formatDateTime(value: DateLike): string {
  return render(value, 'minute', { ...DAY, ...CLOCK });
}

/** Дата и время до секунд — для журналов, где порядок событий внутри минуты важен. */
export function formatDateTimeSeconds(value: DateLike): string {
  return render(value, 'second', { ...DAY, ...CLOCK, second: '2-digit' });
}

/** День и время без года — для ленты недавнего: «05.10, 14:32». */
export function formatDayTime(value: DateLike): string {
  return render(value, 'daytime', { day: '2-digit', month: '2-digit', ...CLOCK });
}

/** Время до минут: «14:32». */
export function formatTime(value: DateLike): string {
  return render(value, 'time', CLOCK);
}

/** Время до секунд: «14:32:07». */
export function formatTimeSeconds(value: DateLike): string {
  return render(value, 'time:second', { ...CLOCK, second: '2-digit' });
}

function render(value: DateLike, key: string, options: Intl.DateTimeFormatOptions): string {
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? String(value) : dateFormat(key, options).format(date);
}
