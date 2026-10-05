import { LOCALE_KEY, SYSTEM_LOCALE, resolveLocale } from '@/shared/hooks/useLocale';
import type { DatePrecision } from '@/shared/api/types';

/**
 * Единственное место, где клиент превращает число, сумму и дату в текст для человека (задача N2
 * этапа 2, issue #1103; ТЗ COST-7.2, CORE-25.3).
 *
 * <p>До него настройка «Внешний вид» обещала формат, и ни один экран обещание не держал: тридцать
 * вызовов `toLocaleString('ru-RU')` по месту, три самодельных форматтера дат и четыре — чисел.
 * Сменив язык форматирования, человек видел новый формат в предпросмотре самой настройки — и
 * больше нигде. Прямой вызов `toLocale*`, `Intl.*Format` и `toFixed` вне этого каталога роняет
 * перепись (`format.census.test.ts`).</p>
 *
 * <p><b>Язык держит модуль, а не аргумент.</b> Половина потребителей — чистые функции без доступа к
 * контексту React («3 счёта на 412 500,00 ₽» собирается в `closing.ts`, подпись дубликата — в
 * `invoiceFields.ts`); протащить язык параметром значило бы дать каждому вызову возможность его
 * забыть — то есть вернуть то, от чего уходим. Значение сюда кладёт `LocaleProvider`, первый кадр
 * берёт его из зеркала в браузере — того же, каким пользуется сама настройка.</p>
 *
 * <p>⚠️ <b>Язык слушает ПОКАЗ, а не ввод.</b> Число в поле ввода (`formatInput`) пишется одинаково
 * при любом языке: его читают разбор формы и сервер, а они знают одну запись — см. там.</p>
 *
 * <p>⚠️ <b>Валюта учёта пока одна и вписана здесь</b> — рубль. Настройкой модуля она станет в M1
 * (issue #1070), и менять придётся только `CURRENCY`: знак валюты нигде больше не пишется.</p>
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
// каждую ячейку — поэтому по одному на набор параметров. Параметры — константы модуля: собирать
// объект на каждый вызов ради форматтера, который уже лежит в кэше, значило бы платить за промах
// при каждом попадании.
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

const MONEY: Intl.NumberFormatOptions = { style: 'currency', currency: CURRENCY, currencyDisplay: 'narrowSymbol' };
const AMOUNT: Intl.NumberFormatOptions = { minimumFractionDigits: 2, maximumFractionDigits: 2 };
const QUANTITY: Intl.NumberFormatOptions = { maximumFractionDigits: 3 };
const COUNT: Intl.NumberFormatOptions = { maximumFractionDigits: 0 };

/** Сумма со знаком валюты учёта: «412 500,00 ₽». Копейки пишутся всегда — «100 ₽» и «100,00 ₽» в
 * одной колонке читаются как разная точность. */
export function formatMoney(value: number): string {
  return numberFormat('money', MONEY).format(value);
}

/** Сумма без знака валюты — для колонки, у которой валюта названа в заголовке: «412 500,00». */
export function formatAmount(value: number): string {
  return numberFormat('amount', AMOUNT).format(value);
}

/**
 * Количество: «1 250», «7,25». Лишних нулей нет, знаков после запятой не больше трёх — столько
 * хранит база, и четвёртый был бы выдумкой округления.
 */
export function formatQuantity(value: number): string {
  return numberFormat('quantity', QUANTITY).format(value);
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
  return numberFormat('count', COUNT).format(value);
}

/** Произвольное число с пределом знаков после запятой — для ячейки таблицы, чей смысл экран не знает. */
export function formatNumber(value: number, maxFractionDigits = 2): string {
  const key = `number:${maxFractionDigits}`;
  return (numbers.get(key) ?? numberFormat(key, { maximumFractionDigits: maxFractionDigits })).format(value);
}

/**
 * Знаков после запятой, при которых число в поле ввода равно сохранённому. Меньше нельзя: поле с
 * округлением при первом же сохранении записало бы округлённое вместо того, что лежало в базе.
 * Больше незачем: дальше начинается шум двоичной дроби (0,1 + 0,2).
 */
const EXACT = 10;

/**
 * Число для ПОЛЯ ВВОДА: «1250,5» — запятая, разрядов нет, цифры и минус обычные.
 *
 * <p>⚠️ <b>От языка не зависит, и это не недосмотр.</b> Текст поля читают разбор формы (`toNumber`) и
 * сервер, а они знают одну запись: десятичный знак — запятая или точка, разрядов нет. Напиши поле
 * число по-английски («1,250.5») или по-шведски (минус знаком U+2212) — и форма, открытая и
 * сохранённая без единой правки, записала бы 1,25 или не записала бы ничего (ревью PR #1207).
 * Языку ввод начнёт следовать вместе со строгим разбором набранного — это отдельная задача.</p>
 *
 * <p>Предел знаков называют только там, где поле показывает РАСЧЁТНОЕ число (остаток разнесения).
 * Значение из базы идёт без предела — см. `EXACT`.</p>
 */
export function formatInput(value: number | null | undefined, maxFractionDigits = EXACT): string {
  if (value == null || !Number.isFinite(value)) return '';
  return trimZeros(fixed(value, maxFractionDigits)).replace('.', ',');
}

/** Сумма для поля ввода: копейки всегда, разрядов нет — «1250,50». От языка не зависит, как и `formatInput`. */
export function formatInputAmount(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return '';
  return fixed(value, 2).replace('.', ',');
}

/** Запись с точкой и заданным числом знаков; «-0» не пишется — такого числа человек не набирал. */
function fixed(value: number, digits: number): string {
  const text = value.toFixed(digits);
  return /^-0(\.0*)?$/.test(text) ? text.slice(1) : text;
}

function trimZeros(text: string): string {
  return text.includes('.') ? text.replace(/0+$/, '').replace(/\.$/, '') : text;
}

/** Размер файла: «512 Б», «1,5 МБ». */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${formatCount(bytes)} Б`;
  if (bytes < 1024 * 1024) return `${formatNumber(bytes / 1024, 1)} КБ`;
  // Гигабайты появились с issue #711: вложения столько не весят, а вот резервная копия с
  // библиотекой сканов — вполне, и «1433,6 МБ» читается заметно хуже, чем «1,4 ГБ».
  if (bytes < 1024 * 1024 * 1024) return `${formatNumber(bytes / 1024 / 1024, 1)} МБ`;
  return `${formatNumber(bytes / 1024 / 1024 / 1024, 1)} ГБ`;
}

// ─── Даты ───────────────────────────────────────────────────────────────────────────────────────

type DateLike = Date | string | number;

const DAY: Intl.DateTimeFormatOptions = { day: '2-digit', month: '2-digit', year: 'numeric' };
const DAY_UTC: Intl.DateTimeFormatOptions = { ...DAY, timeZone: 'UTC' };
const MONTH_UTC: Intl.DateTimeFormatOptions = { month: '2-digit', year: 'numeric', timeZone: 'UTC' };
const MINUTE: Intl.DateTimeFormatOptions = { ...DAY, hour: '2-digit', minute: '2-digit' };
const DAY_TIME: Intl.DateTimeFormatOptions = { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' };
const TIME: Intl.DateTimeFormatOptions = { hour: '2-digit', minute: '2-digit' };
const TIME_SECONDS: Intl.DateTimeFormatOptions = { ...TIME, second: '2-digit' };

const DATE_ONLY = /^(\d{4})-(\d{2})-(\d{2})$/;
/** Момент в записи ISO: день, «T» и время. Всё прочее строкой — не дата, а чей-то текст. */
const ISO_MOMENT = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/;

/**
 * Календарный день из `ГГГГ-ММ-ДД` — полночью по Гринвичу, чтобы форматировать в том же поясе.
 * `null` — такого дня нет («2026-13-45»): `Date.UTC` молча перекатил бы его в другой месяц.
 */
function calendarDay(text: string): Date | null {
  const m = DATE_ONLY.exec(text);
  if (!m) return null;
  const date = new Date(Date.UTC(+m[1], +m[2] - 1, +m[3]));
  return date.getUTCFullYear() === +m[1] && date.getUTCMonth() === +m[2] - 1 && date.getUTCDate() === +m[3]
    ? date : null;
}

/**
 * Дата без времени: «05.10.2026».
 *
 * <p>⚠️ Строка вида `2026-10-05` — календарный день, а не момент: `new Date('2026-10-05')` читает её
 * как полночь по Гринвичу, и западнее Гринвича день становится вчерашним. Поэтому такая строка
 * форматируется в том же поясе, в каком разобрана. Момент со временем — в поясе браузера.</p>
 *
 * <p>⚠️ Строка, которая не запись ISO, возвращается КАК ЕСТЬ и в `new Date` не попадает: «05.10.2026»
 * из нераспознанного реквизита браузер прочёл бы как десятое мая и показал бы уверенно — день и
 * месяц переставлены, отказа нет (ревью PR #1207).</p>
 */
export function formatDate(value: DateLike): string {
  if (typeof value !== 'string') return render(value, 'day', DAY);
  if (DATE_ONLY.test(value)) {
    const day = calendarDay(value);
    return day ? dateFormat('day:utc', DAY_UTC).format(day) : value;
  }
  return ISO_MOMENT.test(value) ? render(value, 'day', DAY) : value;
}

/**
 * Календарный день из значения, у которого время — не смысл, а способ хранения: колонка вида «дата»
 * отдаёт и `2026-10-05`, и `2026-10-05T00:00:00Z`. День берётся из записи, пояс браузера его не
 * двигает. Неполное («2026», «2026-07») и нечитаемое — как есть.
 */
export function formatDay(value: string): string {
  const day = calendarDay(value.slice(0, 10));
  return day ? dateFormat('day:utc', DAY_UTC).format(day) : value;
}

/**
 * Дата с точностью типа поля (issue #60): год — «2026», месяц — «07.2026», день — «01.07.2026».
 * Хранится всегда полный день; здесь скрываются дополненные части. Нечитаемое — как есть.
 */
export function formatDatePrecise(iso: string | null | undefined, precision: DatePrecision = 'day'): string {
  if (!iso) return '';
  const day = calendarDay(iso.slice(0, 10));
  if (!day) return iso;
  if (precision === 'year') return iso.slice(0, 4);
  return precision === 'month'
    ? dateFormat('month:utc', MONTH_UTC).format(day)
    : dateFormat('day:utc', DAY_UTC).format(day);
}

/** Дата и время до минут: «05.10.2026, 14:32». */
export function formatDateTime(value: DateLike): string {
  return render(value, 'minute', MINUTE);
}

/** День и время без года — для ленты недавнего: «05.10, 14:32». */
export function formatDayTime(value: DateLike): string {
  return render(value, 'daytime', DAY_TIME);
}

/** Время до минут: «14:32». */
export function formatTime(value: DateLike): string {
  return render(value, 'time', TIME);
}

/** Время до секунд: «14:32:07». */
export function formatTimeSeconds(value: DateLike): string {
  return render(value, 'time:second', TIME_SECONDS);
}

function render(value: DateLike, key: string, options: Intl.DateTimeFormatOptions): string {
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? String(value) : dateFormat(key, options).format(date);
}
