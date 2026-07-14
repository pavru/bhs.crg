import { useState } from 'react';
import { Users, ChevronDown, ChevronRight, Trash2, Mail, Plus, AlertTriangle } from 'lucide-react';
import { useAuth } from '@/shared/hooks/useAuth';
import { useListUsers } from '@/shared/api/users';
import {
  useSubscribers, useRecipients, useAddSubscriber, useRemoveSubscriber, type SubscriptionScope,
} from '@/shared/api/subscriptions';
import { SendMessageDialog } from '@/shared/ui/SendMessageDialog';
import { SCOPE_LABELS } from '@/shared/api/types';
import { SCOPE_COLORS } from './fields/constants';

/**
 * Подписчики уровня стройка/раздел/комплект. Управление (добавить/удалить/отправить) — только Admin
 * (как и отправка почты); список видят все. Отправка идёт эффективным получателям (прямые +
 * унаследованные с вышестоящих уровней).
 */
export function SubscribersPanel({ scope, scopeId }: { scope: SubscriptionScope; scopeId: string }) {
  const { user } = useAuth();
  const isAdmin = user?.role === 'Admin';
  const [expanded, setExpanded] = useState(false);
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
    <div className="mt-3 rounded-xl overflow-hidden border border-stroke">
      <div role="button" tabIndex={0} aria-expanded={expanded}
        className="flex items-center gap-2 px-3 py-2 cursor-pointer select-none bg-base"
        onClick={() => setExpanded(o => !o)}
        onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setExpanded(o => !o); } }}>
        <Users size={13} className="text-brand" />
        <span className={`shrink-0 text-[10px] px-1.5 py-0.5 rounded-full font-medium ${SCOPE_COLORS[scope]}`}>
          {SCOPE_LABELS[scope]}
        </span>
        <span className="text-xs font-medium flex-1 text-fg2">
          Подписчики
          {subscribers.length > 0 && <span className="ml-1.5 text-[10px] px-1.5 py-0.5 rounded-full bg-brand-subtle text-brand">{subscribers.length}</span>}
        </span>
        {isAdmin && (
          <button onClick={e => { e.stopPropagation(); setSendOpen(true); }}
            className="flex items-center gap-1 text-xs px-2 py-1 rounded text-brand hover:bg-brand-subtle transition-colors">
            <Mail size={11} /> Сообщение
          </button>
        )}
        {expanded ? <ChevronDown size={13} className="text-fg4" /> : <ChevronRight size={13} className="text-fg4" />}
      </div>

      {expanded && (
        <div className="border-t border-stroke bg-surface px-3 py-3 space-y-2">
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
              <select value={addUserId} onChange={e => setAddUserId(e.target.value)}
                className="flex-1 border border-stroke-strong rounded-md px-2 py-1.5 text-sm bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand">
                <option value="">Добавить подписчика…</option>
                {available.map(u => <option key={u.id} value={u.id}>{u.displayName || u.email}</option>)}
              </select>
              <button onClick={handleAdd} disabled={!addUserId || add.isPending}
                className="flex items-center gap-1 text-sm px-3 py-1.5 border border-stroke-strong rounded-md hover:bg-base transition-colors disabled:opacity-50">
                <Plus size={13} /> Добавить
              </button>
            </div>
          )}
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
