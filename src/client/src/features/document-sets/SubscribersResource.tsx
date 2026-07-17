import { useState } from 'react';
import { Trash2, Mail, Plus, AlertTriangle } from 'lucide-react';
import { useAuth } from '@/shared/hooks/useAuth';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useListUsers } from '@/shared/api/users';
import {
  useSubscribers, useRecipients, useAddSubscriber, useRemoveSubscriber, type SubscriptionScope,
} from '@/shared/api/subscriptions';
import { SendMessageDialog } from '@/shared/ui/SendMessageDialog';

/**
 * Содержимое «Подписчики» для ЛЮБОГО scope (issue #210, ось видимости): тулбар «Сообщение» (Admin) +
 * список подписчиков + добавление. Без чипа области и без внешнего коллапса — область выражается
 * положением. Единый компонент для scoped-панелей и будущих scope-страниц. Заголовок даёт вызывающий.
 * Управление (добавить/удалить/отправить) — только Admin; список видят все. Отправка идёт эффективным
 * получателям (прямые + унаследованные с вышестоящих уровней).
 */
export function SubscribersResource({ scope, scopeId }: { scope: SubscriptionScope; scopeId: string }) {
  const { user } = useAuth();
  const isAdmin = user?.role === 'Admin';
  const [sendOpen, setSendOpen] = useState(false);
  const [addUserId, setAddUserId] = useState('');

  const { data: subscribers = [] } = useSubscribers(scope, scopeId);
  const { data: users = [] } = useListUsers();
  const { data: recipients = [] } = useRecipients(scope, scopeId, sendOpen);
  const add = useAddSubscriber();
  const remove = useRemoveSubscriber();

  const subscribedIds = new Set(subscribers.map(s => s.userId));
  const available = users.filter(u => !subscribedIds.has(u.id));

  function handleAdd() {
    if (!addUserId) return;
    add.mutate({ userId: addUserId, scope, scopeId });
    setAddUserId('');
  }

  return (
    <div className="space-y-2">
      {isAdmin && (
        <div className="flex justify-end">
          <button onClick={() => setSendOpen(true)}
            className="flex items-center gap-1 text-xs px-2 py-1 rounded text-brand hover:bg-brand-subtle transition-colors">
            <Mail size={12} /> Сообщение
          </button>
        </div>
      )}

      {subscribers.length === 0 ? (
        <p className="text-xs text-fg4">Нет подписчиков этого уровня. Получатели наследуются с вышестоящих.</p>
      ) : (
        <div className="rounded-md border border-stroke divide-y divide-muted">
          {subscribers.map(s => (
            <div key={s.id} className="flex items-center gap-2 px-2.5 py-1.5 text-sm group">
              <span className="text-fg1 flex-1 min-w-0 truncate">{s.displayName}</span>
              <span className="text-xs text-fg4 min-w-0 truncate">{s.email || '—'}</span>
              {!s.validEmail && (
                <span title="Нет валидного email — писем не получит" className="shrink-0">
                  <AlertTriangle size={13} className="text-warning" />
                </span>
              )}
              {isAdmin && (
                <button onClick={() => remove.mutate({ id: s.id, scope, scopeId })}
                  className="p-0.5 text-stroke-strong hover:text-danger opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 transition-all" title="Убрать из подписчиков">
                  <Trash2 size={13} />
                </button>
              )}
            </div>
          ))}
        </div>
      )}

      {isAdmin && (
        <div className="flex items-center gap-2">
          <Select value={addUserId || undefined} onValueChange={setAddUserId}
            placeholder="Добавить подписчика…" aria-label="Подписчик" className="flex-1">
            {available.map(u => <SelectItem key={u.id} value={u.id}>{u.displayName || u.email}</SelectItem>)}
          </Select>
          <button onClick={handleAdd} disabled={!addUserId || add.isPending}
            className="flex items-center gap-1 text-sm px-3 py-1.5 border border-stroke-strong rounded-md hover:bg-base transition-colors disabled:opacity-50">
            <Plus size={13} /> Добавить
          </button>
        </div>
      )}

      {isAdmin && (
        <SendMessageDialog open={sendOpen} onClose={() => setSendOpen(false)}
          title="Сообщение подписчикам"
          candidates={recipients.map(r => ({ id: r.userId, displayName: r.displayName, email: r.email }))} />
      )}
    </div>
  );
}
