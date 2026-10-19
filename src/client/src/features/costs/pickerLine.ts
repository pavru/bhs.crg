import { trimText as text, type LineWords } from './newNomenclature';

/**
 * Для какой строки открыт выбор позиции (issue #1517) — плашка под заголовком окна. Без неё человек,
 * открывший окно у строки 7 из двадцати, выбирает по памяти: слова строки в окне иначе не видны.
 */
export interface PickerLine {
  /** «Строка 3 счёта · ЭНЕРГОСФЕРА», «Соответствие поставщика «…»». */
  where: string;
  /** Количество строки, как набрано; пустое не выводится. */
  quantity?: string | null;
}

/** «Строка 3 счёта · ЭНЕРГОСФЕРА»: поставщика нет — без него, а не «· —». */
export function invoiceLineWhere(number: number, supplier: string | null | undefined): string {
  return [`Строка ${number} счёта`, text(supplier)].filter(Boolean).join(' · ');
}

export function waybillLineWhere(number: number): string {
  return `Строка ${number} накладной`;
}

export function supplierMatchWhere(supplier: string | null | undefined): string {
  return text(supplier) ? `Соответствие поставщика «${text(supplier)}»` : 'Соответствие поставщика';
}

/** «арт. mcb10-1-06C · 12 шт» — пустые части не выводятся; нечего сказать — пустая строка. */
export function plateMeta(words: LineWords, quantity?: string | null): string {
  const amount = [text(quantity), text(words.unit)].filter(Boolean).join(' ');
  return [text(words.code) && `арт. ${text(words.code)}`, amount].filter(Boolean).join(' · ');
}
