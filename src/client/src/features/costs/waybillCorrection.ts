import { formatDate, formatInput } from '@/shared/format/format';
import type { WaybillCorrectionMark, WaybillVersionView, WaybillView } from '@/shared/api/waybills';

/**
 * Чистая логика исправления накладной закрытого периода (issue #1340, ТЗ COST-16; макет — вариант 1a,
 * design D10): кому кнопка «Исправить», чем пометить строку списка и что изменило каждое исправление.
 */

/**
 * Кнопка «Исправить» — только у проведённой накладной, запертой закрытым периодом, и только тому, у кого
 * правка накладных и право исправления. Остальным о режиме ни слова: кнопка вела бы в отказ.
 */
export function mayCorrectWaybill(view: WaybillView, permission: (code: string) => boolean, gone: boolean): boolean {
  return !gone && view.state === 'Posted' && !!view.locked
    && permission('costs.waybill.edit') && permission('costs.waybill.correct');
}

/**
 * Пометка строки списка — видимой строкой, а не подсказкой (D10): на телефоне подсказка не открывается,
 * а день и причину строка обязана назвать (D6).
 */
export function correctionMarkText(mark: WaybillCorrectionMark): string {
  return `исправлена ${formatDate(mark.at)} · ${mark.reason}`;
}

/** Содержимое накладной в форме версии — чтобы сравнивать последнюю версию с действующим. */
export type WaybillContent = WaybillVersionView['content'];

export function contentOfView(view: WaybillView): WaybillContent {
  return {
    number: view.number, issuedOn: view.issuedOn, warehouse: view.warehouse, constructionId: view.constructionId,
    receivedBy: view.receivedBy, note: view.note, accountingOn: view.accountingOn ?? null,
    lines: view.lines.map((line, index) => ({
      id: line.id, ordinal: index + 1, nomenclatureId: line.nomenclatureId, sourceText: line.sourceText,
      unit: line.unit, quantity: line.quantity, note: line.note,
    })),
  };
}

const HEADER: [keyof WaybillContent, string][] = [
  ['number', 'номер'], ['issuedOn', 'дата отпуска'], ['warehouse', 'склад'], ['constructionId', 'стройка'],
  ['receivedBy', 'получил'], ['note', 'примечание'],
];

const shown = (value: unknown) => (value === null || value === '' ? '—' : String(value));

/**
 * Что исправление поменяло: от прежнего содержимого (`prior`) к тому, что стало (`next`). Строки сверяются
 * по идентификатору — номер строки меняется, когда соседнюю удалили. Стройка и позиция называются словом
 * «сменена»: в версии лежит идентификатор, а имя за ним могло с тех пор поменяться.
 */
export function versionChanges(prior: WaybillContent, next: WaybillContent): string[] {
  const changes: string[] = [];
  for (const [key, label] of HEADER) {
    if ((prior[key] ?? null) === (next[key] ?? null)) continue;
    changes.push(key === 'constructionId' ? 'стройка сменена'
      : key === 'issuedOn' ? `${label}: ${prior.issuedOn ? formatDate(prior.issuedOn) : '—'} → ${next.issuedOn ? formatDate(next.issuedOn) : '—'}`
        : `${label}: ${shown(prior[key])} → ${shown(next[key])}`);
  }

  const after = new Map(next.lines.map(line => [line.id, line]));
  for (const line of prior.lines) {
    const now = after.get(line.id);
    const where = `строка ${line.ordinal}`;
    if (!now) { changes.push(`${where} удалена`); continue; }
    if (line.quantity !== now.quantity)
      changes.push(`${where}: количество ${formatInput(line.quantity)} → ${formatInput(now.quantity)}`);
    if ((line.sourceText ?? '') !== (now.sourceText ?? '')) changes.push(`${where}: наименование изменено`);
    if ((line.unit ?? '') !== (now.unit ?? '')) changes.push(`${where}: ед. ${shown(line.unit)} → ${shown(now.unit)}`);
    if (line.nomenclatureId !== now.nomenclatureId)
      changes.push(`${where}: ${line.nomenclatureId ? 'позиция сменена' : 'позиция выбрана'}`);
    if ((line.note ?? '') !== (now.note ?? '')) changes.push(`${where}: примечание изменено`);
  }
  const before = new Set(prior.lines.map(line => line.id));
  const added = next.lines.filter(line => !before.has(line.id)).length;
  if (added > 0) changes.push(added === 1 ? 'добавлена строка' : `добавлено строк: ${added}`);
  return changes;
}

/**
 * Событие исправления — строка истории (D10). Версия хранит УШЕДШЕЕ содержимое, а причина и автор — от
 * исправления, которое его заменило: подпись «версия 1 — причина» читалась бы «зачем завели версию 1».
 */
export interface CorrectionEvent {
  /** Номер ушедшей версии: «прежнее — версия N». */
  version: number;
  at: string;
  author: string;
  reason: string;
  prior: WaybillContent;
  changes: string[];
  /**
   * «Что изменилось» сверено с нынешним содержимым, а накладная с тех пор не заперта: её могли править
   * обычным путём, и те правки тоже попали в список. Версия хранит только ушедшее, и отделить их нечем.
   */
  mixed: boolean;
}

/** События свежими сверху, как приходят версии: историю открывают узнать, что случилось последним. */
export function correctionEvents(versions: WaybillVersionView[], view: WaybillView): CorrectionEvent[] {
  const byNumber = new Map(versions.map(v => [v.number, v.content]));
  return versions.map(v => {
    // Что стало после этого исправления — следующая версия, а у последней — действующее содержимое. Оно
    // равно записанному исправлением, пока накладная заперта: обычной правки у запертой нет.
    const following = byNumber.get(v.number + 1);
    const next = following ?? contentOfView(view);
    return {
      version: v.number, at: v.at, author: v.authorName ?? 'автор неизвестен', reason: v.reason,
      prior: v.content, changes: versionChanges(v.content, next), mixed: !following && !view.locked,
    };
  });
}
