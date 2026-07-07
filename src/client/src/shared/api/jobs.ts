import { useEffect, useRef } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

/** Активная фоновая задача (Queued/Running) текущего пользователя — для индикатора. */
export interface ActiveJob {
  id: string;
  kind: string;
  /** Цель задачи — для распознавания это id источника (sourceId). */
  targetId: string;
  status: 'Queued' | 'Running';
  title: string;
  progress: string | null;
  createdAt: string;
  startedAt: string | null;
}

/** Виды задач распознавания PDF-источника (для блокировки повторного запуска по этому источнику). */
const RECOGNITION_KINDS = ['RecognizeGostSet', 'RecognizeDocument', 'RecognizeTable'];

/**
 * Активные фоновые задачи пользователя (источник данных индикатора). Поллит `/jobs/active`: часто, пока
 * есть активные; редко в простое (ловит задачи, запущенные в других вкладках — сессионность). При
 * ЗАВЕРШЕНИИ задачи (была активной, пропала из списка) инвалидирует наборы данных и уведомления —
 * результат распознавания подтягивается, а итог всплывает в колокольчике (handoff). Монтировать один
 * раз (в AppShell) — это и поллер, и точка инвалидации.
 */
export function useActiveJobs(): ActiveJob[] {
  const qc = useQueryClient();
  const prevIds = useRef<Set<string>>(new Set());

  const query = useQuery<ActiveJob[]>({
    queryKey: ['jobs', 'active'],
    queryFn: () => apiClient.get('/jobs/active').then(r => r.data as ActiveJob[]),
    refetchInterval: q => ((q.state.data?.length ?? 0) > 0 ? 2000 : 10000),
    refetchOnWindowFocus: true,
  });

  const jobs = query.data;
  useEffect(() => {
    const current = new Set((jobs ?? []).map(j => j.id));
    let completed = false;
    for (const id of prevIds.current) if (!current.has(id)) { completed = true; break; }
    if (completed) {
      qc.invalidateQueries({ queryKey: ['datasets', 'files'] });
      qc.invalidateQueries({ queryKey: ['notifications'] });
    }
    prevIds.current = current;
  }, [jobs, qc]);

  return jobs ?? [];
}

/**
 * Идёт ли по данному источнику активная задача распознавания — для блокировки повторного запуска
 * (кнопка «Распознать»). Читает тот же кэш `['jobs','active']`, что и индикатор (общий поллинг), без
 * side-эффектов инвалидции (они живут в единственном useActiveJobs в AppShell).
 */
export function useSourceRecognizing(sourceId: string): boolean {
  const { data } = useQuery<ActiveJob[]>({
    queryKey: ['jobs', 'active'],
    queryFn: () => apiClient.get('/jobs/active').then(r => r.data as ActiveJob[]),
    refetchInterval: q => ((q.state.data?.length ?? 0) > 0 ? 2000 : 10000),
  });
  return (data ?? []).some(j => j.targetId === sourceId && RECOGNITION_KINDS.includes(j.kind));
}

/** Отмена задачи из очереди (только Queued — 409 для выполняемых). После — обновляем список активных. */
export function useCancelJob() {
  const qc = useQueryClient();
  return useMutation<void, unknown, string>({
    mutationFn: id => apiClient.post(`/jobs/${id}/cancel`).then(() => undefined),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['jobs', 'active'] }),
  });
}
