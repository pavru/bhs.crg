import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { isInvoiceView } from './invoices';

/**
 * Условия поставщика — отсрочка платежа в днях (ТЗ COST-9.2; задача C4, issue #1080). Подставляется в
 * счёт, когда у него выбран поставщик; заведённые счета смена условий не трогает.
 */
export interface SupplierTermsItem {
  supplierId: string;
  /** Версия записи: её называют правка и снятие заголовком `If-Match`. */
  version: string;
  supplierName: string | null;
  supplierArchived: boolean;
  /** Записи поставщика больше нет — имя взять неоткуда, и это не «без имени». */
  supplierLost: boolean;
  deferralDays: number;
  updatedAt: string;
  updatedBy: string | null;
}

const TERMS_KEY = ['costs', 'supplier-terms'] as const;

export function useSupplierTerms() {
  return useQuery({
    queryKey: TERMS_KEY,
    queryFn: () => apiClient.get<SupplierTermsItem[]>('/costs/supplier-terms').then(r => r.data),
  });
}

/**
 * Список условий перечитывается после любой попытки — и после отказа: 409 значит, что в окне устаревшее.
 * Счета — только после успеха и только ВИДЫ открытых счетов (ревью PR #1329): от условий зависит одна
 * подсказка у поля «Отсрочка», а списки счетов и числа очередей от них не зависят ничем.
 */
function useTermsChanged() {
  const qc = useQueryClient();
  return {
    onSuccess: () => void qc.invalidateQueries({ predicate: q => isInvoiceView(q.queryKey) }),
    onSettled: () => qc.invalidateQueries({ queryKey: TERMS_KEY }),
  };
}

/**
 * Записать условия. Без версии — ЗАВЕСТИ новые: у поставщика, у которого они уже есть, сервер ответит
 * 409, а не затрёт чужие. С версией — изменить те, что видел человек.
 */
export function useSetSupplierTerms() {
  const changed = useTermsChanged();
  return useMutation({
    mutationFn: ({ supplierId, deferralDays, version }: { supplierId: string; deferralDays: number; version?: string }) =>
      apiClient.put<SupplierTermsItem>(`/costs/supplier-terms/${supplierId}`, { deferralDays },
        version ? { headers: { 'If-Match': version } } : undefined).then(r => r.data),
    ...changed,
  });
}

/** Снять условия. Называет версию, которую видел человек. */
export function useRemoveSupplierTerms() {
  const changed = useTermsChanged();
  return useMutation({
    mutationFn: (item: SupplierTermsItem) =>
      apiClient.delete(`/costs/supplier-terms/${item.supplierId}`, { headers: { 'If-Match': item.version } }),
    ...changed,
  });
}
