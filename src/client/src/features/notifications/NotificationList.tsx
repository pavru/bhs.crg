import { useId, useState } from 'react';
import * as Popover from '@radix-ui/react-popover';
import { Icon } from '@/shared/ui/Icon';
import { ArrowForward, Check, Close, Delete, DoneAll, Download, ErrorIcon, Info, KeyboardArrowDown, MonitorHeart, NotificationsOff, OpenInNew, Warning, type IconDef } from '@/shared/ui/icons';
import { useGuardedNavigate } from '@/shared/ui/NavigationGuard';
import { formatDayTime } from '@/shared/format/format';
import { IconButton } from '@/shared/ui/Button';
import {
  useHealth,
  useMarkNotificationRead, useMarkAllNotificationsRead,
  useDismissNotification, useClearNotifications,
  type NotificationSeverity, type NotificationDto, type ComponentHealth,
} from '@/shared/api/notifications';
import { apiClient } from '@/shared/api/client';
import { filenameFromContentDisposition, saveBlob } from '@/shared/api/attachments';
import { Tooltip } from '@/shared/ui/Tooltip';

/**
 * Ссылка ведёт НАРУЖУ (issue #834): issue в GitHub, заведённый из сообщения об ошибке. Такую
 * открываем новой вкладкой — ни скачивание с токеном, ни маршрутизатор приложения к чужому адресу
 * не применимы.
 */
function isExternalLink(linkUrl: string): boolean {
  return linkUrl.startsWith('https://') || linkUrl.startsWith('http://');
}

/**
 * Куда ведёт ссылка уведомления: к ФАЙЛУ или на ЭКРАН.
 *
 * Различать обязательно (issue #834). Файл скачивается запросом с токеном, экран открывается
 * маршрутом — а до сих пор всё уходило в скачивание, и ссылка «Открыть проблемы комплекта»
 * (`/document-sets/…`, issue #414) пыталась получить страницу как blob. Признак — префикс `/api`:
 * его ставит тот же код, что публикует ссылку на скачивание.
 */
function isDownloadLink(linkUrl: string): boolean {
  return linkUrl.startsWith('/api/');
}

// Прямой доступ к результату job: скачиваем через apiClient (с JWT), сохраняем файл.
async function openResult(linkUrl: string) {
  const path = linkUrl.replace(/^\/api/, '');
  const resp = await apiClient.get(path, { responseType: 'blob' });
  const cd = resp.headers['content-disposition'] as string | undefined;
  const filename = filenameFromContentDisposition(cd, 'document');
  saveBlob(resp.data as Blob, filename);
}

// Аватар-иконка уведомления: для ошибок — error-container, иначе — соответствующий контейнер.
const SEVERITY: Record<NotificationSeverity, { icon: IconDef; color: string; bg: string }> = {
  Info:    { icon: Info,          color: 'text-on-primary-container', bg: 'bg-primary-container' },
  Warning: { icon: Warning, color: 'text-on-warning-container', bg: 'bg-warning-container' },
  Error:   { icon: ErrorIcon,   color: 'text-on-error-container', bg: 'bg-error-container' },
};

function relTime(iso: string): string {
  const d = new Date(iso);
  const s = (Date.now() - d.getTime()) / 1000;
  if (s < 60) return 'только что';
  if (s < 3600) return `${Math.floor(s / 60)} мин назад`;
  if (s < 86400) return `${Math.floor(s / 3600)} ч назад`;
  return formatDayTime(d);
}

function NotificationRow({ n }: { n: NotificationDto }) {
  // Кнопка активности видна над любой страницей — уходит через охрану несохранённых правок (issue #1238).
  const navigate = useGuardedNavigate();
  const markRead = useMarkNotificationRead();
  const dismiss = useDismissNotification();
  const meta = SEVERITY[n.severity];
  const icon = meta.icon;
  return (
    <div className={`group flex gap-3.5 pl-5 pr-3 py-4 transition-colors ${n.isRead ? '' : 'bg-secondary-container'}`}>
      <div className={`shrink-0 w-10 h-10 rounded-full flex items-center justify-center ${meta.bg}`}>
        <Icon icon={icon} size={20} className={meta.color} />
      </div>
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2">
          <p className="text-body-medium font-medium text-on-surface truncate">{n.title}</p>
          {!n.isRead && <span className="shrink-0 w-2 h-2 rounded-full bg-primary" />}
        </div>
        <p className="text-body-medium leading-[1.45] text-on-surface-variant mt-1 break-words whitespace-pre-wrap">{n.message}</p>
        {n.linkUrl && (isExternalLink(n.linkUrl) ? (
          <a
            href={n.linkUrl} target="_blank" rel="noreferrer"
            className="mt-2 inline-flex items-center gap-1.5 h-8 px-3 rounded-full text-body-small font-medium bg-secondary-container text-on-secondary-container state-layer"
          >
            <Icon icon={OpenInNew} size={18} /> {n.linkLabel ?? 'Открыть'}
          </a>
        ) : isDownloadLink(n.linkUrl) ? (
          <button
            type="button"
            onClick={() => { void openResult(n.linkUrl!); }}
            className="mt-2 inline-flex items-center gap-1.5 h-8 px-3 rounded-full text-body-small font-medium bg-secondary-container text-on-secondary-container state-layer"
          >
            <Icon icon={Download} size={18} /> {n.linkLabel ?? 'Открыть результат'}
          </button>
        ) : (
          // Уведомления — сигнал, а не рабочее место: уходя на экран, закрываем его за собой.
          <Popover.Close asChild>
            <button
              type="button"
              onClick={() => navigate(n.linkUrl!)}
              className="mt-2 inline-flex items-center gap-1.5 h-8 px-3 rounded-full text-body-small font-medium bg-secondary-container text-on-secondary-container state-layer"
            >
              <Icon icon={ArrowForward} size={18} /> {n.linkLabel ?? 'Открыть'}
            </button>
          </Popover.Close>
        ))}
        <div className="flex items-center gap-2 mt-2 text-body-small text-on-surface-variant">
          {n.source && (
            <span className="inline-flex items-center h-6 px-3 rounded-md border border-outline-variant font-medium">{n.source}</span>
          )}
          <span>{relTime(n.createdAt)}</span>
        </div>
      </div>
      <div className="shrink-0 self-start flex flex-col items-center">
        {!n.isRead && (
          <IconButton label="Отметить прочитанным" size="sm" onClick={() => markRead.mutate(n.id)}>
            <Icon icon={Check} size={18} />
          </IconButton>
        )}
        <IconButton label="Удалить" size="sm" danger onClick={() => dismiss.mutate(n.id)}
          className="opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 focus-visible:opacity-100 transition-opacity">
          <Icon icon={Close} size={18} />
        </IconButton>
      </div>
    </div>
  );
}

/** Состояние компонента словами и цветом. Три состояния, а не два: «отвечает через раз» — не норма и
 *  ещё не отказ. Пока их было два, панель перекрашивалась на каждом опросе (issue #917). */
function healthLook(c: ComponentHealth): { dot: string; tone: string; label: string } {
  if (c.state === 'Up') return { dot: 'bg-success', tone: 'text-on-surface-variant', label: 'в норме' };
  if (c.state === 'Flapping') return { dot: 'bg-warning', tone: 'text-warning', label: 'отвечает через раз' };
  // «Не проверяли» — не норма и не отказ: сервис за упавшим прокси, и сказано о нём одной строкой
  // выше. Выдать это за норму значило бы спрятать, за отказ — обвинить невиновного.
  if (c.state === 'Unknown') return { dot: 'bg-outline', tone: 'text-on-surface-variant', label: 'не проверяли' };
  return { dot: 'bg-error', tone: 'text-error', label: c.detail ? 'сбой' : 'недоступен' };
}

/**
 * Состояние системы — одной строкой, и только когда что-то не в норме (#1431): «всё в порядке»
 * каждому на каждом открытии — шум, который приучает строку не читать. Полный список раскрывается
 * здесь же, а не на странице администрирования: его читает и пользователь — руководство отсылает
 * к строке «Конвертер офисных файлов», когда не строится вид для чтения.
 *
 * «Не проверяли» в счёт не идёт: такой компонент стоит за упавшим прокси, а прокси уже в строке.
 */
function HealthNote() {
  const { data: health } = useHealth();
  const [open, setOpen] = useState(false);
  const bad = (health ?? []).filter(c => c.state === 'Down' || c.state === 'Flapping');
  if (!health || bad.length === 0) return null;
  const down = bad.some(c => c.state === 'Down');
  const summary = bad.length === 1
    ? `${bad[0].name} — ${healthLook(bad[0]).label}`
    : `не в норме: ${bad.length} из ${health.length}`;
  return (
    <div className="shrink-0 border-b border-outline-variant">
      <button type="button" aria-expanded={open} onClick={() => setOpen(v => !v)}
        className="w-full flex items-center gap-2.5 pl-5 pr-4 py-2.5 text-left text-body-medium state-layer focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-primary">
        <Icon icon={MonitorHeart} size={18} className={`shrink-0 ${down ? 'text-error' : 'text-warning'}`} />
        {/* Переносом, а не обрезкой: обрезанным оказывалось как раз имя сервиса — то, ради чего строка. */}
        <span className="flex-1 min-w-0">
          <span className="font-medium text-on-surface">Состояние системы:</span>{' '}
          <span className={down ? 'text-error' : 'text-warning'}>{summary}</span>
        </span>
        <Icon icon={KeyboardArrowDown} size={18} className={`shrink-0 text-on-surface-variant transition-transform ${open ? 'rotate-180' : ''}`} />
      </button>
      {open && (
        <div className="pl-5 pr-4 pb-2">
          {health.map(c => {
            const { dot, tone, label } = healthLook(c);
            return (
              <Tooltip key={c.code} content={c.detail ?? undefined}>
                <div className="flex items-center gap-2.5 h-9 text-body-medium">
                  <span className={`w-2 h-2 rounded-full shrink-0 ${dot}`} />
                  <span className="text-on-surface flex-1 truncate">
                    {c.name}
                    {c.viaProxy && <span className="ml-1.5 text-body-small text-on-surface-variant">через прокси</span>}
                  </span>
                  <span className={`text-body-small font-medium shrink-0 ${tone}`}>{label}</span>
                </div>
              </Tooltip>
            );
          })}
        </div>
      )}
    </div>
  );
}

/**
 * Раздел «Уведомления» кнопки активности (#1431): заголовок с «Отметить все прочитанными» и
 * «Очистить все», строка состояния системы, список. «Уже случилось» — в отличие от раздела
 * «Выполняется» над ним.
 */
export function NotificationList({ items, unread, className = '' }: {
  items: NotificationDto[];
  unread: number;
  /** Место в раскладке поповера: прокручивается только список, шапка остаётся на месте. */
  className?: string;
}) {
  const markAll = useMarkAllNotificationsRead();
  const clearAll = useClearNotifications();
  const heading = useId();
  return (
    <section aria-labelledby={heading} className={`flex flex-col ${className}`}>
      <div className="shrink-0 flex items-center gap-2 pl-5 pr-2 py-2">
        <h2 id={heading} className="flex-1 text-body-large font-medium text-on-surface">Уведомления</h2>
        <IconButton label="Отметить все прочитанными" onClick={() => markAll.mutate()} disabled={unread === 0}>
          <Icon icon={DoneAll} size={18} />
        </IconButton>
        <IconButton label="Очистить все" onClick={() => clearAll.mutate()} disabled={items.length === 0}>
          <Icon icon={Delete} size={18} />
        </IconButton>
      </div>
      <HealthNote />
      {items.length === 0 ? (
        <div className="px-6 py-9 text-center text-body-medium text-on-surface-variant">
          <Icon icon={NotificationsOff} size={40} className="mx-auto mb-2 opacity-50" />
          Нет новых уведомлений
        </div>
      ) : (
        <div className="min-h-0 overflow-y-auto divide-y divide-outline-variant">
          {items.map(n => <NotificationRow key={n.id} n={n} />)}
        </div>
      )}
    </section>
  );
}
