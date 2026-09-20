import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Журнал действий (issue #950, ТЗ CORE-28) — только чтение.
 *
 * Записывающих вызовов здесь нет и не будет: журнал пишет сервер там, где происходит само
 * действие. Возможность дописать запись из браузера означала бы журнал, в который можно положить
 * что угодно от чьего угодно имени.
 */

export interface ActivityRecord {
  id: string;
  occurredAt: string;
  /** Код действия — им же отбирают. */
  action: string;
  /** Название для человека; у записи с незнакомым кодом совпадает с кодом. */
  actionTitle: string;
  actorId: string | null;
  /** Имя автора на момент действия. Учётной записи может уже не быть — имя всё равно читается. */
  actorName: string;
  target: string | null;
  before: string | null;
  after: string | null;
}

export interface ActivityPage {
  /** Сколько записей отвечает отбору: список показывает часть, и врать про конец нельзя. */
  total: number;
  items: ActivityRecord[];
}

export interface ActivityActionOption {
  code: string;
  title: string;
}

export function useActivity(skip: number, take: number, action: string | null) {
  return useQuery({
    queryKey: ['activity', skip, take, action],
    queryFn: () => apiClient
      .get<ActivityPage>('/activity', { params: { skip, take, action: action || undefined } })
      .then(r => r.data),
  });
}

/** Список действий для отбора — из каталога сервера, а не из того, что уже записано. */
export function useActivityActions() {
  return useQuery({
    queryKey: ['activity', 'actions'],
    queryFn: () => apiClient.get<ActivityActionOption[]>('/activity/actions').then(r => r.data),
    staleTime: Infinity,
  });
}
