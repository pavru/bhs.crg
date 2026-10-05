import type { WaybillHeader, WaybillLineView, WaybillView } from '@/shared/api/waybills';
import { catalogRef, fromInput } from './invoiceFields';

/**
 * Чистая логика формы накладной (задача D1, issue #1083): черновик формы ↔ то, что уезжает на сервер.
 */
export interface WaybillLineDraft {
  /** `null` — строка новая: сервер заведёт её и выдаст идентификатор. */
  id: string | null;
  /** Ключ для React: у новой строки идентификатора ещё нет. */
  key: string;
  nomenclatureId: string | null;
  nomenclatureName: string | null;
  nomenclatureLost: boolean;
  sourceText: string;
  unit: string;
  quantity: string;
  note: string;
}

export interface WaybillDraft {
  number: string;
  issuedOn: string;
  warehouse: string;
  construction: string;
  receivedBy: string;
  note: string;
  lines: WaybillLineDraft[];
}

let counter = 0;
const nextKey = () => `new-${++counter}`;

export function emptyLine(): WaybillLineDraft {
  return {
    id: null, key: nextKey(), nomenclatureId: null, nomenclatureName: null, nomenclatureLost: false,
    sourceText: '', unit: '', quantity: '', note: '',
  };
}

/** Количество в поле ввода — с запятой, как его набирают; хвостовые нули не дописываются. */
export function quantityInput(value: number | null): string {
  return value == null ? '' : String(value).replace('.', ',');
}

function lineDraft(line: WaybillLineView): WaybillLineDraft {
  return {
    id: line.id, key: line.id,
    nomenclatureId: line.nomenclatureId, nomenclatureName: line.nomenclatureName,
    nomenclatureLost: line.nomenclatureLost,
    sourceText: line.sourceText ?? '', unit: line.unit ?? '',
    quantity: quantityInput(line.quantity), note: line.note ?? '',
  };
}

export function toDraft(view: WaybillView): WaybillDraft {
  return {
    number: view.number ?? '', issuedOn: view.issuedOn ?? '', warehouse: view.warehouse ?? '',
    construction: view.constructionId ?? '', receivedBy: view.receivedBy ?? '', note: view.note ?? '',
    lines: view.lines.map(lineDraft),
  };
}

export function headerPayload(draft: WaybillDraft): WaybillHeader {
  return {
    number: fromInput(draft.number), issuedOn: fromInput(draft.issuedOn),
    warehouse: fromInput(draft.warehouse), construction: fromInput(draft.construction),
    receivedBy: fromInput(draft.receivedBy), note: fromInput(draft.note),
  };
}

/**
 * Строка без единого значения — заготовка, которую человек добавил и не заполнил. На сервер она не
 * едет: сохранённая, она стала бы «несопоставленной строкой» и держала бы счётчик.
 */
export function isBlank(line: WaybillLineDraft): boolean {
  return line.nomenclatureId === null
    && [line.sourceText, line.unit, line.quantity, line.note].every(text => text.trim() === '');
}

export function linesPayload(draft: WaybillDraft): Record<string, unknown>[] {
  return draft.lines.filter(line => !isBlank(line)).map(line => ({
    id: line.id,
    nomenclature: line.nomenclatureId ? catalogRef(line.nomenclatureId) : null,
    sourceText: fromInput(line.sourceText),
    unit: fromInput(line.unit),
    // Строкой, как набрано: запятую и пробелы разбирает сервер, и он же называет строку в отказе.
    quantity: fromInput(line.quantity),
    note: fromInput(line.note),
  }));
}

/** Изменилась ли форма относительно того, что лежит на сервере. Пустые заготовки изменением не считаются. */
export function isDirty(draft: WaybillDraft, view: WaybillView): boolean {
  const saved = toDraft(view);
  const strip = (d: WaybillDraft) => JSON.stringify([
    headerPayload(d),
    linesPayload(d).map(line => ({ ...line, quantity: normalized(line.quantity) })),
  ]);
  return strip(draft) !== strip(saved);
}

/** «12,50» и «12.5» — одно количество: правкой это не считается. */
function normalized(quantity: unknown): unknown {
  if (typeof quantity !== 'string') return quantity;
  const number = Number(quantity.replace(/\s/g, '').replace(',', '.'));
  return Number.isFinite(number) ? number : quantity;
}

/** Строки формы без позиции — то, что после сохранения сервер назовёт «не сопоставлено». */
export function unmatchedCount(draft: WaybillDraft): number {
  return draft.lines.filter(line => !isBlank(line) && line.nomenclatureId === null).length;
}

/** «строка / строки / строк» по числу. */
export function linesWord(count: number): string {
  const tens = count % 100, ones = count % 10;
  if (tens >= 11 && tens <= 14) return 'строк';
  if (ones === 1) return 'строка';
  return ones >= 2 && ones <= 4 ? 'строки' : 'строк';
}

/**
 * Оговорка о несопоставленных строках — число, которое обязано быть на виду (ТЗ COST-17).
 * `null` — оговаривать нечего.
 */
export function unmatchedNote(count: number, posted: boolean): string | null {
  if (count === 0) return null;
  return `Не сопоставлено: ${count} ${linesWord(count)}. `
    + (posted
      ? 'В «материалы на объекте» они не попали — выберите позицию номенклатуры, проводить заново не нужно.'
      : 'Провести накладную можно и так, но в «материалы на объекте» эти строки не попадут, пока у них нет позиции.');
}
