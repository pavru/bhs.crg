import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { isAxiosError } from 'axios';
import { apiClient } from './client';
import { seenBy } from './invoices';
import { apiError } from '@/shared/utils/apiError';

const ROOT = '/costs/requests';
export const PURCHASE_REQUESTS = ['costs', 'purchase-requests'] as const;
export const REQUEST_READ = 'costs.request.read';
export const REQUEST_EDIT = 'costs.request.edit';

export type PurchaseStatus = 'draft' | 'submitted' | 'closed';
export interface PurchaseHeader {
  id: string; number: number; constructionId: string; version: string; status: PurchaseStatus;
  createdBy: string; createdAt: string; updatedAt: string;
}
export interface PurchaseListRow extends PurchaseHeader { lineCount: number }
export interface PurchasePage { items: PurchaseListRow[]; nextBeforeNumber: number | null }
export interface PurchaseLine {
  nomenclatureId: string; planItemId: string | null; quantity: number; unit: string; note: string | null;
}
export interface PurchaseQuantity {
  nomenclatureId: string; unit: string | null; orderedQuantity: number;
  invoicedQuantity: number | null; invoicedUnitMismatch: boolean | null;
  issuedForConstructionQuantity: number | null; issuedForConstructionUnitMismatch: boolean | null;
}
interface QuantityAccess { state: 'shown' | 'hidden'; anyOfPermissions: string[] }
export interface PurchaseQuantities {
  invoiced: QuantityAccess; issuedForConstruction: QuantityAccess; items: PurchaseQuantity[];
}
export interface PurchaseRequest extends PurchaseHeader {
  lines: PurchaseLine[];
  quantities: PurchaseQuantities | null;
  nomenclatureNames: Record<string, string | null> | null;
}
export interface PurchaseConstruction { id: string; name: string; sections: { id: string; name: string }[] }
export interface PurchasePlanItem { id: string; name: string | null; sectionId: string | null; unit: string | null }
export type PurchaseSource = { constructionId: string } & ({ sectionId: string } | { planItemIds: string[] });
export interface PurchaseProposalLine {
  nomenclatureId: string; planItemId: string | null; unit: string | null; estimateQuantity: number;
  compatibleIssuedQuantity: number | null; quantity: number; issuedExceeds: boolean; unitMismatch: boolean;
}
interface Hidden { state: 'hidden'; permission: string; message: string }
export type EstimateItems = Hidden | { state: 'shown'; items: PurchasePlanItem[] };
export type EstimateProposal = Hidden | {
  state: 'shown'; nomenclatureNames: Record<string, string | null>;
  suggestion: {
    lines: PurchaseProposalLine[]; unmatchedIssued: number; unmatchedEstimate: number;
    undecidedEstimate: number; warnings: string[];
  };
};
export type PurchaseWrite = { id: string; version: string } & (
  { action: 'lines'; lines: PurchaseLine[] } | { action: 'return'; reason: string }
  | { action: 'submit' | 'close' | 'delete' }
);

const itemKey = (id: string | undefined) => [...PURCHASE_REQUESTS, 'one', id] as const;

function fetchPage(constructionId?: string, beforeNumber?: number, status?: PurchaseStatus, signal?: AbortSignal) {
  return apiClient.get<PurchasePage>(ROOT, { params: { constructionId, beforeNumber, status }, signal }).then(r => r.data);
}

export function usePurchaseList(constructionId: string | undefined, allowed: boolean) {
  return useInfiniteQuery({
    queryKey: [...PURCHASE_REQUESTS, 'list', constructionId],
    queryFn: ({ pageParam, signal }) => fetchPage(constructionId, pageParam, undefined, signal),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: page => page.nextBeforeNumber ?? undefined,
    enabled: allowed,
  });
}

export function useSubmittedPurchases(constructionId: string | undefined, allowed: boolean) {
  return useQuery({
    queryKey: [...PURCHASE_REQUESTS, 'submitted', constructionId],
    queryFn: async ({ signal }) => {
      const rows: PurchaseListRow[] = [];
      let next: number | undefined;
      do {
        const page = await fetchPage(constructionId, next, 'submitted', signal);
        rows.push(...page.items);
        next = page.nextBeforeNumber ?? undefined;
      } while (next !== undefined);
      return rows;
    },
    enabled: allowed && !!constructionId,
  });
}

export function usePurchaseRequest(id: string | undefined, allowed = true) {
  return useQuery({
    queryKey: itemKey(id), enabled: allowed && !!id, retry: false,
    queryFn: ({ signal }) => apiClient.get<PurchaseRequest>(`${ROOT}/${id}`, { signal }).then(r => r.data),
  });
}

export function usePurchaseConstructions(allowed: boolean) {
  return useQuery({
    queryKey: [...PURCHASE_REQUESTS, 'constructions'], enabled: allowed,
    queryFn: ({ signal }) => apiClient.get<PurchaseConstruction[]>(`${ROOT}/constructions`, { signal }).then(r => r.data),
  });
}

export function usePurchaseEstimateItems(constructionId: string, allowed: boolean) {
  return useQuery({
    queryKey: [...PURCHASE_REQUESTS, 'estimate-items', constructionId], enabled: allowed && !!constructionId,
    retry: false,
    queryFn: ({ signal }) => apiClient.get<EstimateItems>(`${ROOT}/estimate-items`, { params: { constructionId }, signal }).then(r => r.data),
  });
}

export function usePurchaseProposal() {
  return useMutation({
    mutationFn: (source: PurchaseSource) => apiClient.post<EstimateProposal>(`${ROOT}/draft-from-estimate`, source).then(r => r.data),
  });
}

export function useCreatePurchase() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: PurchaseSource & { lines: PurchaseLine[] }) => apiClient.post<PurchaseRequest>(ROOT, body).then(r => r.data),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: PURCHASE_REQUESTS }); },
  });
}

export function useWritePurchase() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (write: PurchaseWrite): Promise<void> => {
      const url = `${ROOT}/${write.id}`;
      const seen = seenBy(write.version);
      if (write.action === 'delete') await apiClient.delete(url, seen);
      else if (write.action === 'lines') await apiClient.put(`${url}/lines`, { lines: write.lines }, seen);
      else await apiClient.post(`${url}/${write.action}`, write.action === 'return' ? { reason: write.reason } : null, seen);
    },
    onSuccess: () => { void qc.invalidateQueries({ queryKey: PURCHASE_REQUESTS }); },
  });
}

const refusals = {
  400: 'Проверьте введённые данные.',
  403: 'Для действия не хватает права.',
  409: 'Состояние заявки изменилось. Перечитайте её.',
  422: 'Счёт и заявка не подходят для связи.',
} as const;

export function purchaseRefusal(error: unknown) {
  if (!isAxiosError(error)) return { conflict: false, message: apiError(error, 'Не удалось выполнить действие.') };
  const status = error.response?.status;
  return {
    conflict: status === 409,
    message: apiError({ response: error.response }, refusals[status as keyof typeof refusals] ?? 'Не удалось выполнить действие.'),
  };
}
