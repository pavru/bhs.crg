import type {
  AttachmentKind, ConjunctureItem, ConjunctureOffer, EstimateAttachment, VolumeLine,
} from '@/shared/api/planningConjuncture';
import { formatCount, formatDate, formatDay } from '@/shared/format/format';
import { ruPlural } from '@/shared/utils/pluralize';

/**
 * Слова и строки приложений сметы (change `plan-11-conjuncture`, макет П11, вариант 1): заголовок и сводка строки
 * списка, способ выбора словом, строки сетки КА («позиция → предложения») и ВОР. Чистые функции — их проверяет
 * `attachmentRows.test.ts`, а экран только рисует.
 */

export const KIND_LABEL: Record<AttachmentKind, string> = { ka: 'КА', vor: 'ВОР' };
export const KIND_NAME: Record<AttachmentKind, string> = { ka: 'Конъюнктурный анализ', vor: 'Ведомость объёмов работ' };

const count = (n: number, one: string, few: string, many: string) => `${formatCount(n)} ${ruPlural(n, one, few, many)}`;

/** Ключ приложения в адресе и в выборе: вид и id — у КА и ВОР id из разных таблиц. */
export const attachmentKey = (a: Pick<EstimateAttachment, 'kind' | 'id'>) => `${a.kind}:${a.id}`;

/** Заголовок: номер и название из файла, иначе имя файла целиком (номер есть у половины КА — разбор §78). */
export function attachmentTitle(a: Pick<EstimateAttachment, 'number' | 'name' | 'fileName'>): string {
  const number = a.number?.trim();
  const name = a.name?.trim();
  if (number && name) return `${number} · ${name}`;
  return name || number || a.fileName;
}

/** Сводка строки списка: «52 позиции · 3 поставщика · цены на 25.03.2026» или «48 работ в 7 главах». */
export function attachmentSummary(a: EstimateAttachment): string {
  if (a.kind === 'vor') return `${count(a.parsed, 'работа', 'работы', 'работ')} в ${count(a.chapters, 'главе', 'главах', 'главах')}`;
  const parts = [count(a.parsed, 'позиция', 'позиции', 'позиций')];
  if (a.suppliers !== null) parts.push(count(a.suppliers, 'поставщик', 'поставщика', 'поставщиков'));
  if (a.currentPriceDate) parts.push(`цены на ${formatDay(a.currentPriceDate)}`);
  return parts.join(' · ');
}

/** «приложено 08.11.2026, Иванова А.» */
export const attachedWords = (a: Pick<EstimateAttachment, 'importedAt' | 'importedBy'>) =>
  `приложено ${formatDate(a.importedAt)}${a.importedBy ? `, ${a.importedBy}` : ''}`;

/** «частично 84 из 86» — приложено с согласия без не разобранных; всё разобрано — `null`. */
export const partialWords = (a: Pick<EstimateAttachment, 'parsed' | 'inFile'>) =>
  a.parsed < a.inFile ? `частично ${formatCount(a.parsed)} из ${formatCount(a.inFile)}` : null;

/** КА и ВОР одним списком, новые сверху (макет: оба вида вместе). */
export function mergeAttachments(ka: readonly EstimateAttachment[], vor: readonly EstimateAttachment[]): EstimateAttachment[] {
  return [...ka, ...vor].sort((x, y) => Date.parse(y.importedAt) - Date.parse(x.importedAt));
}

const priceOf = (o: ConjunctureOffer) => o.cost?.price ?? null;

/** Самая низкая цена без НДС среди предложений; цены не видны (нет права) или их нет — `null`. */
function minPrice(item: ConjunctureItem): number | null {
  const prices = item.offers.map(priceOf).filter((p): p is number => p !== null);
  return prices.length ? Math.min(...prices) : null;
}

/**
 * Способ выбора позиции словом — всегда, и без права на цены (вопрос 6 листа 0, предложенный ответ): «минимальная»,
 * «выбрано в файле» (и «не минимальная», когда цены видны и это так), «цен нет», «не учитывается», у одного
 * предложения — «единственное».
 */
export function selectionWords(item: ConjunctureItem): string {
  if (item.inactive) return 'не учитывается';
  if (item.selection === 'none') return 'цен нет';
  if (item.offers.length === 1) return 'единственное';
  if (item.selection === 'min') return 'минимальная';
  const min = minPrice(item);
  const selected = item.cost?.selectedPrice ?? null;
  return min !== null && selected !== null && min < selected ? 'выбрано в файле, не минимальная' : 'выбрано в файле';
}

/**
 * Пометка предложения под позицией: «выбрано», «минимальная» или обе. Без права на цены минимальное известно только
 * у выбранного способом «минимальная».
 */
export function offerWords(item: ConjunctureItem, offer: ConjunctureOffer): string | null {
  const min = minPrice(item);
  const minimal = min !== null ? priceOf(offer) === min : item.selection === 'min' && offer.selected;
  const words = [offer.selected ? 'выбрано' : null, minimal && item.selection !== 'none' ? 'минимальная' : null];
  return words.filter(Boolean).join(' · ') || null;
}

/** Строка сетки КА: глава, позиция, её предложение, а у позиции без цен — одна строка «N предложений без цен». */
export type KaLine =
  | { kind: 'chapter'; key: string; caption: string }
  | { kind: 'item'; key: string; item: ConjunctureItem; single: boolean }
  | { kind: 'offer'; key: string; item: ConjunctureItem; offer: ConjunctureOffer }
  | { kind: 'no-prices'; key: string; item: ConjunctureItem };

const needle = (query: string) => query.trim().toLocaleLowerCase('ru');
const has = (q: string, ...texts: (string | null)[]) => texts.some(t => t?.toLocaleLowerCase('ru').includes(q));
const chapterCaption = (caption: string, index: number) => caption.trim() || `Глава ${index}`;

/**
 * Строки сетки КА. Одно предложение — одна строка (поставщик под наименованием позиции); несколько — строками под
 * позицией; цен нет ни у одного — одной строкой с поставщиками. Поиск — по номеру, наименованию, поставщику и ИНН:
 * найденная позиция показывается со всеми предложениями.
 */
export function kaLines(items: readonly ConjunctureItem[], query: string): KaLine[] {
  const q = needle(query);
  const lines: KaLine[] = [];
  let chapter: number | null = null;
  for (const item of items) {
    if (q && !has(q, item.number, item.caption, ...item.offers.flatMap(o => [o.supplier, o.supplierInn, o.caption]))) continue;
    if (item.chapterIndex !== chapter) {
      chapter = item.chapterIndex;
      lines.push({ kind: 'chapter', key: `chapter:${chapter}`, caption: chapterCaption(item.chapterCaption, chapter) });
    }
    const single = item.offers.length <= 1;
    lines.push({ kind: 'item', key: item.id, item, single });
    if (single) continue;
    if (item.selection === 'none') lines.push({ kind: 'no-prices', key: `${item.id}:none`, item });
    else for (const offer of item.offers) lines.push({ kind: 'offer', key: `${item.id}:${offer.order}`, item, offer });
  }
  return lines;
}

/** «ООО «Альфа-Кабель» · ИНН 7700000011» — поставщик, как его записал файл. */
export function supplierWords(offer: Pick<ConjunctureOffer, 'supplier' | 'supplierInn'>): string {
  const inn = offer.supplierInn ? `ИНН ${offer.supplierInn}` : null;
  return [offer.supplier, inn].filter(Boolean).join(' · ') || 'поставщик не назван';
}

/** «3 предложения без цен: ООО «Альфа-Кабель», ООО «ЭлектроСнаб»» */
export function noPricesWords(item: ConjunctureItem): string {
  const names = item.offers.map(o => o.supplier ?? o.supplierInn).filter(Boolean);
  const head = `${count(item.offers.length, 'предложение', 'предложения', 'предложений')} без цен`;
  return names.length ? `${head}: ${names.join(', ')}` : head;
}

/** Строка сетки ВОР: глава или работа. */
export type VorLine =
  | { kind: 'chapter'; key: string; caption: string }
  | { kind: 'line'; key: string; line: VolumeLine };

/** Строки сетки ВОР по главам; поиск — по номеру, наименованию, шифру и чертежам. */
export function vorLines(lines: readonly VolumeLine[], query: string): VorLine[] {
  const q = needle(query);
  const out: VorLine[] = [];
  let chapter: number | null = null;
  for (const line of lines) {
    if (q && !has(q, line.number, line.caption, line.code, line.drawings)) continue;
    if (line.chapterIndex !== chapter) {
      chapter = line.chapterIndex;
      out.push({ kind: 'chapter', key: `chapter:${chapter}`, caption: chapterCaption(line.chapterCaption, chapter) });
    }
    out.push({ kind: 'line', key: line.id, line });
  }
  return out;
}

/** Обоснование работы одной строкой: «шифр 250803-ЭОМ лист 7 · чертёж ЭОМ-2 л. 4–6 · примечание: с запасом 2 %». */
export function justificationWords(line: Pick<VolumeLine, 'code' | 'drawings' | 'note'>): string | null {
  const parts = [
    line.code ? `шифр ${line.code}` : null,
    line.drawings ? `чертёж ${line.drawings}` : null,
    line.note ? `примечание: ${line.note}` : null,
  ];
  return parts.filter(Boolean).join(' · ') || null;
}
