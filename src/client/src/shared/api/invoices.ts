import { useQuery, useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query';
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
  /** Разноска строки по стройкам и остаток «не разнесено» (F1, issue #1085). */
  allocation: LineAllocationView;
}

/**
 * Часть разноски строки (F1, issue #1085, ТЗ COST-10).
 *
 * ⚠️ `amount` у строки с количеством ПОСЧИТАН сервером (доля суммы строки), а не введён: форма его
 * только показывает. `rounding` и `discrepancy` не нуль у той части, в которую ушли копейки округления
 * и расхождение с суммой к оплате, — её форма помечает, иначе «33,34» среди «33,33» выглядело бы
 * опечаткой.
 */
export interface AllocationPartView {
  id: string;
  ordinal: number;
  /** Стройка; `null` — часть легла на статью вне строек (F3, issue #1087). */
  constructionId: string | null;
  constructionName: string | null;
  sectionId: string | null;
  sectionName: string | null;
  /** Статья вне строек — «Склад», «Общие расходы»; `null` — часть легла на стройку. Ровно одно из двух. */
  articleId: string | null;
  articleName: string | null;
  /** Стройку, раздел или статью удалили — потеря, а не «не выбрано». */
  targetLost: boolean;
  quantity: number | null;
  amount: number | null;
  rounding: number;
  discrepancy: number;
  /** Часть не того вида, что строка (у строки убрали количество) — не разносит ничего. */
  mismatched: boolean;
}

/**
 * Разноска строки. `mode` решает сервер по самой строке: есть количество — делится количество, нет —
 * сумма; форма не выбирает, иначе предлагала бы метры строке, которую сервер разносит рублями.
 */
export interface LineAllocationView {
  mode: 'quantity' | 'amount' | 'none';
  parts: AllocationPartView[];
  unallocatedQuantity: number | null;
  unallocatedAmount: number | null;
  balanced: boolean;
}

/** «Разнесён» — ровно то условие, которое проверяет переход «разобран» (ТЗ COST-9, COST-13). */
export interface AllocationSummaryView {
  allocated: boolean;
  /** Номера строк, разнесённых не полностью. */
  unbalanced: number[];
  /** Частей на удалённую стройку или раздел. */
  lost: number;
  /** Сумма к оплате минус сумма строк; `null` — суммы к оплате нет. */
  discrepancy: number | null;
  /** Допуск расхождения — приезжает от сервера: по ТЗ это настройка, повторять её в форме нельзя. */
  tolerance: number;
  withinTolerance: boolean;
  /** Разноска счёта целиком суммой — у счёта без строк (F2, ТЗ COST-11). */
  document: DocumentAllocationView;
  /** Отметка версии разноски — её присылает запись матрицы, чтобы устаревший набор был отвергнут. */
  stamp: string;
}

/**
 * Разноска счёта целиком суммой (F2, issue #1086). Живёт, пока строк нет; появились строки — ждёт
 * пересчёта (`pending`), и «разнесён» до него не наступает.
 */
export interface DocumentAllocationView {
  parts: AllocationPartView[];
  /** «Не разнесено» от суммы к оплате; `null` — у счёта есть строки или нет суммы к оплате. */
  unallocatedAmount: number | null;
  pending: boolean;
  balanced: boolean;
}

export interface CostsSection {
  id: string;
  name: string;
}

export interface CostsConstruction {
  id: string;
  name: string;
  sections: CostsSection[];
}

/** Сверка суммы строк с суммой к оплате и счётчик ждущих позицию (ТЗ COST-6.2). */
export interface InvoiceLineTotals {
  count: number;
  withoutNomenclature: number;
  amount: number;
  vat: number;
}

/**
 * Оплата в ответе счёта (C5, issue #1082). Всё здесь — слова и решения сервера: форма не выводит
 * «заперт» из границ периодов и «оплатить нельзя» из сверки сумм своей формулой.
 */
export interface PaymentView {
  paid: boolean;
  paidOn: string | null;
  /** Платёжный документ — необязательный текст. */
  document: string | null;
  paidAt: string | null;
  /** Почему счёт сейчас оплатить нельзя; у оплаченного — `null`. */
  refusal: string | null;
  /** Чем счёт заперт («период закрыт по … у стройки …»); `null` — не заперт. Признак запертого —
   *  только это поле, а не `paid`: оплаченный счёт открытого периода правится. */
  lockedBy: string | null;
  /** Учётные месяцы счёта — «09.2026», по возрастанию. */
  periods: string[];
}

export interface InvoiceView {
  id: string;
  /** Версия счёта: её называет каждая правка (`seen`), и устаревшая получает отказ 409 (issue #1176). */
  version: string;
  documentTypeId: string;
  requisites: InvoiceRequisites;
  /** Ключи полей, заполненных распознаванием и не подтверждённых человеком. */
  unconfirmed: string[];
  duplicates: InvoiceDuplicate[];
  lines: InvoiceLineView[];
  totals: InvoiceLineTotals;
  allocation: AllocationSummaryView;
  payment: PaymentView;
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
  /**
   * Альтернативное имя, по которому позиция найдена, — когда набранного нет в названии. Показывать
   * обязательно: позиция, в названии которой набранного нет, без пояснения выглядит ошибкой поиска.
   */
  matchedAlias: string | null;
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

export const QK = 'costs-invoices';

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

/** Чтение счёта — одно на обычный запрос и на перечитывание после отказа: ключ и адрес не разойдутся. */
function invoiceQuery(id: string | undefined) {
  return {
    queryKey: [QK, id] as const,
    queryFn: () => apiClient.get<InvoiceView>(`/costs/invoices/${id}`).then(r => r.data),
  };
}

export function useInvoice(id: string | undefined) {
  return useQuery({ ...invoiceQuery(id), enabled: !!id });
}

/**
 * Счёт, прочитанный СЕЙЧАС, — после отказа «счёт изменили» (issue #1176).
 *
 * Версия у счёта одна на все его части, и вид на экране мог отстать: сосед сохранил шапку минуту назад,
 * а кэш ещё прежний. Свежий вид отвечает на вопрос, который отказ оставил открытым: изменилась ли МОЯ
 * часть. Нет — запись повторяется со свежей версией (`useDraftBase`).
 */
export function useFreshInvoice() {
  const qc = useQueryClient();
  return (id: string) => qc.fetchQuery({ ...invoiceQuery(id), staleTime: 0 });
}

/** Организации для выбора поставщика и плательщика — узкий список модуля. */
export function useCostsOrganizations() {
  return useQuery({
    queryKey: ['costs-organizations'],
    queryFn: () => apiClient.get<CostsOrganization[]>('/costs/organizations').then(r => r.data),
  });
}

/**
 * Правка называет версию счёта, по которой собрана (issue #1176): заголовок `If-Match`. Без него
 * сервер отказывает 400, с устаревшей версией — 409 «счёт тем временем изменили».
 *
 * ⚠️ `seen` — версия вида, по которому собран ЧЕРНОВИК, а не «какая сейчас в кэше»: вид под формой
 * обновляется сам, и свежая подпись под прежним черновиком затёрла бы чужую правку. Какую версию
 * называть, решает форма (`useDraftBase`).
 */
export function seenBy(version: string) {
  return { headers: { 'If-Match': version } };
}

/**
 * Отказ 409 — счёт изменили: вид перечитывается сразу, чтобы форма узнала, ЧТО изменилось, и назвала
 * это человеку. Без перечитывания он увидел бы отказ над экраном, на котором всё по-прежнему.
 */
function rereadOnConflict(qc: QueryClient) {
  return (error: unknown, input: { id: string }) => {
    if ((error as { response?: { status?: number } })?.response?.status === 409)
      void qc.invalidateQueries({ queryKey: [QK, input.id] });
  };
}

/**
 * Правка счёта, возвращающая его вид: ответ кладётся в кэш, реестр перечитывается, отказ 409 перечитывает
 * счёт. Каждый пишущий хук счёта строится на этом — иначе следующий, написанный по образцу, забыл бы
 * перечитать после отказа, и отказ повис бы над экраном, на котором всё по-прежнему.
 */
export function useInvoiceWrite<T extends { id: string }>(send: (input: T) => Promise<InvoiceView>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: send,
    onSuccess: view => {
      qc.setQueryData([QK, view.id], view);
      void qc.invalidateQueries({ queryKey: INVOICES_KEY });
    },
    onError: rereadOnConflict(qc),
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
  return useInvoiceWrite(({ id, seen, requisites }: { id: string; seen: string; requisites: InvoiceRequisites }) =>
    apiClient.put<InvoiceView>(`/costs/invoices/${id}`, { requisites }, seenBy(seen)).then(r => r.data));
}

/**
 * «Всё верно» по блоку: снять метки с названных полей.
 *
 * ⚠️ Поля перечисляет КЛИЕНТ, и пустой список сервер отвергает. Это не формальность: «снять все» —
 * самое дорогое прочтение промаха, метки исчезли бы разом и вернуть их было бы нечем.
 */
export function useConfirmInvoiceFields() {
  return useInvoiceWrite(({ id, seen, fields }: { id: string; seen: string; fields: string[] }) =>
    apiClient.post<InvoiceView>(`/costs/invoices/${id}/confirmed`, { fields }, seenBy(seen)).then(r => r.data));
}

export function useAttachInvoiceScan() {
  return useInvoiceWrite(({ id, seen, file }: { id: string; seen: string; file: File }) => {
    const form = new FormData();
    form.append('file', file);
    return apiClient.post<InvoiceView>(`/costs/invoices/${id}/scan`, form, seenBy(seen)).then(r => r.data);
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
  return useInvoiceWrite(({ id, seen, lines }: { id: string; seen: string; lines: Record<string, unknown>[] }) =>
    apiClient.put<InvoiceView>(`/costs/invoices/${id}/lines`, { lines }, seenBy(seen)).then(r => r.data));
}

/** Стройки с разделами — цели разноски. Узким списком модуля, как организации. */
export function useCostsConstructions() {
  return useQuery({
    queryKey: ['costs-constructions'],
    queryFn: () => apiClient.get<CostsConstruction[]>('/costs/constructions').then(r => r.data),
  });
}

/**
 * Части разноски строки — НАБОРОМ (F1, issue #1085). Часть с `id` правится на месте.
 *
 * ⚠️ У строки с количеством уезжает `quantity`, без количества — `amount`, и никогда оба: сумму части
 * строки с количеством считает сервер, и присланная сумма была бы отвергнута.
 */
export function useReplaceAllocation() {
  return useInvoiceWrite(({ id, seen, lineId, parts }: {
    id: string; seen: string; lineId: string; parts: Record<string, unknown>[];
  }) =>
    apiClient.put<InvoiceView>(`/costs/invoices/${id}/lines/${lineId}/allocation`, { parts }, seenBy(seen))
      .then(r => r.data));
}

/**
 * «Разобран» и «вернуть в черновик» — переходы состояния (ТЗ COST-9).
 *
 * Одним хуком на два адреса: действие у них одно и то же по устройству — послать и перечитать, — а
 * различаются они словом в адресе. Два почти одинаковых хука расходились бы обработкой отказа.
 */
export function useInvoiceState() {
  return useInvoiceWrite(({ id, seen, to }: { id: string; seen: string; to: 'parsed' | 'draft' }) =>
    apiClient.post<InvoiceView>(`/costs/invoices/${id}/${to}`, null, seenBy(seen)).then(r => r.data));
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
