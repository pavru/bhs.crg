import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Что система знает о версиях (issue #813).
 *
 * `releaseUrl`/`releaseNotes` приходят только администратору: остальным они ни к чему, а показывать
 * всё, что есть, — верный способ превратить полезное в фон.
 */
export interface UpdateStatus {
  installed: string;
  latest: string | null;
  updateAvailable: boolean;
  /** Когда проверка последний раз УДАЛАСЬ. null — ни разу: либо только поставили, либо не достучались. */
  lastCheckedAt: string | null;
  enabled: boolean;
  releaseUrl?: string | null;
  releaseNotes?: string | null;
  /** Ответ на явную проверку: состоялась ли она. null — просто читали известное. */
  justChecked?: boolean | null;
  /** Почему последняя попытка не удалась. */
  lastError?: string | null;
}

/**
 * Читает то, что уже узнала фоновая служба, — в сеть этот запрос не ходит.
 *
 * Зовётся с каждого экрана (подвал боковой панели), поэтому дешёвый и с длинным staleTime: проверка
 * идёт раз в шесть часов, чаще спрашивать нечего.
 */
export function useUpdateStatus(enabled = true, withNotes = false) {
  return useQuery<UpdateStatus>({
    // Ключ различает выдачи с заметками и без: иначе лёгкий ответ из подвала панели лёг бы в кеш
    // страницы настроек, и заметки там не появились бы вовсе.
    queryKey: ['system', 'update', withNotes ? 'notes' : 'brief'],
    queryFn: () => apiClient
      .get('/system/update', { params: withNotes ? { withNotes: true } : undefined })
      .then(r => r.data),
    staleTime: 15 * 60 * 1000,
    retry: false,
    enabled,
  });
}

/** Проверить сейчас (Admin) — иначе выключатель «включено» неопровержим до следующего цикла. */
export function useCheckUpdatesNow() {
  const qc = useQueryClient();
  return useMutation<UpdateStatus, Error, void>({
    mutationFn: () => apiClient.post('/system/update/check').then(r => r.data),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['system', 'update'] }); },
  });
}

export function useSaveUpdateSettings() {
  const qc = useQueryClient();
  return useMutation<void, Error, { enabled: boolean }>({
    mutationFn: (body) => apiClient.put('/system/update/settings', body).then(() => undefined),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['system', 'update'] }); },
  });
}
