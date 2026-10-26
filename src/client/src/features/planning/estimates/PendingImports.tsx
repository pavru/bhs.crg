import { Upload } from 'lucide-react';
import { useConstructionImports, waitingDecision, type EstimateImportItem } from '@/shared/api/planningImports';
import { Button } from '@/shared/ui/Button';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { formatDate } from '@/shared/format/format';

type Badge = { text: string; tone: 'neutral' | 'warning' | 'danger' };

const STATE: Record<Exclude<EstimateImportItem['state'], 'committed'>, Badge> = {
  parsing: { text: 'разбирается', tone: 'neutral' },
  parsed: { text: 'ждёт решения', tone: 'warning' },
  rejected: { text: 'не разобран', tone: 'danger' },
};
/** Прерванный разбор остаётся `parsing`, но исхода не дождаться — не «разбирается», а отказ. */
const INTERRUPTED: Badge = { text: 'разбор прерван', tone: 'danger' };

/**
 * Импорты, закрытые без решения (макет П1 1а, ответ листа 0): полосой над сметами, «Открыть» открывает окно
 * загрузки снова — на отчёте. Видны только тому, кто импортирует (`plan.estimate.edit`): у читающего
 * адреса импортов закрыты, и «ждёт решения» ему не по силам.
 */
export function PendingImports({ constructionId, enabled, only, onOpen }: {
  constructionId: string;
  enabled: boolean;
  /** Какие импорты здесь уместны: на странице раздела — его сметы, в карточке сметы — её. */
  only?: (item: EstimateImportItem) => boolean;
  onOpen: (importId: string) => void;
}) {
  const imports = useConstructionImports(constructionId, enabled);
  const waiting = (imports.data ?? []).filter(i => waitingDecision(i) && (!only || only(i)));
  if (!enabled || waiting.length === 0) return null;
  return (
    <ul className="space-y-1.5" aria-label="Импорты, ждущие решения">
      {waiting.map(i => {
        const state = i.interrupted ? INTERRUPTED : STATE[i.state as keyof typeof STATE];
        return (
          <li key={i.id} className="flex items-center gap-2 rounded-md bg-muted px-3 py-1.5 text-sm text-fg2">
            <Upload size={16} className="shrink-0 text-fg3" aria-hidden />
            <span className="min-w-0 flex-1">
              Импорт <b className="font-medium text-fg1 break-all">{i.fileName}</b>
              {' · '}{i.target.newEstimate ? `новая смета «${i.target.newEstimate.name}»` : `в смету «${i.target.estimateName ?? '…'}»`}
              <span className="text-fg3"> · {i.createdBy ? `${i.createdBy}, ` : ''}{formatDate(i.createdAt)}</span>{' '}
              <StatusBadge tone={state.tone}>{state.text}</StatusBadge>
            </span>
            <Button variant="text" size="sm" className="shrink-0" onClick={() => onOpen(i.id)}>Открыть</Button>
          </li>
        );
      })}
    </ul>
  );
}
