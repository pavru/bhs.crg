import { useState } from 'react';
import { Users, ChevronDown, ChevronRight } from 'lucide-react';
import { useSubscribers, type SubscriptionScope } from '@/shared/api/subscriptions';
import { SCOPE_LABELS } from '@/shared/api/types';
import { SCOPE_COLORS } from './fields/constants';
import { SubscribersResource } from './SubscribersResource';

/**
 * Инлайн-коллапс «Подписчики» уровня стройка/раздел/комплект (внутри DocumentSetsPage). Тонкая обёртка
 * над общим `SubscribersResource` (issue #210). При постройке scope-страниц коллапс+чип уйдут, останется
 * `SubscribersResource` как detail-панель.
 */
export function SubscribersPanel({ scope, scopeId }: { scope: SubscriptionScope; scopeId: string }) {
  const [expanded, setExpanded] = useState(false);
  const { data: subscribers = [] } = useSubscribers(scope, scopeId);

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
        {expanded ? <ChevronDown size={13} className="text-fg4" /> : <ChevronRight size={13} className="text-fg4" />}
      </div>

      {expanded && (
        <div className="border-t border-stroke bg-surface px-3 py-3">
          <SubscribersResource scope={scope} scopeId={scopeId} />
        </div>
      )}
    </div>
  );
}
