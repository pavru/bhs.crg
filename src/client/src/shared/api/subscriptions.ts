import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { CatalogScope } from './types';

export type SubscriptionScope = Extract<CatalogScope, 'Construction' | 'Section' | 'Set'>;

export interface Subscriber {
  id: string;
  userId: string;
  displayName: string;
  email: string | null;
  validEmail: boolean;
}

export interface Recipient {
  userId: string;
  displayName: string;
  email: string | null;
  validEmail: boolean;
}

/** Прямые подписчики уровня. */
export function useSubscribers(scope: SubscriptionScope, scopeId: string) {
  return useQuery({
    queryKey: ['subscriptions', scope, scopeId],
    queryFn: () => apiClient.get<Subscriber[]>('/subscriptions', { params: { scope, scopeId } }).then(r => r.data),
  });
}

/** Эффективные получатели (прямые + унаследованные с вышестоящих уровней). */
export function useRecipients(scope: SubscriptionScope, scopeId: string, enabled = true) {
  return useQuery({
    queryKey: ['subscriptions', scope, scopeId, 'recipients'],
    queryFn: () => apiClient.get<Recipient[]>('/subscriptions/recipients', { params: { scope, scopeId } }).then(r => r.data),
    enabled,
  });
}

export function useAddSubscriber() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (dto: { userId: string; scope: SubscriptionScope; scopeId: string }) =>
      apiClient.post<Subscriber>('/subscriptions', dto).then(r => r.data),
    onSuccess: (_d, v) => qc.invalidateQueries({ queryKey: ['subscriptions', v.scope, v.scopeId] }),
  });
}

export function useRemoveSubscriber() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id }: { id: string; scope: SubscriptionScope; scopeId: string }) =>
      apiClient.delete(`/subscriptions/${id}`).then(() => undefined),
    onSuccess: (_d, v) => qc.invalidateQueries({ queryKey: ['subscriptions', v.scope, v.scopeId] }),
  });
}
