import { useMutation } from '@tanstack/react-query';
import { apiClient } from './client';
import { seenBy, useInvoiceWrite, type InvoiceView } from './invoices';
import type { MatrixPart } from './allocationMatrix';

/**
 * Исправление счёта закрытого периода (E2, issue #1100, spec v3.3 п. 1.4): причина и ВСЁ содержимое
 * счёта одним телом — шапка, строки с частями разноски и части счёта целиком.
 *
 * <p>Тело одно на предпросмотр и запись: предпросмотр считает ровно то, что запишет запись, и отдаёт
 * отметку (`stamp`), которую запись обязана прислать. Изменилось ли что-то между ними (полночь,
 * закрыли период, сменили допуск) — решает сервер, сверяя отметки.</p>
 */
export interface InvoiceCorrectionBody {
  reason: string;
  requisites: Record<string, unknown>;
  /** Строки — тем же видом, что у замены строк, и у каждой `parts` (`[]` — не разнесена). */
  lines: Record<string, unknown>[];
  /** Части счёта целиком — только у счёта без строк. */
  document: MatrixPart[];
}

/** Строка книги: ячейка (без цели — остаток счёта), день и деньги со знаком. */
export interface CorrectionLineView {
  constructionId: string | null;
  sectionId: string | null;
  articleId: string | null;
  day: string;
  gross: number;
  net: number;
  vatUnknown: boolean;
}

/**
 * Запись книги, которая ляжет исправлением. `opening` — деньги счёта, как они лежали до первой записи;
 * `alignment` — сдвиг допуском, не записанный раньше; `correction` — само исправление.
 */
export interface CorrectionRecordView {
  kind: 'opening' | 'alignment' | 'correction' | string;
  day: string;
  tolerance: number;
  lines: CorrectionLineView[];
}

export interface CorrectionPreview {
  /** Номер версии, которая уйдёт в историю. */
  version: number;
  /** День записи — им исправление ляжет в книгу. */
  day: string;
  records: CorrectionRecordView[];
  stamp: string;
}

/**
 * Что запишет исправление — не записывая. Мутация, а не запрос: ответ — про этот черновик и эту минуту,
 * и кэшированный ответ, показанный повторно, был бы отметкой, которую запись уже не примет.
 */
export function usePreviewCorrection() {
  return useMutation({
    mutationFn: ({ id, seen, body }: { id: string; seen: string; body: InvoiceCorrectionBody }) =>
      apiClient.post<CorrectionPreview>(`/costs/invoices/${id}/correction/preview`, body, seenBy(seen))
        .then(r => r.data),
  });
}

/** Записать исправление — с отметкой показанного предпросмотра. Отказ 409 перечитывает счёт сам. */
export function useCorrectInvoice() {
  return useInvoiceWrite(({ id, seen, body, stamp }: {
    id: string; seen: string; body: InvoiceCorrectionBody; stamp: string;
  }) =>
    apiClient.post<InvoiceView>(`/costs/invoices/${id}/correction`, { ...body, stamp }, seenBy(seen))
      .then(r => r.data));
}
