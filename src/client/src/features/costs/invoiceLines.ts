import { formatInput } from '@/shared/format/format';
import type { InvoiceLineMatch, InvoiceLineView, VatMode } from '@/shared/api/invoices';
import type { MatchOffer } from './supplierMatches';
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
  /** Запись есть, но она больше не позиция номенклатуры: помечается иначе, чем удалённая. */
  nomenclatureMoved?: boolean;
  /** Позиция в архиве — пометка у названия; выбор другой позиции её снимает. */
  nomenclatureArchived?: boolean;
  supplierText: string;
  supplierCode: string;
  unit: string;
  quantity: string;
  price: string;
  vatRate: string;
  vatAmount: string;
  /**
   * Сумма строки КАК В БУМАГЕ (issue #1497): при «в том числе» — с НДС, при НДС сверху — без НДС. Смену
   * режима это число переживает нетронутым: на бумаге оно то же, меняется только его смысл.
   */
  amount: string;
  note: string;
  /**
   * Соответствие, из которого позиция ПОДСТАВЛЕНА, — пометка «запомнено» (C3, issue #1079). Уезжает на
   * сервер вместе с позицией, и сервер её сверяет. `null` — позицию выбрал человек либо её нет.
   */
  matchedBy: string | null;
  /** Сведения пометки для показа: чем узнана строка, когда и кем запомнено, что стало с тех пор. */
  match?: InvoiceLineMatch | null;
  /**
   * Запомненное для этой строки, известное форме. `undefined` — форма о строке не спрашивала (ответ
   * берётся из вопроса о лежащих строках); `null` — спрашивать незачем: ключ строки с тех пор правили.
   */
  offer?: MatchOffer | null;
  /** Подстановку отменили: «Подставить запомненное» эту строку не трогает. До сохранения строк. */
  declined?: boolean;
  /** `false` — «не запоминать выбор в этой строке». На сервер уезжает только отказ. */
  remember?: boolean;
  /**
   * Сумма НДС, отложенная сменой режима (ревью PR #1508): флажок, поставленный и снятый, возвращает её, а
   * не теряет — у бумаги она могла расходиться с расчётной на копейку. Правка НДС руками её снимает.
   */
  vatAside?: string;
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
    matchedBy: null,
  };
}

/**
 * Строки с сервера — в правку. Числа показываем так, как их набирают: с запятой.
 *
 * <p>⚠️ <b>Сумма — бумажная, поле её режима</b> (issue #1497): у счёта с НДС сверху это `netAmount`, а
 * `amount` ответа там — сумма С НДС, посчитанная сервером. Взятая в черновик, она уехала бы обратно как
 * бумага, и НДС прибавился бы второй раз. Режим обязателен, умолчания нет нарочно — как у сервера
 * (`InvoiceLineValues.Completed`): с умолчанием забытое место читало бы «в том числе» у счёта сверху.</p>
 */
export function toDrafts(lines: readonly InvoiceLineView[], mode: VatMode): LineDraft[] {
  return lines.map(line => ({
    key: line.id,
    id: line.id,
    nomenclatureId: line.nomenclatureId,
    nomenclatureName: line.nomenclatureName,
    nomenclatureLost: line.nomenclatureLost,
    nomenclatureMoved: line.nomenclatureIssue === 'moved',
    nomenclatureArchived: line.nomenclatureArchived === true,
    supplierText: line.supplierText ?? '',
    supplierCode: line.supplierCode ?? '',
    unit: line.unit ?? '',
    quantity: formatInput(line.quantity),
    price: formatInput(line.price),
    vatRate: formatInput(line.vatRate),
    vatAmount: formatInput(line.vatAmount),
    amount: formatInput(mode === 'OnTop' ? line.netAmount ?? null : line.amount),
    note: line.note ?? '',
    matchedBy: line.match?.id ?? null,
    match: line.match ?? null,
  }));
}

/**
 * Что уедет на сервер. Пустое поле — `null`, а не «»: человек, стерев количество, имеет в виду
 * «количества нет», а пустую строку сервер прочитал бы как значение.
 *
 * <p>Бумажная сумма уезжает полем своего режима (issue #1497): `netAmount` при НДС сверху, `amount` при
 * «в том числе». Чужое поле сервер отвергает (400) — иначе прочитанное «с НДС» легло бы бумагой.</p>
 */
export function toPayload(drafts: readonly LineDraft[], mode: VatMode): Record<string, unknown>[] {
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
    [mode === 'OnTop' ? 'netAmount' : 'amount']: text(draft.amount),
    note: text(draft.note),
    matchedBy: draft.matchedBy,
    // Только отказ: умолчание сервера — «запоминать», и слать его на каждой строке незачем.
    ...(draft.remember === false ? { remember: false } : {}),
  }));
}

/** Строка, посчитанная для показа: бумажная сумма, НДС и сумма с НДС. */
export interface LinePreview {
  /** Сумма как в бумаге — с НДС при «в том числе», без НДС при НДС сверху. */
  amount: number | null;
  vat: number | null;
  /** Сумма С НДС — та, что делится разноской. Сверху без НДС её нет: `null`, а не бумажная. */
  gross: number | null;
}

/**
 * Сумма и НДС строки для ПОКАЗА, пока она не сохранена.
 *
 * <p>⚠️ Правило то же, что на сервере (`InvoiceLineValues.Completed`), и повторено оно нарочно — иначе
 * таблица молчала бы о суммах до сохранения, то есть сверка с суммой к оплате появлялась бы только после
 * него. Сохранённые значения всегда приходят от сервера. Совпадение правил держит общий набор примеров
 * `invoiceLineCases.json`: его считают и этот тест, и тест сервера (issue #1499).</p>
 *
 * <p>«В том числе»: НДС — сумма × ставка ÷ (100 + ставка). Сверху: НДС — сумма × ставка ÷ 100, сумма с
 * НДС — их сложение, а без НДС её нет вовсе: бумажная сумма, выданная за сумму с НДС, ошиблась бы на весь
 * налог молча.</p>
 *
 * <p>Присланное не пересчитывается: если сумма набрана руками, показываем её.</p>
 */
export function preview(draft: LineDraft, mode: VatMode): LinePreview {
  const quantity = toNumber(draft.quantity);
  const price = toNumber(draft.price);
  const rate = toNumber(draft.vatRate);

  const typed = toNumber(draft.amount);
  const amount = typed ?? (quantity != null && price != null ? money(quantity * price) : null);

  const typedVat = toNumber(draft.vatAmount);
  const known = amount != null && rate != null && rate >= 0;
  if (mode === 'Included') {
    const vat = typedVat ?? (known ? money((amount * rate) / (100 + rate)) : null);
    return { amount, vat, gross: amount };
  }
  const vat = typedVat ?? (known ? money((amount * rate) / 100) : null);
  return { amount, vat, gross: amount != null && vat != null ? money(amount + vat) : null };
}

/**
 * Итоги по строкам в правке — то же, что считает сервер (`InvoiceLineTotals`), но по несохранённому.
 *
 * <p>`vatMissing` — сколько строк без НДС (issue #1409): `vat` сложен только по указанным, и без этого
 * числа сверка писала бы «в том числе НДС: 0,00» у счёта, где НДС стоит лишь в итогах бумаги. ⚠️ Правило
 * обязано совпадать с серверным: научи «не указан» только одно место — надпись менялась бы на глазах
 * при сохранении. «Без НДС» (ставка 0) — это ноль, а не пропуск.</p>
 */
export interface LineTotals {
  count: number;
  withoutNomenclature: number;
  /** Сумма строк С НДС — строки без неё (сверху без НДС) не входят. */
  amount: number;
  vat: number;
  vatMissing: number;
  mode: VatMode;
  /** Сумма строк без НДС из бумаги — только при НДС сверху: там она и есть бумага. */
  net: number | null;
}

export function totals(drafts: readonly LineDraft[], mode: VatMode): LineTotals {
  let amount = 0;
  let net = 0;
  let vat = 0;
  let vatMissing = 0;
  for (const draft of drafts) {
    const line = preview(draft, mode);
    amount += line.gross ?? 0;
    net += line.amount ?? 0;
    if (line.vat === null) vatMissing += 1;
    else vat += line.vat;
  }
  return {
    count: drafts.length,
    withoutNomenclature: drafts.filter(d => d.nomenclatureId === null).length,
    amount: money(amount),
    vat: money(vat),
    vatMissing,
    mode,
    net: mode === 'OnTop' ? money(net) : null,
  };
}

/**
 * Надпись «в том числе НДС» под строками (issue #1409): неизвестное — словами, а не нулём.
 *
 * <p>НДС не указан ни у одной строки — так выглядит каждый счёт, где НДС только в итогах бумаги, — и
 * «0,00» здесь читалось бы как «НДС потерян». У части — сумма по указанным и сколько строк молчит:
 * одно число без оговорки выдало бы неполную сумму за полную.</p>
 */
export function vatCaption(
  sums: { count: number; vat: number; vatMissing: number },
  format: (value: number) => string,
): string {
  if (sums.count > 0 && sums.vatMissing === sums.count) return 'НДС по строкам не указан';
  const vat = `в том числе НДС: ${format(sums.vat)}`;
  return sums.vatMissing > 0 ? `${vat}, у ${lines(sums.vatMissing)} не указан` : vat;
}

/** «1 строки», «2 строк», «21 строки» — родительный падеж после «у». */
export function lines(count: number): string {
  const tens = count % 100;
  const one = count % 10 === 1 && tens !== 11;
  return `${count} ${one ? 'строки' : 'строк'}`;
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
export function guessRoles(table: readonly string[][], mode: VatMode): PasteRole[] {
  const width = Math.max(0, ...table.map(row => row.length));
  const header = headerRoles(table[0] ?? [], width, mode);
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

/** Шапка колонки говорит «без НДС» — и как её узнать, и как сказать обратное. */
const WITHOUT_VAT = /без\s*ндс/i;
const WITH_VAT = /(^|[\s(])(с|вкл\.?|включая)\s*ндс|всего/i;

/**
 * Цены вставленной таблицы — без НДС (issue #1499): так подписана шапка цены, либо сумма строки в шапке
 * одна, и она «без НДС». Только догадка для окна вставки — отметка стоит там на виду, снять её можно.
 */
export function pastedWithoutVat(table: readonly string[][]): boolean {
  if (!hasHeader(table)) return false;
  const head = table[0];
  if (head.some(cell => roleWords(cell) === 'price' && WITHOUT_VAT.test(cell))) return true;
  const sums = head.filter(cell => roleWords(cell) === 'amount');
  return sums.length > 0 && sums.every(cell => WITHOUT_VAT.test(cell));
}

/**
 * Роли из шапки таблицы — по словам, которыми колонки подписывает бумага.
 *
 * <p>Две колонки на одну денежную роль («Сумма без НДС | Сумма с НДС», issue #1499) — берётся та, что
 * подходит режиму: при «в том числе» — с НДС, при НДС сверху — без НДС. Раньше роль забирала левая, без
 * НДС, и вставка сама заводила счёт «в том числе» в расхождение ровно на НДС.</p>
 */
function headerRoles(row: readonly string[], width: number, mode: VatMode): PasteRole[] | null {
  // ⚠️ Роль не занимают ДВАЖДЫ. «Сумма без НДС | Сумма с НДС» просили одну и ту же роль, и второе
  // число молча перетирало первое (а у текстовых ролей — склеивалось с ним). Остальные колонки получают
  // «пропустить»: это видно человеку зачёркнутой колонкой в разборе, и он вправе назвать роль сам.
  // Молчаливая же потеря числа не видна вовсе — сумма просто оказывается не той.
  const found = Array.from({ length: width }, (_, column) => roleWords(row[column] ?? ''));
  const fits = mode === 'OnTop' ? WITHOUT_VAT : WITH_VAT;
  const owner = new Map<PasteRole, number>();
  found.forEach((role, column) => {
    if (role === undefined) return;
    const was = owner.get(role);
    const money = role === 'price' || role === 'amount';
    // Денежной роли — колонку своего режима, если она есть; иначе — первую, как прежде.
    const better = money && was !== undefined && !fits.test(row[was] ?? '') && fits.test(row[column] ?? '');
    if (was === undefined || better) owner.set(role, column);
  });
  const roles = found.map((role, column) => (role !== undefined && owner.get(role) === column ? role : 'skip'));

  // Шапкой считаем только то, где узнано наименование И хотя бы одно число: иначе первая строка
  // товаров, начинающаяся со слова «Кабель», сошла бы за шапку и потерялась.
  const recognized = roles.filter(role => role !== 'skip');
  return recognized.includes('supplierText')
    && recognized.some(role => role === 'quantity' || role === 'price' || role === 'amount')
    ? roles
    : null;
}

/** Роль по словам шапки колонки; `undefined` — слов не узнали. */
function roleWords(cell: string): PasteRole | undefined {
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
  return words.find(([, pattern]) => pattern.test(cell))?.[0];
}

/** Есть ли у разбора шапка, которую надо выбросить из данных. */
export function hasHeader(table: readonly string[][]): boolean {
  return headerRoles(table[0] ?? [], Math.max(0, ...table.map(row => row.length)), 'Included') !== null;
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

/**
 * Копейки с округлением «от нуля» — как считает бумага и как считает сервер (`decimal`).
 *
 * <p>⚠️ Через 15 значащих цифр, а не прямо: двоичная дробь прячет половинку. 10,05 × 10 % — это 1,005, а в
 * double — 1,00499…, и прямое округление давало 1,00 там, где сервер пишет 1,01: предпросмотр расходился
 * с сохранённым на копейку. Нашёл общий набор примеров `invoiceLineCases.json` (issue #1499).</p>
 */
export function money(value: number): number {
  return Math.sign(value) * Math.round(Number((Math.abs(value) * 100).toPrecision(15))) / 100;
}



function text(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}
