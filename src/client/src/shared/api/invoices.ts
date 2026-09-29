import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Счета модуля «Счета и накладные» (задача C1 этапа 2, issue #1076, ТЗ COST-6.2).
 *
 * ⚠️ Реквизиты уезжают и приезжают ОБЪЕКТОМ «ключ поля: значение» — тем же, что отдаёт сервер, без
 * изъятий. Поля, которые ведёт код (состояния, скан), в этом объекте есть, и присылать их обратно
 * не только можно, но и нужно: правило сервера — «присылать можно, менять нельзя», и сравнивает он
 * значения, а не наличие ключа. Вырезав их, клиент получил бы отказ охраны записи ядра —
 * отсутствующее запертое поле она читает как стёртое.
 */
export interface InvoiceRequisites {
  [key: string]: unknown;
}

export interface InvoiceDuplicate {
  id: string;
  number: string | null;
  issuedOn: string | null;
  total: number | null;
}

export interface InvoiceView {
  id: string;
  documentTypeId: string;
  requisites: InvoiceRequisites;
  /** Ключи полей, заполненных распознаванием и не подтверждённых человеком. */
  unconfirmed: string[];
  duplicates: InvoiceDuplicate[];
  createdAt: string;
  updatedAt: string;
}

export interface InvoiceListItem {
  id: string;
  number: string | null;
  issuedOn: string | null;
  supplierId: string | null;
  /** ⚠️ `null` при заполненном `supplierId` означает ПОТЕРЮ: ссылка есть, записи нет. */
  supplierName: string | null;
  total: number | null;
  state: string;
  payment: string;
  dueDate: string | null;
  purpose: string | null;
  unconfirmedCount: number;
  hasScan: boolean;
}

export interface CostsOrganization {
  id: string;
  name: string;
  type: string;
}

const QK = 'costs-invoices';

export const INVOICES_KEY = [QK] as const;

export function useInvoices() {
  return useQuery({
    queryKey: INVOICES_KEY,
    queryFn: () => apiClient.get<InvoiceListItem[]>('/costs/invoices').then(r => r.data),
  });
}

export function useInvoice(id: string | undefined) {
  return useQuery({
    queryKey: [QK, id],
    queryFn: () => apiClient.get<InvoiceView>(`/costs/invoices/${id}`).then(r => r.data),
    enabled: !!id,
  });
}

/** Организации для выбора поставщика и плательщика — узкий список модуля. */
export function useCostsOrganizations() {
  return useQuery({
    queryKey: ['costs-organizations'],
    queryFn: () => apiClient.get<CostsOrganization[]>('/costs/organizations').then(r => r.data),
  });
}

export function useCreateInvoice() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (requisites: InvoiceRequisites) =>
      apiClient.post<InvoiceView>('/costs/invoices', { requisites }).then(r => r.data),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: INVOICES_KEY }); },
  });
}

export function useUpdateInvoice() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, requisites }: { id: string; requisites: InvoiceRequisites }) =>
      apiClient.put<InvoiceView>(`/costs/invoices/${id}`, { requisites }).then(r => r.data),
    onSuccess: view => {
      qc.setQueryData([QK, view.id], view);
      void qc.invalidateQueries({ queryKey: INVOICES_KEY });
    },
  });
}

/**
 * «Всё верно» по блоку: снять метки с названных полей.
 *
 * ⚠️ Поля перечисляет КЛИЕНТ, и пустой список сервер отвергает. Это не формальность: «снять все» —
 * самое дорогое прочтение промаха, метки исчезли бы разом и вернуть их было бы нечем.
 */
export function useConfirmInvoiceFields() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, fields }: { id: string; fields: string[] }) =>
      apiClient.post<InvoiceView>(`/costs/invoices/${id}/confirmed`, { fields }).then(r => r.data),
    onSuccess: view => {
      qc.setQueryData([QK, view.id], view);
      void qc.invalidateQueries({ queryKey: INVOICES_KEY });
    },
  });
}

export function useAttachInvoiceScan() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, file }: { id: string; file: File }) => {
      const form = new FormData();
      form.append('file', file);
      return apiClient.post<InvoiceView>(`/costs/invoices/${id}/scan`, form).then(r => r.data);
    },
    onSuccess: view => {
      qc.setQueryData([QK, view.id], view);
      void qc.invalidateQueries({ queryKey: INVOICES_KEY });
    },
  });
}

/**
 * Скан счёта — объектной ссылкой для показа.
 *
 * ⚠️ Через адрес МОДУЛЯ, а не через общий адрес вложений: у файла в хранилище прав нет, а у этого
 * адреса есть — `costs.invoice.read`. Общий адрес закрыт другим правом (`core.files.use`), и счёт
 * читался бы тем, у кого прав на счета нет вовсе.
 */
export async function loadInvoiceScan(id: string): Promise<{ url: string; mimeType: string }> {
  const response = await apiClient.get(`/costs/invoices/${id}/scan`, { responseType: 'blob' });
  const declared = (response.headers['content-type'] as string | undefined) ?? '';
  const mimeType = declared.split(';')[0].trim().toLowerCase() || 'application/octet-stream';
  return { url: URL.createObjectURL(new Blob([response.data as BlobPart], { type: mimeType })), mimeType };
}
