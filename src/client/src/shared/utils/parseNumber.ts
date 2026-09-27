/**
 * Строгий разбор числа из текста, набранного или вставленного человеком (Excel, PDF, буфер).
 *
 * <p>Строгий НАМЕРЕННО (issue #1064). Из двух исходов — «не смог разобрать» и «разобрал неверно» —
 * второй дороже на порядок: первый человек видит и правит, второй уезжает в данные. Поэтому
 * значение либо укладывается в число ЦЕЛИКОМ, либо это отказ; разбор префикса, каким занимается
 * `parseFloat` (`parseFloat('12 шт') === 12`, `parseFloat('1.234,56') === 1.234`), запрещён.</p>
 *
 * <p>Обе принятые в отрасли записи разбираются одинаково правильно: разделитель тысяч пробелом
 * (`1 234,56`) и точкой (`1.234,56`), включая смешанную (`1.234.567,89`). Десятичный разделитель —
 * и запятая, и точка.</p>
 */

/** Пробелы-разделители тысяч: обычный, неразрывный, узкий неразрывный, тонкий (их даёт Excel). */
const GROUP_SPACES = /[\s\u00a0\u202f\u2009]+/g;

/**
 * Разбор строки в число. `null` — отказ: значение не число целиком либо его запись неоднозначна.
 * Отказ НЕ нуль и НЕ пустое значение: звать его так и есть дефект, ради которого написана функция.
 */
export function parseNumberStrict(raw: string): number | null {
  const body0 = raw.trim().replace(GROUP_SPACES, ' ');
  // Только цифры, знак и разделители. Всё прочее (единицы измерения, %, буквы) — отказ.
  const m = /^([+-]?)([\d ,.]+)$/.exec(body0);
  if (!m || !/\d/.test(m[2])) return null;
  const sign = m[1] === '-' ? -1 : 1;
  const body = m[2];

  const dots = (body.match(/\./g) ?? []).length;
  const commas = (body.match(/,/g) ?? []).length;
  const spaced = body.includes(' ');

  let decimalSep: '.' | ',' | null = null;
  if (dots > 0 && commas > 0) {
    // Есть и точка, и запятая: десятичный — тот, что стоит ПОСЛЕДНИМ, и он обязан быть один.
    decimalSep = body.lastIndexOf('.') > body.lastIndexOf(',') ? '.' : ',';
    if ((decimalSep === '.' ? dots : commas) !== 1) return null;
  } else if (dots + commas === 1) {
    const sep = dots === 1 ? '.' : ',';
    const frac = body.slice(body.indexOf(sep) + 1);
    // Единственный разделитель и РОВНО три цифры за ним — запись неоднозначная: `1.234` это и
    // «тысяча двести тридцать четыре», и «1,234». Догадка здесь и есть дефект #1064, поэтому
    // отказ. Исключение: группы уже разбиты пробелами (`1 234.567`) — тогда этот знак десятичный.
    if (frac.length === 3 && !spaced) return null;
    decimalSep = sep;
  }
  // Повторяющийся один и тот же знак (`1.234.567`) — только группирующий, дробной части нет.

  let intPart = body;
  let fracPart = '';
  if (decimalSep) {
    const i = body.lastIndexOf(decimalSep);
    intPart = body.slice(0, i);
    fracPart = body.slice(i + 1);
    if (!/^\d+$/.test(fracPart)) return null;
    if (intPart === '') intPart = '0';
  }

  // Целая часть: группы по три цифры, первая — от одной до трёх. Группируют пробел и тот из
  // `.`/`,`, который не стал десятичным.
  const groupSeps = decimalSep === '.' ? /[ ,]/ : decimalSep === ',' ? /[ .]/ : /[ .,]/;
  const groups = intPart.split(groupSeps);
  if (groups.length === 1) {
    if (!/^\d+$/.test(groups[0])) return null;
  } else if (!/^\d{1,3}$/.test(groups[0]) || !groups.slice(1).every(g => /^\d{3}$/.test(g))) {
    return null;
  }

  const n = Number(`${groups.join('')}.${fracPart || '0'}`);
  return Number.isFinite(n) ? sign * n : null;
}
