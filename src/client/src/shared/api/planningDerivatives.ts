import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import type { OfferStatus } from './planningOfferTypes';

export const DERIVATIVES_KEY = ['planning', 'estimates', 'derivatives'] as const;

export interface RevisionDerivative {
  id: string;
  number: string;
  currentVersion: number;
  status: string;
  date: string;
  sentOn: string | null;
  coverage: { covered: number; total: number; percent: number | null };
  outdated: boolean;
  historical: boolean;
}

type DerivativeBlock<Item> = { state: 'hidden'; permission: string } | { state: 'ok'; items: Item[] };

export interface RevisionDerivatives {
  revisionId: string;
  number: number;
  offers: DerivativeBlock<RevisionDerivative & { status: OfferStatus }>;
  schedules: DerivativeBlock<RevisionDerivative>;
}

export function useEstimateDerivatives(estimateId: string, enabled: boolean) {
  return useQuery({
    queryKey: [...DERIVATIVES_KEY, estimateId], enabled, retry: false,
    queryFn: ({ signal }) => apiClient.get<{ revisions: RevisionDerivatives[] }>(`/plan/estimates/${estimateId}/derivatives`, { signal }).then(r => r.data),
  });
}
