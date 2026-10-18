import { createElement, useEffect, useState, type ComponentType } from 'react';
import { Loader2, X, ScanText, FileText, Layers, Activity } from 'lucide-react';
import { useCancelJob, type ActiveJob } from '@/shared/api/jobs';
import { IconButton } from '@/shared/ui/Button';

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
        <div className="absolute inset-0 rounded-full bg-muted flex items-center justify-center text-fg3">
          {/* Через createElement, а не <Icon/>: локальная переменная с большой буквы читается
              линтом как компонент, ОПРЕДЕЛЁННЫЙ в рендере, — а здесь это готовый компонент,
              выбранный из модульного набора по виду задачи (issue #858). */}
          {createElement(kindIcon(job.kind), { size: 18 })}
        </div>
        <Loader2 size={40} className="absolute inset-0 animate-spin motion-reduce:animate-none text-brand" strokeWidth={2} />
      </div>
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2">
          <p className="text-sm font-medium text-fg1 truncate flex-1">{job.title}</p>
          {/* Вне озвучки: раздел задач — живая область, и тикающий таймер диктор читал бы каждую секунду. */}
          <span aria-hidden="true" className="text-xs tabular-nums text-fg4 shrink-0">{elapsed(job, now)}</span>
        </div>
        <p className="text-note text-fg4 truncate">
          {queued ? 'В очереди' : (job.progress ?? 'Выполняется…')}
        </p>
      </div>
      {/* Отмена — только пока задача в очереди; выполняемые добегают до конца. */}
      {queued && (
        <IconButton label="Отменить задачу" title="Отменить (задача ещё в очереди)" danger
          onClick={() => cancel.mutate(job.id)} disabled={cancel.isPending}>
          <X size={16} />
        </IconButton>
      )}
    </div>
  );
}
