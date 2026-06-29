import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

export type NotificationSeverity = 'Info' | 'Warning' | 'Error';

export interface NotificationDto {
  id: string;
  severity: NotificationSeverity;
  title: string;
  message: string;
  source?: string | null;
  linkUrl?: string | null;
  linkLabel?: string | null;
  isRead: boolean;
  createdAt: string;
}

export interface NotificationsResponse {
  items: NotificationDto[];
  unreadCount: number;
}

export interface ComponentHealth {
  name: string;
  healthy: boolean;
  detail?: string | null;
  checkedAt: string;
}

const POLL_MS = 20_000;

export function useNotifications() {
  return useQuery({
    queryKey: ['notifications'],
    queryFn: () => apiClient.get<NotificationsResponse>('/notifications').then(r => r.data),
    refetchInterval: POLL_MS,
    refetchOnWindowFocus: true,
  });
}

export function useHealth() {
  return useQuery({
    queryKey: ['notifications', 'health'],
    queryFn: () => apiClient.get<ComponentHealth[]>('/notifications/health').then(r => r.data),
    refetchInterval: POLL_MS,
  });
}

function useNotificationMutation<T = void>(fn: (arg: T) => Promise<unknown>) {
  const qc = useQueryClient();
  return useMutation<unknown, unknown, T>({
    mutationFn: fn,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['notifications'] }),
  });
}

export function useMarkNotificationRead() {
  return useNotificationMutation<string>((id) => apiClient.post(`/notifications/${id}/read`));
}

export function useMarkAllNotificationsRead() {
  return useNotificationMutation<void>(() => apiClient.post('/notifications/read-all'));
}

export function useDismissNotification() {
  return useNotificationMutation<string>((id) => apiClient.delete(`/notifications/${id}`));
}

export function useClearNotifications() {
  return useNotificationMutation<void>(() => apiClient.delete('/notifications'));
}
