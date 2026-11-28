import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import { filenameFromContentDisposition, saveBlob } from './attachments';
import { withBlobErrorBody } from '@/shared/utils/apiError';
import type { CostsConstruction } from './invoices';

export function useEstimateVsCostsConstructions() {
  return useQuery({
    queryKey: ['costs-estimate-vs-costs-constructions'],
    queryFn: () => apiClient.get<CostsConstruction[]>('/costs/estimate-vs-costs/constructions').then(response => response.data),
  });
}

export interface EstimateCostCell {
  state: 'shown' | 'hidden' | 'not-connected' | 'not-in-estimate';
  amount?: number;
  basisUnknown: boolean;
}

export interface EstimateVsCostsRow {
  nomenclatureId: string | null;
  name: string;
  allocated: number;
  paid: number;
  allocatedVatUnknown: boolean;
  paidVatUnknown: boolean;
  allocatedIncomplete: boolean;
  paidIncomplete: boolean;
  estimate: EstimateCostCell;
  difference?: number;
}

export interface EstimateVsCostsView {
  constructionId: string;
  sectionId: string | null;
  construction: string;
  section: string | null;
  estimateState: 'shown' | 'hidden' | 'not-connected';
  permission: string | null;
  message: string | null;
  rows: EstimateVsCostsRow[];
  allocated: number;
  paid: number;
  undecided: number;
  estimateCurrency: string | null;
  factCurrency: string;
  currencyMismatch: boolean;
}

export interface EstimateVsCostsQuery {
  site: string | null;
  section: string | null;
  search: string;
  onlyExcess: boolean;
}

export function useEstimateVsCosts(query: EstimateVsCostsQuery) {
  return useQuery({
    queryKey: ['costs-estimate-vs-costs', query],
    queryFn: ({ signal }) => apiClient.get<EstimateVsCostsView>('/costs/estimate-vs-costs', {
      params: query, signal,
    }).then(response => response.data),
    enabled: !!query.site,
    staleTime: 0,
    refetchOnMount: 'always',
  });
}

export async function exportEstimateVsCosts(query: EstimateVsCostsQuery) {
  const response = await withBlobErrorBody(apiClient.get<Blob>('/costs/estimate-vs-costs/export', {
    params: query, responseType: 'blob',
  }));
  saveBlob(response.data, filenameFromContentDisposition(
    response.headers['content-disposition'] as string | undefined, 'План против факта.xlsx'));
}
