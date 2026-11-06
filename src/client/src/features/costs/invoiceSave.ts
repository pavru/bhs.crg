import { useRef, useState } from 'react';
import type { InvoiceView } from '@/shared/api/invoices';

/**
 * Одна кнопка «Сохранить» в сводке счёта (costs-invoice-screen-10-layout, D-10.7, ТЗ COST-6.2): шапка и
 * строки — одним нажатием, одной очередью, шапка раньше строк.
 *
 * <p>Пока нет автосохранения (ребёнок 12), у счёта одна кнопка вместо двух. Порядок не вкусовой:
 * строки запоминают соответствия за поставщиком, который ЛЕЖИТ в счёте (#1657), и запись строк раньше
 * шапки запомнила бы их за прежним. Версия у счёта одна, поэтому строки идут с версией из ответа шапки.
 * Отказ шапки строк не пишет: правки остаются на экране, причину называет отказ.</p>
 */

/** Запись реквизитов: вид из ответа или `null` — отказ (о нём уже сказано). */
export type HeaderStep = () => Promise<InvoiceView | null>;

/** Запись строк: с видом из ответа шапки, если она шла первой. `false` — отказ (о нём уже сказано). */
export type LinesStep = (after: InvoiceView | null) => Promise<boolean>;

/**
 * Записать по очереди то, что грязно. Чистая — её и проверяют тесты.
 *
 * @returns записалось ли всё: уходить со счёта после отказа нельзя — правки бы пропали.
 */
export async function saveInOrder(header: HeaderStep | null, lines: LinesStep | null): Promise<boolean> {
  let after: InvoiceView | null = null;
  if (header) {
    after = await header();
    if (!after) return false;
  }
  return lines ? lines(after) : true;
}

/**
 * Очередь записи формы: шапка даёт свой шаг, строки регистрируют свой (`lines.current`) — таблица
 * строк держит свой черновик и свою основу сама.
 */
export function useInvoiceSave({ headerDirty, linesDirty, saveHeader }: {
  headerDirty: boolean;
  linesDirty: boolean;
  saveHeader: HeaderStep;
}) {
  const lines = useRef<LinesStep | null>(null);
  const [saving, setSaving] = useState(false);

  async function save(): Promise<boolean> {
    setSaving(true);
    try {
      return await saveInOrder(headerDirty ? saveHeader : null, linesDirty ? lines.current : null);
    } finally {
      setSaving(false);
    }
  }

  return { save, saving, lines };
}
