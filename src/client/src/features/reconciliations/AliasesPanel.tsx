import { useState } from 'react';
import { Link2, Link2Off, Check, X } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useToast } from '@/shared/ui/Toast';
import {
  useAliases, useReviewAlias, useDeleteAlias,
  ALIAS_STATUS_LABELS, type ReconciliationAlias,
} from '@/shared/api/reconciliations';

/**
 * Алиасы позиций (issue #446): утверждения, что два по-разному записанных наименования обозначают
 * одно и то же — знание, которое в ручном процессе держит в голове человек.
 *
 * В сравнении участвуют ТОЛЬКО подтверждённые, и это видно: «Применяется» против «Предложено».
 */

const STATUS_STYLE: Record<ReconciliationAlias['status'], string> = {
  Confirmed: 'bg-brand-subtle text-brand',
  Proposed: 'bg-warning-subtle text-warning',
  Rejected: 'bg-muted text-fg4',
};

export function AliasesPanel() {
  const { data: items = [], isLoading } = useAliases();
  const review = useReviewAlias();
  const remove = useDeleteAlias();
  const toast = useToast();
  const [breaking, setBreaking] = useState<ReconciliationAlias | null>(null);

  return (
    <div className="flex-1 min-w-0 flex flex-col">
      <div className="px-4 py-3 border-b border-stroke shrink-0">
        <h2 className="text-base font-semibold text-fg1 flex items-center gap-2">
          <Link2 size={16} className="text-fg3" /> Алиасы позиций
        </h2>
        <p className="text-xs text-fg3 mt-0.5">
          Наименования, признанные одним и тем же. В сверке участвуют только применяемые.
        </p>
      </div>

      <div className="flex-1 min-h-0 overflow-y-auto">
        {isLoading && <p className="px-4 py-3 text-sm text-fg4">Загрузка…</p>}
        {!isLoading && items.length === 0 && (
          <div className="p-6">
            <EmptyState icon={<Link2 size={32} />} title="Алиасов нет"
              description="Свяжите две позиции в списке находок, если они называются по-разному, но означают одно." />
          </div>
        )}
        {items.map(a => (
          <div key={a.id} className="px-4 py-3 border-b border-stroke group">
            <div className="flex items-center gap-2">
              <span className={`text-[11px] px-1.5 py-0.5 rounded-full shrink-0 ${STATUS_STYLE[a.status]}`}>
                {ALIAS_STATUS_LABELS[a.status]}
              </span>
              <span className="min-w-0 flex-1 text-sm text-fg1 truncate">
                {a.aliasLabel} <span className="text-fg4">→</span> {a.canonicalLabel}
              </span>
              {a.status !== 'Confirmed' && (
                <Button variant="text" size="sm" className="shrink-0"
                  onClick={async () => {
                    await review.mutateAsync({ id: a.id, status: 'Confirmed' });
                    toast.success('Применяется. Прогоните сверку, чтобы позиции слились');
                  }}>
                  <Check size={14} /> Применять
                </Button>
              )}
              {a.status !== 'Rejected' && (
                <Button variant="text" size="sm" className="shrink-0 opacity-0 group-hover:opacity-100"
                  onClick={async () => {
                    await review.mutateAsync({ id: a.id, status: 'Rejected' });
                    toast.info('Отклонено');
                  }}>
                  <X size={14} /> Отклонить
                </Button>
              )}
              <Button variant="text" size="sm" danger className="shrink-0 opacity-0 group-hover:opacity-100"
                onClick={() => setBreaking(a)}>
                <Link2Off size={14} /> Разорвать
              </Button>
            </div>
            {(a.note || a.confirmedBy || a.proposedBy) && (
              <p className="text-[11px] text-fg4 mt-0.5">
                {a.note}
                {a.note && (a.confirmedBy || a.proposedBy) ? ' · ' : ''}
                {a.confirmedBy ? `подтвердил ${a.confirmedBy}` : a.proposedBy ? `предложил ${a.proposedBy}` : ''}
              </p>
            )}
          </div>
        ))}
      </div>

      <ConfirmDialog
        open={!!breaking}
        title="Разорвать связь позиций?"
        // Последствие неочевидно и проявится не сразу — говорим прямо.
        description="В следующем прогоне эти позиции снова разъедутся на две находки."
        confirmLabel="Разорвать"
        onOpenChange={o => { if (!o) setBreaking(null); }}
        onConfirm={async () => {
          await remove.mutateAsync(breaking!.id);
          setBreaking(null);
          toast.success('Связь разорвана');
        }}
      />
    </div>
  );
}
