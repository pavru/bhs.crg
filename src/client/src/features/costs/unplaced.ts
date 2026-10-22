import type { UnplacedRead } from '@/shared/api/invoiceRecognition';
import { ruPlural } from '@/shared/utils/pluralize';

/**
 * «Прочитано, в счёт не положено» (ТЗ COST-8, issue #1484): сводка по ключам — вариант 1b макета.
 * Сервер отдаёт по записи на каждую строку файла; показ сводит их по ключу и причине, иначе колонка
 * «для наборов данных» в счёте на 400 строк дала бы 400 одинаковых строк таблицы, и опечатка в ней тонула бы.
 */

/** Строка сводки: ключ с одной причиной в одном месте — шапке или строках файла. */
export interface UnplacedGroup {
  id: string;
  place: UnplacedRead['place'];
  key: string;
  reason: UnplacedRead['reason'];
  detail: string | null;
  field: string | null;
  items: UnplacedRead[];
}

/** Ключ строк в свёрнутой строке: сколько строк файла его прочитали. */
export interface UnplacedLineKey {
  key: string;
  rows: number;
}

/** Сколько ключей строк называет свёрнутая строка; остальные — числом. */
export const SUMMARY_KEYS = 3;

/** Сколько номеров строк называет клетка «Где»; дальше — «и ещё N». */
const ROWS_NAMED = 5;

/** Сколько разных значений называет строка сводки; все — по раскрытию. */
const VALUES_NAMED = 3;

/** Сначала шапка, потом строки — в порядке, в каком их отдал сервер. */
export function unplacedGroups(list: readonly UnplacedRead[]): UnplacedGroup[] {
  const groups = new Map<string, UnplacedGroup>();
  for (const read of [...list.filter(r => r.place === 'header'), ...list.filter(r => r.place === 'line')]) {
    const id = `${read.place}|${read.key}|${read.reason}|${read.detail ?? ''}`;
    let group = groups.get(id);
    if (!group) {
      group = { id, place: read.place, key: read.key, reason: read.reason, detail: read.detail, field: read.field, items: [] };
      groups.set(id, group);
    }
    group.items.push(read);
  }
  return [...groups.values()];
}

/** Для свёрнутой строки: ключи строк с числом строк и число записей шапки. */
export function unplacedSummary(list: readonly UnplacedRead[]): { lineKeys: UnplacedLineKey[]; header: number } {
  const rows = new Map<string, Set<number | null>>();
  for (const read of list) {
    if (read.place !== 'line') continue;
    const seen = rows.get(read.key) ?? new Set();
    seen.add(read.row);
    rows.set(read.key, seen);
  }
  return {
    lineKeys: [...rows].map(([key, seen]) => ({ key, rows: seen.size })),
    header: list.filter(r => r.place === 'header').length,
  };
}

/** «в 1 строке», «в 12 строках». */
export const inRows = (n: number) => `в ${n} ${ruPlural(n, 'строке', 'строках', 'строках')}`;

/** Клетка «Где»: «шапка», «строка 3», «строки 1–12», «строки 2, 5, 9». */
export function unplacedWhere(group: UnplacedGroup): string {
  if (group.place === 'header') return 'шапка';
  const rows = [...new Set(group.items.map(r => r.row ?? 0))].sort((a, b) => a - b);
  if (rows.length === 1) return `строка ${rows[0]}`;
  if (rows.every((row, i) => i === 0 || row === rows[i - 1] + 1)) return `строки ${rows[0]}–${rows[rows.length - 1]}`;
  const named = rows.slice(0, ROWS_NAMED).join(', ');
  return rows.length > ROWS_NAMED ? `строки ${named} и ещё ${rows.length - ROWS_NAMED}` : `строки ${named}`;
}

/**
 * Клетка «Прочитано»: одно значение — как есть; несколько — первые разные и число остальных. Не «…»:
 * многоточием сервер уже помечает обрезанное значение, и два смысла одного знака не различить.
 */
export function unplacedValues(group: UnplacedGroup): string {
  const values = [...new Set(group.items.map(r => r.value))];
  const named = values.slice(0, VALUES_NAMED).join(', ');
  return values.length > VALUES_NAMED ? `${named} и ещё ${values.length - VALUES_NAMED}` : named;
}

/**
 * Причина словами — по коду (сервер слов не собирает). Зовущие к действию (не разобрано, охрана, занято)
 * ведут к предложению под полем; поле названо подписью — по ключу профиля его не узнать.
 */
export function unplacedReason(read: Pick<UnplacedRead, 'place' | 'reason' | 'detail' | 'field'>): string {
  const field = read.field ? `поле «${read.field}»` : 'поле';
  switch (read.reason) {
    case 'no-field':
      return read.place === 'line' ? 'у строки счёта нет поля с таким ключом' : 'у счёта нет поля с таким ключом';
    case 'not-recognizable':
      return `${field} не заполняется распознаванием${read.detail ? `: ${read.detail}` : ''}`;
    case 'unparsed':
      return `значение не разобрано — предложено под полем${read.field ? ` «${read.field}»` : ''}`;
    case 'guard':
      return `охрана записи${read.detail ? `: ${read.detail}` : ''} — предложено под полем${read.field ? ` «${read.field}»` : ''}`;
    case 'occupied':
      return `${field} уже заполнено — прочитанное предложено под полем`;
    case 'duplicate-key':
      return `повторяет ключ «${read.detail ?? ''}» — ${field} уже заполнено им`;
    default:
      // Код, которого клиент не знает (новый сервер): назвать его, а не промолчать.
      return String(read.reason);
  }
}
