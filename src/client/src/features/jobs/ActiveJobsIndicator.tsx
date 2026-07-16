import { useEffect, useState, type ComponentType } from 'react';
import * as Popover from '@radix-ui/react-popover';
import { Loader2, X, ScanText, FileText, Layers, Activity } from 'lucide-react';
import { useActiveJobs, useCancelJob, type ActiveJob } from '@/shared/api/jobs';

/** Иконка типа задачи по kind (для аватара-статуса). */
function kindIcon(kind: string): ComponentType<{ size?: number; className?: string }> {
  if (/recogni|распозн/i.test(kind)) return ScanText;
  if (/generat|генер/i.test(kind)) return FileText;
  if (/assembl|сбор|kit/i.test(kind)) return Layers;
  return Activity;
}

/** Прошедшее время задачи m:ss (от старта, иначе от постановки). */
function elapsed(job: ActiveJob, now: number): string {
  const start = new Date(job.startedAt ?? job.createdAt).getTime();
  const s = Math.max(0, Math.floor((now - start) / 1000));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

/** Тикающее «сейчас» (раз в секунду) для живого таймера — только пока индикатор смонтирован (есть задачи). */
function useNow(): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, []);
  return now;
}

/**
 * Индикатор активных фоновых задач сессии (issue #188, MD3) — чип слева от колокольчика, видим только при
 * активных задачах. «Идёт сейчас» (transient, спиннер) — в отличие от колокольчика справа («уже случилось»,
 * persist, статусы). По завершении задача уходит отсюда и всплывает в колокольчике (handoff). Поэтому
 * панель показывает ТОЛЬКО живые задачи — без done/failed/clear_all/retry из макета (это роль колокольчика).
 * Клик — Radix-поповер со списком, аватаром-статусом и живым elapsed-таймером.
 */
export function ActiveJobsIndicator() {
  const jobs = useActiveJobs();
  if (jobs.length === 0) return null;

  const label = jobs.length === 1 ? jobs[0].title : `Задач выполняется: ${jobs.length}`;

  return (
    <Popover.Root>
      <Popover.Trigger asChild>
        <button
          type="button"
          aria-label={`Активные задачи: ${jobs.length}`}
          className="flex items-center gap-2 h-9 pl-3 pr-3.5 rounded-full text-sm font-medium text-fg2 hover:bg-muted transition-colors data-[state=open]:bg-brand-subtle data-[state=open]:text-brand"
        >
          <Loader2 size={16} className="animate-spin motion-reduce:animate-none shrink-0" />
          <span className="max-w-[180px] truncate">{label}</span>
        </button>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content
          align="end"
          sideOffset={8}
          role="status"
          className="w-96 max-w-[calc(100vw-2rem)] rounded-2xl border border-stroke bg-surface z-50 overflow-hidden focus:outline-none"
          style={{ boxShadow: 'var(--f-shadow16)' }}
        >
          <div className="flex items-center gap-2 pl-5 pr-2 py-2.5 border-b border-stroke">
            <span className="text-base font-medium text-fg1 flex-1">Фоновые задачи</span>
            <Popover.Close
              aria-label="Закрыть"
              className="flex items-center justify-center w-9 h-9 rounded-full text-fg4 hover:text-fg1 hover:bg-muted transition-colors"
            >
              <X size={18} />
            </Popover.Close>
          </div>
          <div className="max-h-[calc(100vh-9rem)] overflow-y-auto py-1">
            {jobs.map(job => <JobRow key={job.id} job={job} />)}
          </div>
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

function JobRow({ job }: { job: ActiveJob }) {
  const now = useNow();
  const cancel = useCancelJob();
  const queued = job.status === 'Queued';
  const Icon = kindIcon(job.kind);
  return (
    <div className="group flex items-center gap-3.5 pl-5 pr-3 py-3 hover:bg-muted/60 transition-colors">
      {/* Аватар-статус: иконка типа в круге + вращающийся спиннер-кольцо (indeterminate). */}
      <div className="relative w-10 h-10 shrink-0">
        <div className="absolute inset-0 rounded-full bg-muted flex items-center justify-center text-fg3">
          <Icon size={18} />
        </div>
        <Loader2 size={40} className="absolute inset-0 animate-spin motion-reduce:animate-none text-brand" strokeWidth={2} />
      </div>
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2">
          <p className="text-sm font-medium text-fg1 truncate flex-1">{job.title}</p>
          <span className="text-xs tabular-nums text-fg4 shrink-0">{elapsed(job, now)}</span>
        </div>
        <p className="text-[13px] text-fg4 truncate">
          {queued ? 'В очереди' : (job.progress ?? 'Выполняется…')}
        </p>
      </div>
      {/* Отмена — только пока задача в очереди; выполняемые добегают до конца. */}
      {queued && (
        <button
          type="button"
          onClick={() => cancel.mutate(job.id)}
          disabled={cancel.isPending}
          title="Отменить (задача ещё в очереди)"
          className="shrink-0 flex items-center justify-center w-9 h-9 rounded-full text-fg4 hover:text-danger hover:bg-black/5 dark:hover:bg-white/10 disabled:opacity-40 transition-colors"
        >
          <X size={16} />
        </button>
      )}
    </div>
  );
}
