import type { InvoiceDuplicate, InvoiceRequisites } from '@/shared/api/invoices';

/**
 * Правила формы счёта, отделённые от разметки (задача C1, issue #1076, ТЗ COST-6.2).
 *
 * Здесь то, что обязан проверять тест: состав блоков, сборка того, что уедет на сервер, и правило
 * «где показывать скан». Проверять это через отрисовку формы значило бы проверять заодно роутер,
 * провайдеры и вёрстку — и падать от любого из них.
 */

/** Ключи реквизитов счёта — те же, что у сервера. */
export const K = {
  number: 'Номер',
  date: 'Дата',
  supplier: 'Поставщик',
  payer: 'Плательщик',
  basis: 'Основание',
  purpose: 'Назначение',
  total: 'Итого',
  vat: 'ВТомЧислеНДС',
  shippedOn: 'ДатаОтгрузки',
  deferral: 'Отсрочка',
  dueDate: 'Срок',
  state: 'Состояние',
  payment: 'СостояниеОплаты',
  scan: 'Скан',
} as const;

/** Подпись у помеченного поля. Своя, а не общая с документами качества: там метка живёт в состоянии
 *  сессии и означает «только что распознали», здесь — сохранённая на сервере и переживает открытие. */
export const UNCONFIRMED_HINT = 'Распознано, не подтверждено';

/**
 * Поля, которые ведёт код: форма их показывает, но не правит.
 *
 * ⚠️ Из запроса они НЕ вырезаются. Сервер сравнивает значения, а не наличие ключа, и охрана записи
 * ядра читает отсутствующее запертое поле как стёртое — вырезав их, клиент получал бы отказ на каждом
 * сохранении.
 */
export const READ_ONLY_KEYS: readonly string[] = [K.state, K.payment, K.scan];

export interface InvoiceBlock {
  /** Идентификатор блока — он же имя для «Всё верно». */
  id: string;
  title: string;
  /** Ключи полей блока в порядке показа. */
  fields: readonly string[];
}

/**
 * Блоки формы. Шапка — первая и без прокрутки (ТЗ COST-6.2): поставщик, номер, дата, сумма к оплате.
 *
 * ⚠️ Объекта в шапке здесь НЕТ, хотя граница задачи его называет. «Объект в шапке» означает «весь
 * счёт на этот объект» — то есть разноску из одной части, а разноска и её хранение приезжают задачей
 * F2. Завести сейчас своё поле объекта значило бы получить второй ответ на вопрос «на какой объект
 * счёт», и расходились бы эти два ответа молча. Место под него в шапке оставлено.
 */
export const BLOCKS: readonly InvoiceBlock[] = [
  { id: 'head', title: 'Счёт', fields: [K.supplier, K.number, K.date, K.total] },
  { id: 'requisites', title: 'Реквизиты', fields: [K.payer, K.vat, K.basis, K.purpose] },
  { id: 'payment', title: 'Оплата', fields: [K.shippedOn, K.deferral, K.dueDate, K.payment, K.state] },
];

/**
 * Что уедет на сервер: реквизиты, КАК ИХ ОТДАЛ СЕРВЕР, плюс правки человека.
 *
 * ⚠️ Собирать объект поимённо по полям формы нельзя, и это не вкусовщина. Тип счёта расширяемый:
 * заказчик вправе дописать в него свои поля, формы у них нет, а в реквизитах они есть. Поимённая
 * сборка стирала бы их при каждом сохранении — молча, потому что ни сервер, ни тип, ни охрана записи
 * не считают отсутствие поля ошибкой (та же ловушка, что в issue #813: узкий DTO под широким типом).
 * Сюда же попадают и поля, которые ведёт код: они обязаны уехать неизменными.
 */
export function toRequisites(
  stored: InvoiceRequisites, edits: Readonly<Record<string, unknown>>,
): InvoiceRequisites {
  return { ...stored, ...edits };
}

/**
 * Поля блока, с которых «Всё верно» снимет метку. Пусто — кнопку показывать не надо: пустой перечень
 * сервер отвергает, и предлагать действие, которое заведомо откажет, — обман.
 */
export function unconfirmedInBlock(block: InvoiceBlock, unconfirmed: readonly string[]): string[] {
  return block.fields.filter(key => unconfirmed.includes(key));
}

/**
 * Показывать ли у поля метку «распознано, не подтверждено».
 *
 * Тронутое поле метку теряет СРАЗУ, не дожидаясь сохранения: снимает её правка (решение владельца
 * 29.09.2026), и метка, висящая на поле, которое человек только что переписал, утверждала бы о нём
 * неправду. Окончательный ответ всё равно за сервером — он снимает метку по сравнению значений, и
 * после сохранения форма рисует то, что он вернул.
 */
export function isMarked(
  key: string, unconfirmed: readonly string[], edits: Readonly<Record<string, unknown>>,
): boolean {
  return unconfirmed.includes(key) && !(key in edits);
}

/** Есть ли в форме хоть одна метка вне известных блоков — такие поля дописал заказчик. */
export function unconfirmedOutsideBlocks(unconfirmed: readonly string[]): string[] {
  const known = new Set(BLOCKS.flatMap(b => [...b.fields]));
  return unconfirmed.filter(key => !known.has(key));
}

/**
 * Показывать ли скан РЯДОМ с формой.
 *
 * Минимальная ширина экрана — 1280 (решение владельца 28.09.2026). Ниже панель не показывается, и
 * вместо её молчаливого исчезновения форма даёт честную замену — кнопку «Открыть скан» с названной
 * причиной. Молчаливое исчезновение читается как «скана нет».
 */
export const SCAN_BESIDE_MIN_WIDTH = 1280;

export function scanFitsBeside(viewportWidth: number): boolean {
  return viewportWidth >= SCAN_BESIDE_MIN_WIDTH;
}

/** Ссылка на запись справочника — так её хранит ядро. */
export function catalogRef(entryId: string): Record<string, unknown> {
  return { $ref: 'catalog', entryId };
}

/** Идентификатор записи из значения-ссылки; `null` — значения нет или оно не ссылка. */
export function refEntryId(value: unknown): string | null {
  if (value == null || typeof value !== 'object') return null;
  const node = value as Record<string, unknown>;
  return node.$ref === 'catalog' && typeof node.entryId === 'string' ? node.entryId : null;
}

/** Значение поля строкой для ввода: `null` и `undefined` — пустая строка, а не «null». */
export function asInput(value: unknown): string {
  if (value == null) return '';
  return typeof value === 'string' ? value : String(value);
}

/**
 * Сумма в ПОЛЕ ВВОДА: с двумя знаками и запятой — «128400,50», а не «128400.5».
 *
 * Разряды здесь НЕ разделяются, хотя в списке и в оговорке о дубликате разделяются: там сумму читают,
 * а здесь правят, и пробел внутри правимого значения заставляет человека думать, одно это число или
 * два. Запятую и пробелы сервер разбирает сам, так что уехать может и то и другое.
 *
 * ⚠️ Форматируется только то, что пришло с сервера. Пока человек набирает, в состоянии лежит его
 * собственный текст, и переписывать его на каждый удар по клавише нельзя: курсор прыгнет.
 */
export function moneyInput(value: unknown): string {
  if (typeof value === 'number') return value.toFixed(2).replace('.', ',');
  return asInput(value);
}

/**
 * Пустая строка уезжает как `null`, а не как «».
 *
 * Разница видна на сервере: `""` он читает как «значение есть, оно пустое» и кладёт в колонку пустую
 * строку, а человек, стерев номер, имеет в виду «номера нет».
 */
export function fromInput(text: string): string | null {
  const trimmed = text.trim();
  return trimmed.length > 0 ? trimmed : null;
}

/** Чем дубликат назван человеку: номер, дата и сумма — то, по чему он его узнает. */
export function duplicateLabel(duplicate: InvoiceDuplicate): string {
  const parts = [duplicate.number ?? 'без номера'];
  if (duplicate.issuedOn) parts.push(`от ${formatDate(duplicate.issuedOn)}`);
  if (duplicate.total != null) parts.push(`на ${formatMoney(duplicate.total)}`);
  return parts.join(' ');
}

export function formatDate(iso: string): string {
  const [year, month, day] = iso.slice(0, 10).split('-');
  return day && month && year ? `${day}.${month}.${year}` : iso;
}

export function formatMoney(value: number): string {
  return `${value.toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ₽`;
}
