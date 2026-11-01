import { Icon } from '@/shared/ui/Icon';
import { Upload } from '@/shared/ui/icons';
import { useConstructionImports, waitingDecision, type EstimateImportItem } from '@/shared/api/planningImports';
import { Button } from '@/shared/ui/Button';
import { StatusText } from '@/shared/ui/StatusText';
import { formatDate } from '@/shared/format/format';

type Mark = { text: string; tone: 'neutral' | 'warning' | 'error' };

const STATE: Record<Exclude<EstimateImportItem['state'], 'committed'>, Mark> = {
  parsing: { text: 'разбирается', tone: 'neutral' },
  parsed: { text: 'ждёт решения', tone: 'warning' },
  rejected: { text: 'не разобран', tone: 'error' },
};
/** Прерванный разбор остаётся `parsing`, но исхода не дождаться — не «разбирается», а отказ. */
const INTERRUPTED: Mark = { text: 'разбор прерван', tone: 'error' };

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
          <li key={i.id} className="flex items-center gap-2 rounded-sm bg-surface-container px-3 py-1.5 text-body-medium text-on-surface-variant">
            <Icon icon={Upload} size={18} className="shrink-0 text-on-surface-variant" aria-hidden />
            <span className="min-w-0 flex-1">
              Импорт <b className="font-medium text-on-surface break-all">{i.fileName}</b>
              {' · '}{i.target.newEstimate ? `новая смета «${i.target.newEstimate.name}»` : `в смету «${i.target.estimateName ?? '…'}»`}
              <span className="text-on-surface-variant"> · {i.createdBy ? `${i.createdBy}, ` : ''}{formatDate(i.createdAt)}</span>{' '}
              <StatusText tone={state.tone}>{state.text}</StatusText>
            </span>
            <Button variant="text" className="shrink-0" onClick={() => onOpen(i.id)}>Открыть</Button>
          </li>
        );
      })}
    </ul>
  );
}
