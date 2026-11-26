import type { ScheduleFact } from '@/shared/api/planningScheduleTypes';
import type { FactDiagnostics } from '@/shared/api/progress';
import { formatDate, formatQuantityUnit } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { LinkOff, VisibilityOff } from '@/shared/ui/icons';
import { Tooltip } from '@/shared/ui/Tooltip';
import { ProgressHidden } from '../progress/ProgressStrips';

const crewReasons: Record<FactDiagnostics['crewExclusions'][number]['reason'], string> = {
  NotApproved: 'итог не утверждён',
  Stale: 'итог устарел',
  AwaitingAcceptance: 'отчёты ждут приёмки',
  InvalidKey: 'негодная позиция перечня или единица',
  MultipleKeys: 'разные позиции, разделы или единицы',
};

function periodText({ from, to }: FactDiagnostics['scope']) {
  if (from && to) return `с ${formatDate(from)} по ${formatDate(to)}`;
  if (from) return `с ${formatDate(from)}`;
  if (to) return `по ${formatDate(to)}`;
  return 'за весь период';
}

export function ScheduleFactNotice({ fact, diagnostics }: {
  fact: ScheduleFact | null | undefined; diagnostics?: FactDiagnostics | null;
}) {
  if (fact?.state === 'not-connected') return <Banner tone="info" icon={LinkOff}>Учёт работ не подключён</Banner>;
  if (fact?.state === 'hidden') return <Banner tone="info" icon={VisibilityOff}>
    Факт по учёту · <ProgressHidden permission={fact.permission} />
  </Banner>;
  const partial = fact?.state === 'partial';
  if (!diagnostics || (!partial && !(fact?.state === 'ok' && diagnostics.unsubmitted > 0))) return null;
  return <Banner tone="warning">
    {partial && <strong className="block text-label-large">Факт неполный</strong>}
    <span className="block">Диагностика факта по всей стройке {periodText(diagnostics.scope)}</span>
    {diagnostics.awaitingAcceptance > 0 && <span className="block">Отчётов ожидают приёмки: {diagnostics.awaitingAcceptance}</span>}
    {diagnostics.crewExclusions.map(({ reason, groups }) => <span key={reason} className="block">
      Бригадных групп — {crewReasons[reason]}: {groups}
    </span>)}
    {diagnostics.withoutPlanItem > 0 && <span className="block">Строк без корректной позиции перечня: {diagnostics.withoutPlanItem}</span>}
    {diagnostics.unsubmitted > 0 && <span className="block">Неподанные участники по бригадным группам: {diagnostics.unsubmitted}</span>}
  </Banner>;
}

export function ScheduleFactCell({ fact, unitName }: { fact?: ScheduleFact | null; unitName: string | null }) {
  if (fact?.state === 'hidden') return <span className="text-on-surface-variant">Скрыто правами</span>;
  if (fact?.state === 'partial' || fact?.state === 'ok') {
    if (fact.volume != null) return <span className="tabular-nums wrap-anywhere">{formatQuantityUnit(fact.volume, unitName, 4)}</span>;
    const text = fact.state === 'partial' ? 'Нет включённого объёма' : 'Нет учтённого объёма';
    return <Tooltip content={text}><span aria-label={text} className="text-on-surface-variant">—</span></Tooltip>;
  }
  return null;
}
