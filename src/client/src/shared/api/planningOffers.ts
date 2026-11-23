import { useInfiniteQuery, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { apiError, withBlobErrorBody } from '@/shared/utils/apiError';
import { apiClient } from './client';
import { DERIVATIVES_KEY } from './planningDerivatives';
import type {
  CreateOfferInput, CreatePriceListOfferInput, Offer, OfferHeaderInput, OfferListItem, OfferMarkupsInput, OfferOutcome, OfferRebaseLine,
  OfferRebasePreview, OfferVersion, PriceListRepricePreview,
} from './planningOfferTypes';

const PATH = '/plan/offers';
const keys = {
  lists: ['planning', 'offers', 'list'] as const,
  detail: (id: string) => ['planning', 'offers', 'detail', id] as const,
  versions: (id: string) => ['planning', 'offers', 'versions', id] as const,
};
const headers = (version: string) => ({ 'If-Match': version });

export function useOffers() {
  return useInfiniteQuery({
    queryKey: keys.lists,
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam, signal }) => apiClient.get<{ items: OfferListItem[]; next?: string }>(PATH, {
      params: { cursor: pageParam }, signal,
    }).then(r => r.data),
    getNextPageParam: page => page.next,
  });
}

export function useOffer(id: string) {
  return useQuery({
    queryKey: keys.detail(id),
    queryFn: ({ signal }) => apiClient.get<Offer>(`${PATH}/${id}`, { signal }).then(r => r.data),
  });
}

export function useOfferVersions(id: string, enabled: boolean) {
  return useQuery({
    queryKey: keys.versions(id),
    queryFn: ({ signal }) => apiClient.get<OfferVersion[]>(`${PATH}/${id}/versions`, { signal }).then(r => r.data),
    enabled,
  });
}

async function storeOffer(qc: QueryClient, offer: Offer, estimateChanged: boolean) {
  const key = keys.detail(offer.id);
  await qc.cancelQueries({ queryKey: key });
  qc.setQueryData(key, offer);
  void qc.invalidateQueries({ queryKey: keys.lists });
  void qc.invalidateQueries({ queryKey: keys.versions(offer.id) });
  if (estimateChanged) void qc.invalidateQueries({ queryKey: ['planning', 'estimates'] });
  else if (offer.source === 'estimate') void qc.invalidateQueries({ queryKey: DERIVATIVES_KEY });
}

export function useCreateOffer() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateOfferInput) => apiClient.post<Offer>(PATH, input).then(r => r.data),
    onSuccess: offer => storeOffer(qc, offer, true),
  });
}

export function useCreatePriceListOffer() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: CreatePriceListOfferInput) => apiClient.post<Offer>(`${PATH}/from-pricelist`, input).then(r => r.data),
    onSuccess: offer => storeOffer(qc, offer, false),
  });
}

export function usePriceListReprice(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ version, date, previewToken }: { version: string; date: string; previewToken?: string }) =>
      apiClient.post<PriceListRepricePreview>(`${PATH}/${id}/reprice-from-pricelist`, {
        date, preview: previewToken === undefined, previewToken,
      }, { headers: headers(version) }).then(r => r.data),
    onSuccess: result => result.offer ? storeOffer(qc, result.offer, false) : undefined,
  });
}

export type OfferCommand =
  | { type: 'header'; input: OfferHeaderInput }
  | { type: 'markups'; input: OfferMarkupsInput }
  | { type: 'send'; sentOn: string }
  | { type: 'outcome'; status: OfferOutcome }
  | { type: 'next' }
  | { type: 'rebase'; fingerprint: string; revisionId: string | null };

export async function writeOffer(id: string, version: string, command: OfferCommand): Promise<Offer> {
  const path = `${PATH}/${id}`;
  const config = { headers: headers(version) };
  switch (command.type) {
    case 'header': return (await apiClient.put<Offer>(path, command.input, config)).data;
    case 'markups': return (await apiClient.put<Offer>(`${path}/markups`, command.input, config)).data;
    case 'send': return (await apiClient.post<Offer>(`${path}/send`, { sentOn: command.sentOn }, config)).data;
    case 'outcome': return (await apiClient.post<Offer>(`${path}/outcome`, { status: command.status }, config)).data;
    case 'next': return (await apiClient.post<Offer>(`${path}/versions`, undefined, config)).data;
    case 'rebase': return (await apiClient.post<{ offer: Offer; removed: OfferRebaseLine[] }>(`${path}/rebase`, {
      revisionId: command.revisionId, fingerprint: command.fingerprint,
    }, config)).data.offer;
  }
}

export function useWriteOffer(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ version, command }: { version: string; command: OfferCommand }) => writeOffer(id, version, command),
    onSuccess: (offer, { command }) => storeOffer(qc, offer, command.type === 'rebase'),
  });
}

export async function readOfferPreview(id: string, revisionId?: string, signal?: AbortSignal) {
  return (await apiClient.get<OfferRebasePreview>(`${PATH}/${id}/rebase-preview`, { params: { revisionId }, signal })).data;
}

export async function previewOfferPrint(id: string, signal?: AbortSignal) {
  const path = `/print/${encodeURIComponent('КоммерческоеПредложение')}/${id}/preview`;
  const response = await withBlobErrorBody(apiClient.post<Blob>(path, undefined, {
    signal, responseType: 'blob',
  }));
  return response.data;
}

export type OfferRefusal =
  | { kind: 'preview-changed'; message: string; preview: OfferRebasePreview }
  | { kind: 'validation' | 'forbidden' | 'conflict' | 'other'; message: string };

export function offerRefusal(error: unknown, fallback: string): OfferRefusal {
  const response = (error as { response?: { status?: number; data?: { preview?: OfferRebasePreview } } } | null)?.response;
  if (response?.status === 403) return { kind: 'forbidden', message: 'У вас нет права выполнить это действие с КП.' };
  const message = apiError(error, fallback);
  if (response?.status === 400) return { kind: 'validation', message };
  if (response?.status === 409) {
    const preview = response.data?.preview;
    if (preview?.version && preview.fingerprint && preview.source?.id && preview.target?.id &&
      [preview.added, preview.removed, preview.changed, preview.unmatched, preview.unmatchedSectionMarkups].every(Array.isArray)) {
      return { kind: 'preview-changed', message, preview };
    }
    return { kind: 'conflict', message };
  }
  return { kind: 'other', message };
}
