import { useMutation, useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import { QK, seenBy, useInvoiceWrite, type InvoiceView } from './invoices';

/**
 * Оплата счёта (задача C5, issue #1082, ТЗ COST-4, COST-9, COST-16).
 *
 * ⚠️ Клиент здесь НЕ считает ничего: какая доля каким днём войдёт в затраты, почему оплатить нельзя и
 * чем счёт заперт — всё приходит словами и числами сервера. Форма, посчитавшая перенос сама, однажды
 * пообещала бы одно, а записано было бы другое.
 */

/** Доля внутри строки расклада: что именно легло на стройку. */
export interface PostingPart {
  label: string;
  amount: number | null;
}

/** Строка расклада — контур и день: стройка, статьи вне строек или неразнесённый остаток. */
export interface PostingRow {
  kind: 'construction' | 'articles' | 'remainder';
  constructionId: string | null;
  name: string;
  amount: number | null;
  accountingOn: string;
  /** Учётная дата не совпала с датой платежа: период был закрыт. */
  moved: boolean;
  /** Почему перенесено — словами сервера. */
  note: string | null;
  parts: PostingPart[];
}

export interface PaymentPosting {
  /** «Сегодня» по часам компании — умолчание даты платежа; часы браузера могут стоять в другом поясе. */
  today: string;
  paidOn: string;
  total: number | null;
  /** Почему оплатить нельзя при любой дате; расклада тогда нет. */
  refusal: string | null;
  /** Почему нельзя оплатить этой датой; расклада тогда тоже нет. */
  dateRefusal: string | null;
  rows: PostingRow[];
  /** Отметка расклада — её возвращает запись оплаты (`seen`). */
  stamp: string;
}

/**
 * Предпросмотр расклада. Мутацией, а не запросом: это вопрос «что будет, если», его не кэшируют —
 * между двумя нажатиями период могли закрыть.
 */
export function usePaymentPreview() {
  return useMutation({
    mutationFn: ({ id, paidOn }: { id: string; paidOn: string | null }) =>
      apiClient.post<PaymentPosting>(`/costs/invoices/${id}/paid/preview`, { paidOn }).then(r => r.data),
  });
}

/**
 * Первый предпросмотр при открытии диалога — без даты: «сегодня» называет сервер. Запросом, а не
 * мутацией в эффекте, и без кэша: расклад, оставшийся с прошлого открытия, был бы ответом на вчерашний
 * вопрос.
 */
export function useFirstPaymentPreview(id: string) {
  return useQuery({
    // Ключ — ВНЕ семейства счетов: запись оплаты обновляет всё семейство, и предпросмотр, спрошенный
    // заново у только что оплаченного счёта, получал бы отказ «уже оплачен».
    queryKey: ['costs-payment-preview', id] as const,
    queryFn: () => apiClient.post<PaymentPosting>(`/costs/invoices/${id}/paid/preview`, { paidOn: null })
      .then(r => r.data),
    gcTime: 0,
    staleTime: 0,
    retry: false,
  });
}

/** Записанный расклад оплаченного счёта — тем же видом, что предпросмотр. */
export function usePostedPayment(id: string, enabled: boolean) {
  return useQuery({
    queryKey: [QK, id, 'paid'] as const,
    queryFn: () => apiClient.get<PaymentPosting>(`/costs/invoices/${id}/paid`).then(r => r.data),
    enabled,
  });
}

/**
 * Отметить оплату. `seen` — отметка расклада, который человек видел: разошлась — отказ 409.
 * `version` — версия счёта (issue #1176); у оплаты она названа так, потому что `seen` здесь уже занято.
 */
export function usePayInvoice() {
  return useInvoiceWrite(({ id, version, paidOn, document, seen }: {
    id: string; version: string; paidOn: string; document: string | null; seen: string;
  }) => apiClient.post<InvoiceView>(`/costs/invoices/${id}/paid`, { paidOn, document, seen }, seenBy(version))
    .then(r => r.data));
}

/** Поправить платёжный документ. Дату так не меняют — только отменой и новой отметкой. */
export function useDescribePayment() {
  return useInvoiceWrite(({ id, version, document }: { id: string; version: string; document: string | null }) =>
    apiClient.put<InvoiceView>(`/costs/invoices/${id}/paid`, { document }, seenBy(version)).then(r => r.data));
}

export function useCancelPayment() {
  return useInvoiceWrite(({ id, version, reason }: { id: string; version: string; reason: string }) =>
    apiClient.post<InvoiceView>(`/costs/invoices/${id}/unpaid`, { reason }, seenBy(version)).then(r => r.data));
}
