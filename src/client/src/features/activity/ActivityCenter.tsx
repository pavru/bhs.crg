import { useId, useState } from 'react';
import * as Popover from '@radix-ui/react-popover';
import { Badge } from '@/shared/ui/Badge';
import { Icon } from '@/shared/ui/Icon';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { Notifications } from '@/shared/ui/icons';
import { useActiveJobs } from '@/shared/api/jobs';
import { useNotifications } from '@/shared/api/notifications';
import { JobRow } from '@/features/jobs/JobRow';
import { NotificationList } from '@/features/notifications/NotificationList';
import { useHistoryLayer } from '@/shared/ui/historyLayers';
import { LAYER_SURFACE } from '@/shared/ui/menuClasses';
import { useAuth } from '@/shared/hooks/useAuth';
import { ScanBatchActivity } from '@/features/costs/ScanBatchActivity';
import { batchDoing, useScanBatch } from '@/features/costs/scanBatch';

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
 * Пакет счетов из файлов (costs-invoice-screen-14, D5) — клиентский источник рядом с серверными задачами: пока
 * он идёт, кнопка — пилюля с его ходом, а итог стоит разделом «Загрузка счетов» и сам не исчезает. Готовый,
 * но не закрытый итог считается в числе новых: на экране без списка счетов о нём больше негде сказать.
 *
 * Монтируется один раз, в оболочке: `useActiveJobs` — и поллер задач, и точка инвалидации по их
 * завершении. Частота опроса — его дело (2 с при задачах, 10 с в простое), здесь не меняется.
 */
export function ActivityCenter() {
  const jobs = useActiveJobs();
  const { data } = useNotifications();
  const me = useAuth().user?.sub;
  const batch = useScanBatch(me);
  const loading = batch !== null && batch.phase !== 'done';
  const summary = batch !== null && batch.phase === 'done';
  const items = data?.items ?? [];
  const unread = (data?.unreadCount ?? 0) + (summary ? 1 : 0);
  const hasUnreadError = items.some(n => !n.isRead && n.severity === 'Error');
  const tasks = jobs.length + (loading ? 1 : 0);
  const busy = tasks > 0;
  const jobsHeading = useId();
  const doing = tasks > 1 ? `Задач выполняется: ${tasks}` : loading ? batchDoing(batch) : jobs[0]?.title ?? '';

  const news = unread > 0
    ? `новых уведомлений: ${unread}${hasUnreadError ? ', среди них ошибка' : ''}${summary ? ', итог загрузки счетов' : ''}`
    : 'новых уведомлений нет';
  const label = busy
    ? `${tasks === 1 ? `Выполняется: ${doing}` : doing}; ${news}`
    : `Уведомления — ${news}`;

  // «Назад» на телефоне закрывает панель, а не страницу (issue #1418).
  const [open, setOpen] = useState(false);
  useHistoryLayer(open, () => { setOpen(false); return true; });

  return (
    <Popover.Root open={open} onOpenChange={setOpen}>
      <Popover.Trigger asChild>
        <button type="button" data-testid="activity" aria-label={label}
          className={'relative shrink-0 flex items-center justify-center rounded-full h-10 max-md:h-12 touch-target ' +
            'hover:text-on-surface state-layer data-[state=open]:bg-secondary-container data-[state=open]:text-on-secondary-container ' +
            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 focus-visible:ring-offset-surface ' +
            // Цвет — в каждой ветке свой, а не общий с перебивкой: из двух `text-*` побеждает тот, что
            // позже в таблице стилей, а не в атрибуте (ревью PR #1450).
            // На телефоне — 48 px: цель касания видимая, как у соседей по полосе 64 (#1434, sharedui-md3-15).
            (busy ? 'gap-2 pl-2.5 pr-3 text-on-surface-variant' : 'w-10 max-md:w-12 text-on-surface-variant')}>
          {busy ? (
            <>
              <CircularProgress size={20} className="text-primary" />
              {/* На телефоне — кольцо и число: подпись выдавливала аватар из первой строки шапки (#1430). */}
              <span className="max-sm:hidden max-w-45 truncate text-body-medium font-medium">{doing}</span>
              {unread > 0 && <Badge count={unread} />}
            </>
          ) : (
            <>
              <Icon icon={Notifications} size={24} />
              {unread > 0 && <Badge count={unread} className="absolute top-0.5 right-0.5" />}
            </>
          )}
        </button>
      </Popover.Trigger>
      <Popover.Portal>
        {/* Прокручивается только список уведомлений: шапка с «Отметить все» и «Очистить все» и
            раздел «Выполняется» стоят на месте, как у прежнего колокольчика (ревью PR #1450). */}
        <Popover.Content align="end" sideOffset={8} collisionPadding={8} aria-label="Активность"
          className={`z-50 w-100 max-w-(--radix-popover-content-available-width) max-h-(--radix-popover-content-available-height) flex flex-col overflow-hidden rounded-lg focus:outline-none ${LAYER_SURFACE}`}>
          {batch && me && <ScanBatchActivity batch={batch} owner={me} />}
          {jobs.length > 0 && (
            <section aria-labelledby={jobsHeading} className="shrink-0 border-b border-outline-variant pt-1">
              <h2 id={jobsHeading} className="px-5 pt-2 text-body-large font-medium text-on-surface">Выполняется</h2>
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
