import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { INVOICES_KEY } from './invoices';

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
 * Условия перечитываются вместе со счетами: открытый счёт говорит у поля «Отсрочка», совпадает ли она
 * с условиями его поставщика, — и после правки условий говорил бы по старым.
 */
function useTermsChanged() {
  const qc = useQueryClient();
  return () => {
    void qc.invalidateQueries({ queryKey: INVOICES_KEY });
    return qc.invalidateQueries({ queryKey: TERMS_KEY });
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
    onSettled: changed,
  });
}

/** Снять условия. Называет версию, которую видел человек. */
export function useRemoveSupplierTerms() {
  const changed = useTermsChanged();
  return useMutation({
    mutationFn: (item: SupplierTermsItem) =>
      apiClient.delete(`/costs/supplier-terms/${item.supplierId}`, { headers: { 'If-Match': item.version } }),
    onSettled: changed,
  });
}
