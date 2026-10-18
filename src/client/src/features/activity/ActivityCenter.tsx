import { useId, useState } from 'react';
import * as Popover from '@radix-ui/react-popover';
import { Bell } from 'lucide-react';
import { useActiveJobs } from '@/shared/api/jobs';
import { useNotifications } from '@/shared/api/notifications';
import { JobRow } from '@/features/jobs/JobRow';
import { NotificationList } from '@/features/notifications/NotificationList';
import { useHistoryLayer } from '@/shared/ui/historyLayers';

/**
 * Кнопка активности в шапке (issue #1431) — одна дверь к тому, что идёт и что уже случилось. Раньше
 * их было две: пилюля задач и колокольчик, и шапка росла на кнопку ровно тогда, когда человек ждёт.
 *
 * - Задач нет — колокольчик с числом новых уведомлений.
 * - Задача идёт — пилюля: кольцо хода, что выполняется, число уведомлений.
 * - Поповер — «Выполняется» сверху, «Уведомления» под ним.
 *
 * Число — на `brand`: белый на зелёном давал около 4,35:1, ниже 4,5. Красное число по-прежнему
 * значит «среди новых есть ошибка».
 *
 * Подсказки у кнопки нет, как у аватара: имя — в `aria-label`, а подсказка, открытая возвращённым
 * фокусом, забирала бы себе следующий Esc.
 *
 * Монтируется один раз, в оболочке: `useActiveJobs` — и поллер задач, и точка инвалидации по их
 * завершении. Частота опроса — его дело (2 с при задачах, 10 с в простое), здесь не меняется.
 */
export function ActivityCenter() {
  const jobs = useActiveJobs();
  const { data } = useNotifications();
  const items = data?.items ?? [];
  const unread = data?.unreadCount ?? 0;
  const hasUnreadError = items.some(n => !n.isRead && n.severity === 'Error');
  const busy = jobs.length > 0;
  const jobsHeading = useId();
  const doing = jobs.length === 1 ? jobs[0].title : `Задач выполняется: ${jobs.length}`;

  const news = unread > 0
    ? `новых уведомлений: ${unread}${hasUnreadError ? ', среди них ошибка' : ''}`
    : 'новых уведомлений нет';
  const label = busy
    ? `${jobs.length === 1 ? `Выполняется: ${doing}` : doing}; ${news}`
    : `Уведомления — ${news}`;

  // «Назад» на телефоне закрывает панель, а не страницу (issue #1418).
  const [open, setOpen] = useState(false);
  useHistoryLayer(open, () => { setOpen(false); return true; });

  return (
    <Popover.Root open={open} onOpenChange={setOpen}>
      <Popover.Trigger asChild>
        <button type="button" data-testid="activity" aria-label={label}
          className={'relative shrink-0 flex items-center justify-center rounded-full h-10 max-md:h-11 transition-colors ' +
            'hover:text-fg1 hover:bg-black/5 dark:hover:bg-white/10 data-[state=open]:bg-tonal data-[state=open]:text-on-tonal ' +
            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand focus-visible:ring-offset-2 focus-visible:ring-offset-surface ' +
            // Цвет — в каждой ветке свой, а не общий с перебивкой: из двух `text-*` побеждает тот, что
            // позже в таблице стилей, а не в атрибуте (ревью PR #1450).
            // На телефоне — 44 px: цель касания (#1434).
            (busy ? 'gap-2 pl-2.5 pr-3 text-fg2' : 'w-10 max-md:w-11 text-fg3')}>
          {busy ? (
            <>
              <ProgressRing />
              {/* На телефоне — кольцо и число: подпись выдавливала аватар из первой строки шапки (#1430). */}
              <span className="max-sm:hidden max-w-45 truncate text-sm font-medium">{doing}</span>
              {unread > 0 && <Count n={unread} error={hasUnreadError} />}
            </>
          ) : (
            <>
              <Bell size={20} />
              {unread > 0 && <span className="absolute top-0.5 right-0.5"><Count n={unread} error={hasUnreadError} /></span>}
            </>
          )}
        </button>
      </Popover.Trigger>
      <Popover.Portal>
        {/* Прокручивается только список уведомлений: шапка с «Отметить все» и «Очистить все» и
            раздел «Выполняется» стоят на месте, как у прежнего колокольчика (ревью PR #1450). */}
        <Popover.Content align="end" sideOffset={8} collisionPadding={8} aria-label="Активность"
          className="z-50 w-100 max-w-(--radix-popover-content-available-width) max-h-(--radix-popover-content-available-height) flex flex-col overflow-hidden rounded-2xl border border-stroke bg-surface focus:outline-none"
          style={{ boxShadow: 'var(--f-shadow16)' }}>
          {busy && (
            <section aria-labelledby={jobsHeading} className="shrink-0 border-b border-stroke pt-1">
              <h2 id={jobsHeading} className="px-5 pt-2 text-base font-medium text-fg1">Выполняется</h2>
              {/* Ход задач озвучивается, пока поповер открыт (так было у прежнего поповера задач).
                  Таймер в строке из озвучки убран — иначе диктор читал бы его каждую секунду. */}
              <div role="status" className="max-h-60 overflow-y-auto">
                {jobs.map(job => <JobRow key={job.id} job={job} />)}
              </div>
            </section>
          )}
          <NotificationList items={items} unread={unread} className="flex-1 min-h-0" />
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

/**
 * Число новых уведомлений. Красное — среди них ошибка. Текст на красном — цвета поверхности, а не
 * белый: в тёмной теме `danger` светло-розовый, и белое число на нём читалось при 1,8:1.
 */
function Count({ n, error }: { n: number; error: boolean }) {
  return (
    <span aria-hidden="true"
      className={'min-w-4 h-4 px-1 rounded-full text-caption font-medium flex items-center justify-center ' +
        (error ? 'bg-danger text-surface' : 'bg-brand text-on-brand')}>
      {n > 99 ? '99+' : n}
    </span>
  );
}

/** Кольцо хода: дорожка и бегущая по ней дуга. Хода в числах задача не сообщает — кольцо неопределённое. */
function ProgressRing() {
  return (
    <svg viewBox="0 0 20 20" width={20} height={20} aria-hidden="true"
      className="shrink-0 animate-spin motion-reduce:animate-none">
      <circle cx="10" cy="10" r="8" fill="none" strokeWidth="2.5" className="stroke-stroke" />
      <circle cx="10" cy="10" r="8" fill="none" strokeWidth="2.5" strokeLinecap="round"
        strokeDasharray="14 50.3" className="stroke-brand" />
    </svg>
  );
}
