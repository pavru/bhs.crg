import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';

/** Где лежит запись, экран которой свой на каждом уровне (набор данных) — как у поиска палитры. */
export interface RecentLevel {
  scope: string;
  scopeId: string | null;
  constructionId: string | null;
}

/** Недавняя запись (shell-08, ТЗ UI-35) — как её отдаёт `GET /api/account/recent`. */
export interface RecentRecord {
  /** Код типа записи — ключ `RECORD_ROUTES`. */
  recordType: string;
  /** Группа поиска и её название — раздел записи: «Счета», «Стройки». */
  group: string;
  groupTitle: string;
  module: string;
  id: string;
  title: string;
  detail: string | null;
  /** Стройка записи, если владелец её называет. */
  place: string | null;
  amount: number | null;
  level: RecentLevel | null;
  openedAt: string;
}

/** Группа, которая не ответила: её записей в списке нет, и это сказано. */
export interface RecentRefusal {
  group: string;
  title: string;
  reason: string;
}

export interface RecentRecords {
  items: RecentRecord[];
  refused: RecentRefusal[];
}

/**
 * «Недавние» Главной — свои записи, новые первыми, уже отсеянные сервером: удалённых и закрытых правами в
 * ответе нет. Свежесть — `staleTime: 0`, как у очередей: отметки шлют экраны записей, и каждый заход на
 * Главную показывает последнее открытое.
 */
export const RECENT_KEY = ['account', 'recent'] as const;

export function useRecentRecords() {
  return useQuery({
    queryKey: RECENT_KEY,
    queryFn: () => apiClient.get<RecentRecords>('/account/recent').then(r => r.data),
    retry: false,
    staleTime: 0,
  });
}
