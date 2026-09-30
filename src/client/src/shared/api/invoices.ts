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

/**
 * Строка счёта (C2, issue #1078, ТЗ COST-7).
 *
 * ⚠️ Пустое `nomenclatureName` при заполненном `nomenclatureId` потерей НЕ является — потерю называет
 * `nomenclatureLost`, и считает её сервер. Пустым имя бывает у законной записи справочника без имени
 * (такие в базе есть) и там, где справочника нет вовсе; выведи мы потерю из пустоты — законная позиция
 * краснела бы «не найдена» сразу после выбора, а снять такую ссылку было нечем (ревью PR #1117).
 *
 * ⚠️ Потеря отличается от «строка ждёт позиции» ровно наличием ссылки, и путать их нельзя: первое
 * чинит справочник, второе — человек за формой.
 */
export interface InvoiceLineView {
  id: string;
  ordinal: number;
  nomenclatureId: string | null;
  nomenclatureName: string | null;
  nomenclatureLost: boolean;
  supplierText: string | null;
  supplierCode: string | null;
  unit: string | null;
  quantity: number | null;
  price: number | null;
  vatRate: number | null;
  vatAmount: number | null;
  amount: number | null;
  note: string | null;
}

/** Сверка суммы строк с суммой к оплате и счётчик ждущих позицию (ТЗ COST-6.2). */
export interface InvoiceLineTotals {
  count: number;
  withoutNomenclature: number;
  amount: number;
  vat: number;
}

export interface InvoiceView {
  id: string;
  documentTypeId: string;
  requisites: InvoiceRequisites;
  /** Ключи полей, заполненных распознаванием и не подтверждённых человеком. */
  unconfirmed: string[];
  duplicates: InvoiceDuplicate[];
  lines: InvoiceLineView[];
  totals: InvoiceLineTotals;
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
  linesCount: number;
  /** Сколько строк ждёт позиции номенклатуры — счётчик «Разобрать». */
  linesWithoutNomenclature: number;
}

export interface CostsOrganization {
  id: string;
  name: string;
  type: string;
}

/** Позиция номенклатуры в выборе строки. */
export interface NomenclatureItem {
  id: string;
  name: string | null;
  type: string;
}

/**
 * Найденное и оговорка о неполноте.
 *
 * ⚠️ `more` обязана доехать до человека словами. Неполный список, выданный за полный, читается как
 * «такой позиции нет» — и человек заводит вторую такую же позицию номенклатуры.
 */
export interface NomenclatureSearchResult {
  items: NomenclatureItem[];
  more: boolean;
}

const QK = 'costs-invoices';

export const INVOICES_KEY = [QK] as const;

/**
 * Реестр счетов. `needsParsing` — отбор «Разобрать»: счета, у которых есть строки без позиции.
 *
 * ⚠️ Отбор входит в ключ запроса. Без этого React Query отдал бы отобранному списку кэш полного (и
 * наоборот), и «счёт есть, а в списке его нет» стало бы поведением.
 */
export function useInvoices(needsParsing = false) {
  return useQuery({
    queryKey: [QK, 'list', needsParsing] as const,
    queryFn: () => apiClient
      .get<InvoiceListItem[]>('/costs/invoices', { params: needsParsing ? { needsParsing: true } : {} })
      .then(r => r.data),
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
 * Строки счёта — НАБОРОМ (C2, issue #1078): присланное и есть новое состояние.
 *
 * ⚠️ У сохранённых строк обязан уехать их `id`. Тот же набор без `id` означает для сервера «удали эти
 * строки и заведи новые»: на строку будет ссылаться разноска по количеству (F1), и потерянный `id`
 * рвал бы ссылку молча. Собирает набор `toPayload` — там же это правило и записано.
 */
export function useReplaceInvoiceLines() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, lines }: { id: string; lines: Record<string, unknown>[] }) =>
      apiClient.put<InvoiceView>(`/costs/invoices/${id}/lines`, { lines }).then(r => r.data),
    onSuccess: view => {
      qc.setQueryData([QK, view.id], view);
      void qc.invalidateQueries({ queryKey: INVOICES_KEY });
    },
  });
}

/**
 * «Разобран» и «вернуть в черновик» — переходы состояния (ТЗ COST-9).
 *
 * Одним хуком на два адреса: действие у них одно и то же по устройству — послать и перечитать, — а
 * различаются они словом в адресе. Два почти одинаковых хука расходились бы обработкой отказа.
 */
export function useInvoiceState() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, to }: { id: string; to: 'parsed' | 'draft' }) =>
      apiClient.post<InvoiceView>(`/costs/invoices/${id}/${to}`).then(r => r.data),
    onSuccess: view => {
      qc.setQueryData([QK, view.id], view);
      void qc.invalidateQueries({ queryKey: INVOICES_KEY });
    },
  });
}

/**
 * Поиск позиции номенклатуры для строки.
 *
 * ⚠️ Списком целиком справочник НЕ отдаётся: он самый большой в системе, и в данных записи лежат
 * картинки. Поэтому здесь поиск, ответ ограничен, а неполноту сервер называет признаком `more` —
 * показать его человеку обязана форма.
 */
export function useNomenclature(query: string) {
  return useQuery({
    queryKey: ['costs-nomenclature', query] as const,
    queryFn: () => apiClient
      .get<NomenclatureSearchResult>('/costs/nomenclature', { params: query ? { query } : {} })
      .then(r => r.data),
    // Прошлый ответ показываем, пока едет новый: иначе список мигает пустотой на каждой набранной
    // букве, и человек читает это как «ничего не нашлось».
    placeholderData: previous => previous,
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
