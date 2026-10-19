import { useMutation, useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import { invoiceQuery, seenBy, useInvoiceWrite, type InvoiceView, type VatMode } from './invoices';
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
  /**
   * Режим НДС — обязателен, как строки (issue #1498): он описывает их суммы. Без поля сервер откажет —
   * «не менять» из отсутствия он не читает.
   */
  vatMode: VatMode;
}

/** Смена режима НДС исправлением: каким был и каким стал. */
export interface VatModeShift {
  from: VatMode;
  to: VatMode;
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
  /**
   * Легла днём записи, а не своим днём (PR 6d). Нет поля — сервер старше: тогда «днём записи» значит «днём записи»
   * по дате, как было до признака.
   */
  shifted?: boolean;
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
  /** Смена режима НДС отдельным пунктом (issue #1498); `null` — режим не меняется. Нет поля — сервер старше. */
  vatMode?: VatModeShift | null;
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

/** Запись книги проводок счёта — строка истории на форме (E2, PR 6b-2). */
export interface InvoiceBookEntry {
  seq: number;
  /**
   * `opening`, `correction`, `payment`, `unpayment`, `alignment` — теми же словами, что предпросмотр.
   * Строкой, а не объединением: незнакомый вид (сервер новее клиента) показывается как пришёл.
   */
  kind: string;
  /** День записи: им исправление легло в книгу. */
  day: string;
  /** Когда записано. */
  at: string;
  /** Имя автора на момент записи; `null` — служебная запись (открытие, выравнивание) или запись старше поля. */
  author: string | null;
  reason: string | null;
  /** Версия, ушедшая в историю с исправлением. */
  version: number | null;
  /** Сумма строк: у открытия — деньги счёта целиком, у прочих — сдвиг. */
  gross: number;
  net: number;
  lines: CorrectionLineView[];
  /** Смена режима НДС исправлением (issue #1498); `null` — не менялся. Нет поля — сервер старше. */
  vatMode?: VatModeShift | null;
}

/**
 * История счёта — его книга проводок. Ключ — ПОД ключом счёта и без версии: запись счёта сбрасывает ключ
 * счетов целиком (`useInvoiceWrite`), отказ 409 и удаление — ключ счёта, и история перечитывается вместе с
 * ним одним запросом. Версия в ключе дала бы второй: сброс перечитал бы ещё наблюдаемый старый ключ, а
 * перерисовка — новый.
 */
export function useInvoiceBook(view: InvoiceView, enabled: boolean) {
  return useQuery({
    queryKey: [...invoiceQuery(view.id).queryKey, 'book'] as const,
    queryFn: () => apiClient.get<InvoiceBookEntry[]>(`/costs/invoices/${view.id}/book`).then(r => r.data),
    enabled,
  });
}
