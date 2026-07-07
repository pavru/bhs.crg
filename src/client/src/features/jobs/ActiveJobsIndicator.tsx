import { useEffect, useState } from 'react';
import * as Popover from '@radix-ui/react-popover';
import { Loader2, X } from 'lucide-react';
import { useActiveJobs, useCancelJob, type ActiveJob } from '@/shared/api/jobs';

/** Русское склонение «N задача/задачи/задач». */
function jobsWord(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return 'задача';
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return 'задачи';
  return 'задач';
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
 * Индикатор активных фоновых задач сессии — пилюля слева от колокольчика, видима только при активных
 * задачах. «Идёт сейчас» (transient, нейтральный, спиннер, без badge серьёзности) — в отличие от
 * колокольчика справа («уже случилось», persist, цвета). По завершении задача уходит отсюда и всплывает
 * в колокольчике (handoff, см. useActiveJobs). Клик — Radix-поповер со списком и живым elapsed-таймером.
 */
export function ActiveJobsIndicator() {
  const jobs = useActiveJobs();
  if (jobs.length === 0) return null;

  const label = jobs.length === 1 ? jobs[0].title : `${jobs.length} ${jobsWord(jobs.length)}`;

  return (
    <Popover.Root>
      <Popover.Trigger asChild>
        <button
          type="button"
          aria-label={`Активные задачи: ${jobs.length}`}
          className="flex items-center gap-1.5 h-8 px-2.5 rounded-md text-xs text-fg2 hover:text-fg1 hover:bg-base transition-colors"
        >
          <Loader2 size={13} className="animate-spin motion-reduce:animate-none text-fg3 shrink-0" />
          <span className="max-w-[160px] truncate">{label}</span>
        </button>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content
          align="end"
          sideOffset={8}
          role="status"
          className="w-80 rounded-xl border border-stroke bg-surface shadow-lg z-50 overflow-hidden focus:outline-none"
        >
          <div className="px-4 py-2.5 border-b border-stroke text-sm font-semibold text-fg1">
            Выполняется
          </div>
          <div className="max-h-[60vh] overflow-y-auto divide-y divide-stroke">
            {jobs.map(job => (
              <JobRow key={job.id} job={job} />
            ))}
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
  return (
    <div className="group flex items-center gap-3 px-4 py-3">
      <Loader2 size={14} className="animate-spin motion-reduce:animate-none text-fg4 shrink-0" />
      <div className="flex-1 min-w-0">
        <p className="text-sm font-medium text-fg1 truncate">{job.title}</p>
        <p className="text-[11px] text-fg4">
          {queued ? 'В очереди' : (job.progress ?? 'Выполняется…')}
        </p>
      </div>
      <span className="text-xs tabular-nums text-fg3 shrink-0">{elapsed(job, now)}</span>
      {/* Отмена — только пока задача в очереди; выполняемые добегают до конца. */}
      {queued && (
        <button
          type="button"
          onClick={() => cancel.mutate(job.id)}
          disabled={cancel.isPending}
          title="Отменить (задача ещё в очереди)"
          className="shrink-0 p-1 rounded text-fg4 hover:text-danger disabled:opacity-40 transition-colors"
        >
          <X size={13} />
        </button>
      )}
    </div>
  );
}
