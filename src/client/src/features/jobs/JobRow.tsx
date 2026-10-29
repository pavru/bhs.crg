import { useEffect, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { Close, Description, DocumentScanner, Layers, Monitoring, type IconDef } from '@/shared/ui/icons';
import { useCancelJob, type ActiveJob } from '@/shared/api/jobs';
import { IconButton } from '@/shared/ui/Button';

/** Иконка типа задачи по kind (для аватара-статуса). */
function kindIcon(kind: string): IconDef {
  if (/recogni|распозн/i.test(kind)) return DocumentScanner;
  if (/generat|генер/i.test(kind)) return Description;
  if (/assembl|сбор|kit/i.test(kind)) return Layers;
  return Monitoring;
}

/** Прошедшее время задачи m:ss (от старта, иначе от постановки). */
function elapsed(job: ActiveJob, now: number): string {
  const start = new Date(job.startedAt ?? job.createdAt).getTime();
  const s = Math.max(0, Math.floor((now - start) / 1000));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

/** Тикающее «сейчас» (раз в секунду) для живого таймера — только пока строка смонтирована. */
function useNow(): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, []);
  return now;
}

/**
 * Строка живой фоновой задачи (issue #188) — в разделе «Выполняется» кнопки активности (#1431).
 * Только живые задачи: по завершении задача уходит отсюда, а итог приходит уведомлением (handoff).
 * Поэтому ни «готово», ни «повторить» здесь нет — это роль уведомлений.
 */
export function JobRow({ job }: { job: ActiveJob }) {
  const now = useNow();
  const cancel = useCancelJob();
  const queued = job.status === 'Queued';

  return (
    <div className="group flex items-center gap-3.5 pl-5 pr-3 py-3">
      {/* Аватар-статус: иконка типа в круге + вращающийся спиннер-кольцо (indeterminate). */}
      <div className="relative w-10 h-10 shrink-0">
        <div className="absolute inset-0 rounded-full bg-surface-container flex items-center justify-center text-on-surface-variant">
          <Icon icon={kindIcon(job.kind)} size={18} />
        </div>
        <CircularProgress size={40} className="absolute inset-0 text-primary" />
      </div>
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2">
          <p className="text-sm font-medium text-on-surface truncate flex-1">{job.title}</p>
          {/* Вне озвучки: раздел задач — живая область, и тикающий таймер диктор читал бы каждую секунду. */}
          <span aria-hidden="true" className="text-xs tabular-nums text-on-surface-variant shrink-0">{elapsed(job, now)}</span>
        </div>
        <p className="text-note text-on-surface-variant truncate">
          {queued ? 'В очереди' : (job.progress ?? 'Выполняется…')}
        </p>
      </div>
      {/* Отмена — только пока задача в очереди; выполняемые добегают до конца. */}
      {queued && (
        <IconButton label="Отменить задачу" title="Отменить (задача ещё в очереди)" danger
          onClick={() => cancel.mutate(job.id)} disabled={cancel.isPending}>
          <Icon icon={Close} size={18} />
        </IconButton>
      )}
    </div>
  );
}
