import * as Popover from '@radix-ui/react-popover';
import {
  Bell, BellOff, Info, AlertTriangle, AlertCircle, X, Check, CheckCheck, Trash2, HeartPulse, Download,
} from 'lucide-react';
import { IconButton } from '@/shared/ui/Button';
import {
  useNotifications, useHealth,
  useMarkNotificationRead, useMarkAllNotificationsRead,
  useDismissNotification, useClearNotifications,
  type NotificationSeverity, type NotificationDto,
} from '@/shared/api/notifications';
import { apiClient } from '@/shared/api/client';
import { filenameFromContentDisposition } from '@/shared/api/attachments';

// Прямой доступ к результату job: скачиваем через apiClient (с JWT), сохраняем файл.
async function openResult(linkUrl: string) {
  const path = linkUrl.replace(/^\/api/, '');
  const resp = await apiClient.get(path, { responseType: 'blob' });
  const cd = resp.headers['content-disposition'] as string | undefined;
  const filename = filenameFromContentDisposition(cd, 'document');
  const url = URL.createObjectURL(resp.data as Blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

// Аватар-иконка уведомления: для ошибок — error-container, иначе — соответствующий контейнер.
const SEVERITY: Record<NotificationSeverity, { icon: typeof Info; color: string; bg: string }> = {
  Info:    { icon: Info,          color: 'text-brand',   bg: 'bg-brand-subtle' },
  Warning: { icon: AlertTriangle, color: 'text-warning', bg: 'bg-warning-subtle' },
  Error:   { icon: AlertCircle,   color: 'text-danger',  bg: 'bg-danger-subtle' },
};

function relTime(iso: string): string {
  const d = new Date(iso);
  const s = (Date.now() - d.getTime()) / 1000;
  if (s < 60) return 'только что';
  if (s < 3600) return `${Math.floor(s / 60)} мин назад`;
  if (s < 86400) return `${Math.floor(s / 3600)} ч назад`;
  return d.toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
}

function NotificationRow({ n }: { n: NotificationDto }) {
  const markRead = useMarkNotificationRead();
  const dismiss = useDismissNotification();
  const meta = SEVERITY[n.severity];
  const Icon = meta.icon;
  return (
    <div className={`group flex gap-3.5 pl-5 pr-3 py-4 transition-colors ${n.isRead ? '' : 'bg-tonal'}`}>
      <div className={`shrink-0 w-10 h-10 rounded-full flex items-center justify-center ${meta.bg}`}>
        <Icon size={20} className={meta.color} />
      </div>
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2">
          <p className="text-sm font-medium text-fg1 truncate">{n.title}</p>
          {!n.isRead && <span className="shrink-0 w-2 h-2 rounded-full bg-brand" />}
        </div>
        <p className="text-[13px] leading-[1.45] text-fg3 mt-1 break-words whitespace-pre-wrap">{n.message}</p>
        {n.linkUrl && (
          <button
            type="button"
            onClick={() => { void openResult(n.linkUrl!); }}
            className="mt-2 inline-flex items-center gap-1.5 h-8 px-3 rounded-full text-xs font-medium bg-tonal text-on-tonal hover:brightness-95 transition"
          >
            <Download size={13} /> {n.linkLabel ?? 'Открыть результат'}
          </button>
        )}
        <div className="flex items-center gap-2 mt-2 text-xs text-fg3">
          {n.source && (
            <span className="inline-flex items-center h-6 px-3 rounded-lg border border-stroke font-medium">{n.source}</span>
          )}
          <span>{relTime(n.createdAt)}</span>
        </div>
      </div>
      <div className="shrink-0 self-start flex flex-col items-center">
        {!n.isRead && (
          <IconButton label="Отметить прочитанным" size="sm" onClick={() => markRead.mutate(n.id)}>
            <Check size={16} />
          </IconButton>
        )}
        <IconButton label="Удалить" size="sm" danger onClick={() => dismiss.mutate(n.id)}
          className="opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 focus-visible:opacity-100 transition-opacity">
          <X size={16} />
        </IconButton>
      </div>
    </div>
  );
}

function HealthSection() {
  const { data: health } = useHealth();
  if (!health || health.length === 0) return null;
  return (
    <div className="px-5 pt-1 pb-2">
      <div className="flex items-center gap-1.5 mb-1 text-xs font-medium uppercase tracking-wide text-fg3">
        <HeartPulse size={16} /> Состояние системы
      </div>
      <div>
        {health.map(c => (
          <div key={c.name} className="flex items-center gap-2.5 h-9 text-sm">
            <span className={`w-2 h-2 rounded-full shrink-0 ${c.healthy ? 'bg-success' : 'bg-danger'}`} />
            <span className="text-fg1 flex-1">{c.name}</span>
            <span className={`text-xs font-medium ${c.healthy ? 'text-fg3' : 'text-danger'}`}>
              {c.healthy ? 'в норме' : (c.detail ? 'сбой' : 'недоступен')}
            </span>
          </div>
        ))}
      </div>
    </div>
  );
}

export function NotificationsCenter() {
  const { data } = useNotifications();
  const markAll = useMarkAllNotificationsRead();
  const clearAll = useClearNotifications();

  const items = data?.items ?? [];
  const unread = data?.unreadCount ?? 0;
  // Цвет бейджа: красный, если среди непрочитанных есть ошибка, иначе зелёный.
  const hasUnreadError = items.some(n => !n.isRead && n.severity === 'Error');

  return (
    <Popover.Root>
      <Popover.Trigger asChild>
        <button
          type="button"
          title="Уведомления"
          aria-label="Уведомления"
          className="relative flex items-center justify-center w-11 h-11 rounded-full transition-colors text-fg3 hover:text-fg1 hover:bg-black/5 dark:hover:bg-white/10 data-[state=open]:bg-tonal data-[state=open]:text-on-tonal"
        >
          <Bell size={22} />
          {unread > 0 && (
            <span className={`absolute top-1 right-1 min-w-[16px] h-4 px-1 rounded-full text-[11px] font-medium flex items-center justify-center text-white ${hasUnreadError ? 'bg-danger' : 'bg-success'}`}>
              {unread > 99 ? '99+' : unread}
            </span>
          )}
        </button>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content
          align="end"
          sideOffset={8}
          className="w-[400px] max-w-[calc(100vw-40px)] max-h-[calc(100vh-90px)] flex flex-col rounded-2xl border border-stroke bg-surface z-50 overflow-hidden focus:outline-none"
          style={{ boxShadow: 'var(--f-shadow16)' }}
        >
          {/* Header (фиксированная шапка) */}
          <div className="flex items-center gap-2 pl-5 pr-2 py-3 shrink-0">
            <span className="flex-1 text-base font-medium text-fg1">Уведомления</span>
            <IconButton label="Отметить все прочитанными" onClick={() => markAll.mutate()} disabled={unread === 0}>
              <CheckCheck size={18} />
            </IconButton>
            <IconButton label="Очистить все" onClick={() => clearAll.mutate()} disabled={items.length === 0}>
              <Trash2 size={18} />
            </IconButton>
          </div>

          {/* Scrollable body */}
          <div className="flex-1 overflow-y-auto min-h-0">
            <HealthSection />
            <div className="border-t border-stroke" />
            {items.length === 0 ? (
              <div className="px-6 py-9 text-center text-sm text-fg3">
                <BellOff size={36} className="mx-auto mb-2 opacity-50" />
                Нет новых уведомлений
              </div>
            ) : (
              <div className="divide-y divide-stroke">
                {items.map(n => <NotificationRow key={n.id} n={n} />)}
              </div>
            )}
          </div>
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}
