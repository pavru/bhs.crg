import type { InvoiceLineView } from '@/shared/api/invoices';
import { catalogRef } from './invoiceFields';

/**
 * Правила строк счёта, отделённые от разметки (задача C2, issue #1078, ТЗ COST-7, COST-7.2).
 *
 * Здесь то, что обязан проверять тест: разбор вставки из буфера, предпросмотр сумм, сверка суммы
 * строк с суммой к оплате и сборка того, что уедет на сервер. Проверять это через отрисовку таблицы
 * значило бы проверять заодно роутер, провайдеры и вёрстку — и падать от любого из них.
 */

/**
 * Строка в правке. Числа — СТРОКАМИ, как их набрал человек.
 *
 * <p>Своим состоянием, а не значением с сервера: пока человек правит таблицу, он вправе оставить
 * «7,2» недонабранным, и переписывать его текст на каждый удар по клавише нельзя — курсор прыгнет.
 * Разбирает числа сервер (он принимает и «1 234,56»), и он же возвращает досчитанное.</p>
 *
 * <p>⚠️ <b>`id` хранится и уезжает обратно.</b> Это не формальность: строка с `id` правится на месте, а
 * тот же набор без `id` означает «удали эти строки и заведи новые». На строку будет ссылаться разноска
 * по количеству (F1), и потерянный `id` рвал бы ссылку молча.</p>
 */
export interface LineDraft {
  /** Ключ для React. У сохранённой строки — её `id`, у новой — свой: без него React путает строки. */
  key: string;
  id: string | null;
  nomenclatureId: string | null;
  /** Название выбранной позиции — чтобы показать её, не спрашивая справочник заново. */
  nomenclatureName: string | null;
  /**
   * Позиции, на которую ссылается строка, в справочнике НЕТ — её удалили.
   *
   * <p>⚠️ Приходит от сервера, а не выводится из пустого названия: пустым оно бывает и у законной
   * записи без имени, и у выбранной только что позиции. Выведи мы потерю сами — такая строка краснела
   * бы «позиция не найдена» сразу после выбора (нашло ревью PR #1117).</p>
   *
   * <p>У новой строки — <c>false</c>: ссылки ещё нет, терять нечего.</p>
   */
  nomenclatureLost: boolean;
  supplierText: string;
  supplierCode: string;
  unit: string;
  quantity: string;
  price: string;
  vatRate: string;
  vatAmount: string;
  amount: string;
  note: string;
}

let sequence = 0;

/** Пустая строка для дописывания руками. */
export function emptyDraft(): LineDraft {
  sequence += 1;
  return {
    key: `новая-${sequence}`,
    id: null,
    nomenclatureId: null,
    nomenclatureName: null,
    nomenclatureLost: false,
    supplierText: '',
    supplierCode: '',
    unit: '',
    quantity: '',
    price: '',
    vatRate: '',
    vatAmount: '',
    amount: '',
    note: '',
  };
}

/** Строки с сервера — в правку. Числа показываем так, как их набирают: с запятой. */
export function toDrafts(lines: readonly InvoiceLineView[]): LineDraft[] {
  return lines.map(line => ({
    key: line.id,
    id: line.id,
    nomenclatureId: line.nomenclatureId,
    nomenclatureName: line.nomenclatureName,
    nomenclatureLost: line.nomenclatureLost,
    supplierText: line.supplierText ?? '',
    supplierCode: line.supplierCode ?? '',
    unit: line.unit ?? '',
    quantity: numberInput(line.quantity),
    price: numberInput(line.price),
    vatRate: numberInput(line.vatRate),
    vatAmount: numberInput(line.vatAmount),
    amount: numberInput(line.amount),
    note: line.note ?? '',
  }));
}

/**
 * Что уедет на сервер. Пустое поле — `null`, а не «»: человек, стерев количество, имеет в виду
 * «количества нет», а пустую строку сервер прочитал бы как значение.
 */
export function toPayload(drafts: readonly LineDraft[]): Record<string, unknown>[] {
  return drafts.map(draft => ({
    id: draft.id,
    nomenclature: draft.nomenclatureId ? catalogRef(draft.nomenclatureId) : null,
    supplierText: text(draft.supplierText),
    supplierCode: text(draft.supplierCode),
    unit: text(draft.unit),
    quantity: text(draft.quantity),
    price: text(draft.price),
    vatRate: text(draft.vatRate),
    vatAmount: text(draft.vatAmount),
    amount: text(draft.amount),
    note: text(draft.note),
  }));
}

/**
 * Сумма и НДС строки для ПОКАЗА, пока она не сохранена.
 *
 * <p>⚠️ Правило то же, что на сервере, и повторено оно нарочно — иначе таблица молчала бы о суммах до
 * сохранения, то есть сверка с суммой к оплате появлялась бы только после него. Сохранённые значения
 * всегда приходят от сервера: расхождение правил поймал бы тест, считающий те же числа (1666,67 на
 * ставке 20 % — это «в том числе», а не «сверху»).</p>
 *
 * <p>Присланное не пересчитывается: если сумма набрана руками, показываем её.</p>
 */
export function preview(draft: LineDraft): { amount: number | null; vat: number | null } {
  const quantity = toNumber(draft.quantity);
  const price = toNumber(draft.price);
  const rate = toNumber(draft.vatRate);

  const typed = toNumber(draft.amount);
  const amount = typed ?? (quantity != null && price != null ? money(quantity * price) : null);

  const typedVat = toNumber(draft.vatAmount);
  const vat = typedVat ?? (amount != null && rate != null && rate >= 0
    ? money((amount * rate) / (100 + rate))
    : null);

  return { amount, vat };
}

/** Итоги по строкам в правке — то же, что считает сервер, но по ненасохранённому. */
export function totals(drafts: readonly LineDraft[]): {
  count: number; withoutNomenclature: number; amount: number; vat: number;
} {
  let amount = 0;
  let vat = 0;
  for (const draft of drafts) {
    const line = preview(draft);
    amount += line.amount ?? 0;
    vat += line.vat ?? 0;
  }
  return {
    count: drafts.length,
    withoutNomenclature: drafts.filter(d => d.nomenclatureId === null).length,
    amount: money(amount),
    vat: money(vat),
  };
}

/**
 * Расхождение суммы строк с суммой к оплате (ТЗ COST-6.2): `null` — сверять нечего.
 *
 * <p>⚠️ Расхождение — ЧИСЛО, а не ошибка. У поставщика бывает округление, скидка строкой и доставка,
 * не попавшая в таблицу; запрет на сохранение здесь означал бы, что счёт нельзя завести, пока он не
 * сойдётся, — а заводят его как раз затем, чтобы разбираться.</p>
 */
export function mismatch(total: unknown, linesAmount: number, count: number): number | null {
  if (count === 0) return null;
  const paper = typeof total === 'number' ? total : toNumber(String(total ?? ''));
  if (paper === null) return null;
  const difference = money(paper - linesAmount);
  return difference === 0 ? null : difference;
}

/** Роль колонки вставленной таблицы. «Пропустить» — тоже ответ, и он нужен: лишние колонки бывают. */
export type PasteRole =
  | 'skip' | 'supplierText' | 'supplierCode' | 'unit' | 'quantity' | 'price' | 'vatRate'
  | 'vatAmount' | 'amount' | 'note';

export const PASTE_ROLES: readonly { role: PasteRole; title: string }[] = [
  { role: 'skip', title: 'пропустить' },
  { role: 'supplierText', title: 'наименование в счёте' },
  { role: 'supplierCode', title: 'артикул' },
  { role: 'unit', title: 'единица' },
  { role: 'quantity', title: 'количество' },
  { role: 'price', title: 'цена' },
  { role: 'vatRate', title: 'ставка НДС' },
  { role: 'vatAmount', title: 'сумма НДС' },
  { role: 'amount', title: 'сумма' },
  { role: 'note', title: 'примечание' },
];

/**
 * Разобрать вставленное в таблицу «строки × колонки».
 *
 * <p>Разделитель — ТАБУЛЯЦИЯ: так отдают таблицу и Excel, и просмотрщики PDF, и веб-страницы. Точка с
 * запятой добавлена для CSV, сохранённого по-русски. Одиночный пробел разделителем НЕ считается:
 * «кабель ВВГ 3х2,5» — это одна ячейка, и разбей мы её по пробелам, наименование рассыпалось бы на
 * четыре колонки у каждой строки.</p>
 *
 * <p>⚠️ <b>Разделитель ОДИН на всю вставку, а не какой попадётся в строке.</b> Есть табуляция — делим
 * только по ней; нет — по точке с запятой. Иначе точка с запятой ВНУТРИ ячейки таб-таблицы («Труба 20;
 * ГОСТ 55000») добавляет этой строке лишнюю колонку, а лишняя колонка сдвигает роли у ВСЕЙ таблицы:
 * цена уезжает в количество и даёт правдоподобную сумму. Нашло ревью PR #1117.</p>
 *
 * <p>Пустые строки выброшены — ими отделяют разделы в бумаге.</p>
 */
export function parseTable(clipboard: string): string[][] {
  const separator = clipboard.includes('\t') ? '\t' : ';';
  return clipboard
    .split(/\r?\n/)
    .map(line => line.split(separator).map(cell => cell.trim()))
    .filter(cells => cells.some(cell => cell.length > 0));
}

/**
 * Назвать колонки самим: текст — наименование, числа — по порядку «количество, цена, сумма».
 *
 * <p>⚠️ Это ДОГАДКА, и она названа догадкой на экране: человек видит разбор до вставки и правит роли.
 * Угадать наверняка нельзя — у одного поставщика колонка «сумма» стоит третьей, у другого шестой, а
 * ошибка в роли дороже, чем кажется: цена, попавшая в количество, даёт правдоподобную сумму.</p>
 *
 * <p>Шапку таблицы узнаём по словам в первой строке и роли берём из неё — это надёжнее порядка.</p>
 */
export function guessRoles(table: readonly string[][]): PasteRole[] {
  const width = Math.max(0, ...table.map(row => row.length));
  const header = headerRoles(table[0] ?? [], width);
  if (header) return header;

  // ⚠️ Числовой — та колонка, где ЕСТЬ хотя бы одно число, а не та, где нет ничего нечислового.
  // Пустая колонка (два таба подряд — обычный результат извлечения таблицы из PDF) проходила как
  // числовая и забирала роль из очереди «количество, цена, сумма»: количество терялось, цена
  // становилась количеством, сумма — ценой. Нашло ревью PR #1117.
  const numeric = Array.from({ length: width }, (_, column) => {
    const filled = table.map(row => row[column]).filter(cell => cell !== undefined && cell !== '');
    return filled.length > 0 && filled.every(cell => toNumber(cell) !== null);
  });

  const byOrder: PasteRole[] = ['quantity', 'price', 'amount'];
  let next = 0;
  let named = false;

  return Array.from({ length: width }, (_, column) => {
    if (!numeric[column]) {
      if (named) return 'note';
      named = true;
      return 'supplierText';
    }
    return next < byOrder.length ? byOrder[next++] : 'skip';
  });
}

/** Роли из шапки таблицы — по словам, которыми колонки подписывает бумага. */
function headerRoles(row: readonly string[], width: number): PasteRole[] | null {
  // ⚠️ Слова ищутся ЦЕЛИКОМ, а не как попало внутри слова: «ед» без границ совпадало с «прЕДмет», и
  // колонка «Предмет поставки» становилась единицей измерения — фразой на семьдесят знаков, которую
  // сервер отвергал (а до правки отвечал пятисотым). Нашло ревью PR #1117.
  //
  // ⚠️ И порядок здесь — часть смысла: «сумма НДС» обязана проверяться ДО «суммы», но по ТОЧНОЙ
  // фразе. Прежнее «или просто НДС» ловило «Сумма с НДС» — то есть ИТОГ строки уезжал в сумму НДС, а
  // итог сервер досчитывал сам, и сверка «в том числе НДС» сходилась при НДС размером во всю строку.
  const words: [PasteRole, RegExp][] = [
    ['supplierCode', /артикул|код/i],
    ['supplierText', /наимен|товар|услуг|описан|предмет/i],
    ['unit', /(^|[\s.,(])ед\.?([\s.,)]|$)|единиц/i],
    ['quantity', /кол-?в|количест/i],
    ['vatRate', /ставк|ндс,? ?%|% ?ндс/i],
    ['vatAmount', /сумма,? ?ндс|ндс,? ?сумма|в ?т\.?ч\.? ?ндс|^ндс\b/i],
    ['price', /цена|стоимость единиц/i],
    ['amount', /сумма|всего|итого/i],
    ['note', /примеч/i],
  ];

  // ⚠️ Роль не занимают ДВАЖДЫ. «Сумма без НДС | Сумма с НДС» просили одну и ту же роль, и второе
  // число молча перетирало первое (а у текстовых ролей — склеивалось с ним). Вторая колонка получает
  // «пропустить»: это видно человеку зачёркнутой колонкой в разборе, и он вправе назвать роль сам.
  // Молчаливая же потеря числа не видна вовсе — сумма просто оказывается не той.
  const taken = new Set<PasteRole>();
  const roles = Array.from({ length: width }, (_, column) => {
    const cell = row[column] ?? '';
    const found = words.find(([, pattern]) => pattern.test(cell))?.[0];
    if (found === undefined || taken.has(found)) return 'skip';
    taken.add(found);
    return found;
  });

  // Шапкой считаем только то, где узнано наименование И хотя бы одно число: иначе первая строка
  // товаров, начинающаяся со слова «Кабель», сошла бы за шапку и потерялась.
  const recognized = roles.filter(role => role !== 'skip');
  return recognized.includes('supplierText')
    && recognized.some(role => role === 'quantity' || role === 'price' || role === 'amount')
    ? roles
    : null;
}

/** Есть ли у разбора шапка, которую надо выбросить из данных. */
export function hasHeader(table: readonly string[][]): boolean {
  return headerRoles(table[0] ?? [], Math.max(0, ...table.map(row => row.length))) !== null;
}

/** Собрать строки из разобранной таблицы и названных ролей. */
export function fromTable(table: readonly string[][], roles: readonly PasteRole[]): LineDraft[] {
  const rows = hasHeader(table) ? table.slice(1) : table;

  return rows.map(row => {
    const draft = emptyDraft();
    roles.forEach((role, column) => {
      const cell = row[column] ?? '';
      if (role === 'skip' || cell === '') return;
      if (role === 'supplierText' || role === 'supplierCode' || role === 'unit' || role === 'note') {
        draft[role] = draft[role] ? `${draft[role]} ${cell}` : cell;
        return;
      }
      draft[role] = cell;
    });
    return draft;
  });
}

/** Число из набранного текста: запятая и пробелы внутри числа не мешают. `null` — не число. */
export function toNumber(text: string): number | null {
  const normalized = text.replace(/[\s\u00A0]/g, '').replace(',', '.');
  if (normalized === '') return null;
  const value = Number(normalized);
  return Number.isFinite(value) ? value : null;
}

/** Копейки с округлением «от нуля» — как считает бумага и как считает сервер. */
function money(value: number): number {
  return Math.sign(value) * Math.round(Math.abs(value) * 100) / 100;
}

function numberInput(value: number | null): string {
  return value == null ? '' : String(value).replace('.', ',');
}

function text(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}
