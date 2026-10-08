import type { InvoiceListItem, InvoiceView } from '@/shared/api/invoices';
import type {
  InvoiceParty, InvoicePartyCandidate, InvoicePartyOffer, InvoicePartySide, InvoiceRecognition,
} from '@/shared/api/invoiceRecognition';
import { ruPlural } from '@/shared/utils/pluralize';
import { K, refEntryId } from './invoiceFields';

/**
 * Распознавание скана на экране счетов (issue #1077) — чистая логика: что написать в строке списка,
 * в полосе отказа, под полем и у стороны. Сторож задачи — «неудача названа, а не выглядит пустым
 * черновиком»: каждое состояние здесь получает свои слова, и неизвестное — тоже.
 */

/** Причина отказа коротко — для строки списка (рейл 320 пикселей). */
export function shortReason(reason: string | null | undefined): string {
  switch (reason) {
    case 'NotConfigured': return 'распознавание не настроено';
    case 'Unavailable': return 'движок не справился';
    case 'NoAnswer': return 'документ не прочитан';
    case 'Interrupted': return 'прервано';
    case 'Refused': return 'прочитанное не записалось';
    default: return 'причина — в счёте';
  }
}

/** Третья строчка строки списка: что со сканом. `null` — сказать нечего. */
export function rowScan(item: InvoiceListItem): { text: string; tone: 'quiet' | 'danger' | 'warning'; running: boolean } | null {
  const scan = item.recognition;
  if (!scan) return null;
  switch (scan.state) {
    case 'running': return { text: 'распознаётся…', tone: 'quiet', running: true };
    case 'failed': return { text: `не распознан — ${shortReason(scan.reason)}`, tone: 'danger', running: false };
    case 'done': return { text: 'строки не прочитаны', tone: 'warning', running: false };
    case 'none': return { text: 'не распознавался', tone: 'quiet', running: false };
    // Слово, которого клиент не знает, — не молчание: строка стоит под отбором, и причина обязана быть.
    default: return { text: 'скан требует внимания', tone: 'warning', running: false };
  }
}

/** Есть ли в списке счёт, чей скан читается, — пока есть, список опрашивается. */
export const anyRunning = (items: InvoiceListItem[] | undefined) =>
  (items ?? []).some(i => i.recognition?.state === 'running');

/** Полоса отказа в форме: слова и тон. */
export function failureNote(recognition: InvoiceRecognition): { text: string; quiet: boolean } {
  const error = recognition.error ? ` ${recognition.error}` : '';
  switch (recognition.reason) {
    case 'NotConfigured':
      // Тихо нарочно: это состояние установки, а не этого счёта, — красная полоса на каждом черновике
      // научила бы красное не читать.
      return {
        text: 'Скан не распознан: распознавание не настроено. Счёт заполняется вручную; движок настраивает администратор.',
        quiet: true,
      };
    case 'Unavailable':
      return { text: `Скан не распознан: движок не справился.${error} Поля пусты не потому, что в скане их нет.`, quiet: false };
    case 'NoAnswer':
      return { text: `Скан не распознан: в документе не прочитано ничего.${error} Проверьте, тот ли файл приложен.`, quiet: false };
    case 'Interrupted':
      return { text: `Распознавание прервано и исхода не оставило.${error}`, quiet: false };
    case 'Refused':
      return { text: `Скан прочитан, но прочитанное в счёт не записалось.${error}`, quiet: false };
    default:
      return { text: `Скан не распознан.${error}`, quiet: false };
  }
}

/** Предложение под полем: что прочитано в скане и что положить в поле по «Взять». */
export interface FieldOffer {
  text: string;
  /** Значение для поля; `undefined` — взять нечем (значение не разобрано), только показать. */
  take?: unknown;
  /** Подсказка целиком — длинное значение в строке обрезается. */
  title?: string;
}

const isPartyOffer = (offer: unknown): offer is InvoicePartyOffer =>
  typeof offer === 'object' && offer !== null && typeof (offer as InvoicePartyOffer).entryId === 'string';

/** Поля, чьё прочитанное — текст и кладётся в поле как есть. У даты и суммы текст скана ещё надо
 *  разобрать, а второй разборщик на клиенте разошёлся бы с серверным. */
const TEXT_FIELDS: readonly string[] = [K.number, K.basis, K.purpose];

/**
 * Предложение для поля шапки. Источников два:
 * <ul>
 * <li>`offers` сервера — поле было ЗАНЯТО в счёте, когда скан прочитали (или значение не разобрано);</li>
 * <li>само значение счёта — когда человек успел набрать своё и не сохранил: распознанное легло в
 * счёт, а на экране стоит набранное. Набранное побеждает, прочитанное предлагается.</li>
 * </ul>
 *
 * @param names название организации по id — для предложения по стороне.
 */
export function fieldOffer(
  key: string, view: InvoiceView, edits: Readonly<Record<string, unknown>>,
  recognition: InvoiceRecognition | undefined, names: (id: string) => string | null,
): FieldOffer | null {
  const shown = key in edits ? edits[key] : view.requisites[key];
  const party = key === K.supplier || key === K.payer;

  // Набрано поверх распознанного: в счёте лежит прочитанное (оно помечено), на экране — своё.
  if (key in edits && view.unconfirmed.includes(key) && !same(edits[key], view.requisites[key])) {
    const stored = view.requisites[key];
    if (party) {
      const id = refEntryId(stored);
      return id ? { text: names(id) ?? 'организация из справочника', take: stored } : null;
    }
    return stored == null ? null : { text: `«${String(stored)}»`, take: stored };
  }

  const offer = recognition?.state === 'done' ? recognition.offers?.[key] : undefined;
  if (offer == null) return null;

  if (isPartyOffer(offer)) {
    if (refEntryId(shown) === offer.entryId) return null;
    return {
      text: names(offer.entryId) ?? offer.text,
      take: { $ref: 'catalog', entryId: offer.entryId },
      title: `В скане: ${offer.text}`,
    };
  }

  const text = typeof offer === 'string' ? offer : '';
  if (!text || text === String(shown ?? '')) return null;
  return TEXT_FIELDS.includes(key) ? { text: `«${text}»`, take: text } : { text: `«${text}»` };
}

const same = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null);

export const SIDE_OF: Record<string, InvoicePartySide> = { [K.supplier]: 'supplier', [K.payer]: 'payer' };

/** Что сказать под полем стороны и какое действие предложить. */
export interface PartyLine {
  text: string;
  tone: 'quiet' | 'warning';
  /** `pick` — выбрать из кандидатов; `create` — завести; `details` — раскрыть причину; `take` — взять найденную. */
  action: 'pick' | 'create' | 'details' | 'take' | null;
}

/**
 * Строка состояния стороны.
 *
 * @param chosen id организации, стоящей в поле сейчас (с учётом несохранённой правки).
 * @param canCreate есть ли право заводить организации.
 */
export function partyLine(party: InvoiceParty, chosen: string | null, canCreate: boolean): PartyLine | null {
  switch (party.state) {
    case 'matched':
      // Стоит найденная — о ней говорит обычная метка «не подтверждено». Стоит другая или поле пусто —
      // найденная предлагается.
      return party.match && party.match !== chosen
        ? { text: `В скане: ${nameOf(party, party.match)}`, tone: 'quiet', action: 'take' }
        : null;
    case 'several':
      // Человек уже выбрал одну из них — решение принято.
      if (chosen && party.candidates.some(c => c.id === chosen)) return null;
      return { text: `Записей с этим ИНН: ${party.candidates.length}`, tone: 'warning', action: 'pick' };
    case 'archived':
      return { text: 'Есть в справочнике, но в архиве', tone: 'quiet', action: 'details' };
    case 'absent':
      return { text: 'В справочнике нет', tone: 'warning', action: canCreate ? 'create' : 'details' };
    case 'unknown':
      return { text: 'Не найдена; справочник прочитан не весь', tone: 'warning', action: 'details' };
    case 'noTaxId':
      return { text: 'ИНН в скане не прочитан — выберите вручную', tone: 'quiet', action: null };
    case 'badTaxId':
      return { text: `ИНН прочитан с ошибкой: ${party.taxId ?? '—'}`, tone: 'warning', action: 'details' };
    case 'unavailable':
      return { text: 'Сопоставить со справочником не удалось', tone: 'warning', action: 'details' };
    default:
      // ⚠️ Состояние, которого клиент не знает, — НЕ «организации нет»: завести по нему нельзя.
      return { text: 'Состояние сопоставления не распознано — обновите страницу', tone: 'warning', action: 'details' };
  }
}

/** Что в скане: «ООО «Ромашка», ИНН 7701234567». */
export function scanParty(party: InvoiceParty): string {
  return [party.name, party.taxId ? `ИНН ${party.taxId}` : null].filter(Boolean).join(', ');
}

/** Объяснение для раскрытия: слова сервера, а где их нет — свои. */
export function partyWhy(party: InvoiceParty, canCreate: boolean): string {
  if (party.state === 'absent' && !canCreate)
    return `Организации с ИНН ${party.taxId ?? '—'} в справочнике нет. Права заводить организации у вас нет — `
      + 'попросите того, кто ведёт справочник, и выберите её здесь.';
  return party.why ?? 'Причину сервер не назвал.';
}

export function nameOf(party: InvoiceParty, id: string): string {
  return party.candidates.find(c => c.id === id)?.name ?? 'организация из справочника';
}

/** Подпись кандидата-роли: от кого он наследует реквизиты. */
export function inheritsFrom(candidate: InvoicePartyCandidate, all: InvoicePartyCandidate[]): string | null {
  if (!candidate.inheritedFrom) return null;
  const base = all.find(c => c.id === candidate.inheritedFrom)?.name;
  return base ? `роль: реквизиты наследует от «${base}»` : 'роль: реквизиты наследует от записи другого вида';
}

/** Кандидаты для выбора: действующие первыми, архивные — в конце (они не выбираются). */
export function pickOrder(candidates: InvoicePartyCandidate[]): InvoicePartyCandidate[] {
  return [...candidates.filter(c => !c.archived), ...candidates.filter(c => c.archived)];
}

/** Над таблицей строк: распознанные строки, которые в счёт не легли. */
export function unusedLinesNote(recognition: InvoiceRecognition | undefined): string | null {
  const count = recognition?.state === 'done' ? recognition.lines?.length ?? 0 : 0;
  if (count === 0) return null;
  return `В скане ${ruPlural(count, 'прочитана', 'прочитаны', 'прочитано')} ${count} `
    + `${ruPlural(count, 'строка', 'строки', 'строк')}. У счёта уже есть свои — распознанные не добавлены. `
    + 'Чтобы взять распознанные, удалите свои строки и нажмите «Распознать ещё раз».';
}
