import * as Popover from '@radix-ui/react-popover';
import {
  Bell, Info, AlertTriangle, AlertCircle, X, Check, CheckCheck, Trash2, Activity, Download,
} from 'lucide-react';
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

const SEVERITY: Record<NotificationSeverity, { icon: typeof Info; color: string; bg: string; label: string }> = {
  Info:    { icon: Info,          color: 'text-brand',   bg: 'bg-brand-subtle',   label: 'Информация' },
  Warning: { icon: AlertTriangle, color: 'text-warning', bg: 'bg-warning-subtle', label: 'Предупреждение' },
  Error:   { icon: AlertCircle,   color: 'text-danger',  bg: 'bg-danger-subtle',  label: 'Ошибка' },
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
    <div
      className={`group flex gap-3 px-4 py-3 border-b border-stroke last:border-b-0 ${n.isRead ? 'opacity-65' : ''}`}
    >
      <div className={`shrink-0 w-7 h-7 rounded-full flex items-center justify-center ${meta.bg}`}>
        <Icon size={15} className={meta.color} />
      </div>
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2">
          <p className="text-sm font-medium text-fg1 truncate">{n.title}</p>
          {!n.isRead && <span className="shrink-0 w-1.5 h-1.5 rounded-full bg-brand" />}
        </div>
        <p className="text-xs text-fg2 mt-0.5 break-words whitespace-pre-wrap">{n.message}</p>
        {n.linkUrl && (
          <button
            type="button"
            onClick={() => { void openResult(n.linkUrl!); }}
            className="mt-1.5 inline-flex items-center gap-1.5 px-2 py-1 rounded-md text-xs font-medium bg-brand-subtle text-brand hover:bg-brand/15 transition-colors"
          >
            <Download size={13} /> {n.linkLabel ?? 'Открыть результат'}
          </button>
        )}
        <div className="flex items-center gap-2 mt-1 text-[11px] text-fg4">
          {n.source && <span className="px-1.5 py-0.5 rounded bg-base">{n.source}</span>}
          <span>{relTime(n.createdAt)}</span>
        </div>
      </div>
      <div className="shrink-0 self-start flex flex-col items-center gap-1.5">
        {!n.isRead && (
          <button
            type="button"
            onClick={() => markRead.mutate(n.id)}
            title="Отметить прочитанным"
            className="text-fg4 hover:text-brand transition-colors"
          >
            <Check size={14} />
          </button>
        )}
        <button
          type="button"
          onClick={() => dismiss.mutate(n.id)}
          title="Удалить"
          className="text-fg4 hover:text-danger opacity-0 group-hover:opacity-100 focus-visible:opacity-100 transition-opacity"
        >
          <X size={14} />
        </button>
      </div>
    </div>
  );
}

function HealthSection() {
  const { data: health } = useHealth();
  if (!health || health.length === 0) return null;
  return (
    <div className="px-4 py-3 border-b border-stroke bg-base/50">
      <div className="flex items-center gap-1.5 mb-2 text-[11px] font-semibold uppercase tracking-wide text-fg3">
        <Activity size={12} /> Состояние системы
      </div>
      <div className="space-y-1.5">
        {health.map(c => (
          <div key={c.name} className="flex items-center gap-2 text-xs">
            <span className={`w-2 h-2 rounded-full shrink-0 ${c.healthy ? 'bg-success' : 'bg-danger'}`} />
            <span className="text-fg2 flex-1">{c.name}</span>
            <span className={c.healthy ? 'text-fg4' : 'text-danger'}>
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
  const hasError = items.some(n => !n.isRead && n.severity === 'Error');

  return (
    <Popover.Root>
      <Popover.Trigger asChild>
        <button
          type="button"
          title="Уведомления"
          className="relative flex items-center justify-center w-9 h-9 rounded-md transition-colors text-fg3 hover:text-fg1 hover:bg-base data-[state=open]:bg-base data-[state=open]:text-fg1"
        >
          <Bell size={18} />
          {unread > 0 && (
            <span
              className={`absolute -top-0.5 -right-0.5 min-w-[16px] h-4 px-1 rounded-full text-[10px] font-semibold
                flex items-center justify-center text-white ${hasError ? 'bg-danger' : 'bg-brand'}`}
            >
              {unread > 99 ? '99+' : unread}
            </span>
          )}
        </button>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content
          align="end"
          sideOffset={8}
          className="w-96 max-h-[75vh] flex flex-col rounded-xl border border-stroke bg-surface shadow-lg z-50 overflow-hidden focus:outline-none"
        >
          {/* Header */}
          <div className="flex items-center justify-between px-4 py-2.5 border-b border-stroke shrink-0">
            <span className="text-sm font-semibold text-fg1">Уведомления</span>
            <div className="flex items-center gap-1">
              <button
                type="button"
                onClick={() => markAll.mutate()}
                disabled={unread === 0}
                title="Отметить все прочитанными"
                className="p-1.5 rounded text-fg3 hover:text-fg1 hover:bg-base disabled:opacity-40 disabled:hover:bg-transparent"
              >
                <CheckCheck size={15} />
              </button>
              <button
                type="button"
                onClick={() => clearAll.mutate()}
                disabled={items.length === 0}
                title="Очистить все"
                className="p-1.5 rounded text-fg3 hover:text-danger hover:bg-base disabled:opacity-40 disabled:hover:bg-transparent"
              >
                <Trash2 size={15} />
              </button>
            </div>
          </div>

          {/* Scrollable body */}
          <div className="flex-1 overflow-y-auto min-h-0">
            <HealthSection />
            {items.length === 0 ? (
              <div className="px-4 py-10 text-center text-sm text-fg4">
                <Bell size={24} className="mx-auto mb-2 opacity-40" />
                Нет уведомлений
              </div>
            ) : (
              items.map(n => <NotificationRow key={n.id} n={n} />)
            )}
          </div>
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}
