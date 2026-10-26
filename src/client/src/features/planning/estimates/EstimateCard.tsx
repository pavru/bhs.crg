import { useState } from 'react';
import { Link } from 'react-router';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { ChevronLeft, Lock } from 'lucide-react';
import { useCan } from '@/shared/api/access';
import { costHidden, useEstimate, type Estimate, type EstimateRevision } from '@/shared/api/planningEstimates';
import { Button } from '@/shared/ui/Button';
import { Chip } from '@/shared/ui/Chip';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Tabs } from '@/shared/ui/Tabs';
import { useMedia, WIDE } from '@/shared/hooks/useMedia';
import { apiError } from '@/shared/utils/apiError';
import { formatCount, formatDate, formatMoney } from '@/shared/format/format';
import { KIND_LABEL, latestRevision, visibleRevisions } from './estimateRows';
import { MakeCurrentDialog } from './MakeCurrentDialog';
import { RevisionLines } from './RevisionLines';

/**
 * Карточка сметы (change `plan-09-estimate-ui`, макет П1, вариант 1): на месте, «‹ Сметы» — назад. Редакции —
 * вкладками: видны действующая и последние, остальные — в «ещё N» (у живой сметы их 5–10). Под вкладками —
 * полоса редакции и её строки.
 *
 * Права `read`, `edit`, `cost` независимы, недоступное скрыто (П1, §75 E): без `plan.estimate.edit` нет «Сделать
 * действующей». На телефоне — только чтение (решение П1): правок нет и там.
 */
export function EstimateCard({ estimateId, back }: { estimateId: string; back: { to: string; label: string } }) {
  const estimate = useEstimate(estimateId);
  const [chosen, setChosen] = useState<string | null>(null);

  if (estimate.isError) return (
    <div className="space-y-3">
      <BackLink back={back} />
      <p role="alert" className="text-sm text-danger">{apiError(estimate.error, 'Смета не прочиталась')}</p>
    </div>
  );
  if (!estimate.data) return <p className="text-sm text-fg4">Читаем смету…</p>;

  const data = estimate.data;
  const shown = data.revisions.find(r => r.id === chosen) ?? latestRevision(data);
  return (
    <div className="space-y-3">
      <BackLink back={back} />
      <div className="flex flex-wrap items-center gap-2">
        <h2 className="text-base font-semibold text-fg1 min-w-0 truncate">{data.name}</h2>
        <StatusBadge tone="neutral">{KIND_LABEL[data.kind]}</StatusBadge>
      </div>

      {shown === null ? (
        <p className="text-sm text-fg3">Редакций у сметы нет — их создаёт загрузка файла ЛСР.</p>
      ) : (
        <>
          <RevisionTabs estimate={data} selected={shown.id} onSelect={setChosen} />
          <RevisionBand estimate={data} revision={shown} />
          <RevisionLines key={shown.id} revision={shown} />
        </>
      )}
    </div>
  );
}

function BackLink({ back }: { back: { to: string; label: string } }) {
  return (
    <Link to={back.to} className="inline-flex items-center gap-1 text-sm text-brand hover:underline">
      <ChevronLeft size={16} aria-hidden /> {back.label}
    </Link>
  );
}

const revisionLabel = (r: EstimateRevision) =>
  `Ред. ${r.number}${r.current ? ' · действующая' : r.frozen ? ` · ${r.frozen.label}` : ''}`;

function RevisionTabs({ estimate, selected, onSelect }: {
  estimate: Estimate; selected: string; onSelect: (id: string) => void;
}) {
  const { tabs, more } = visibleRevisions(estimate, selected);
  return (
    <div className="flex items-center gap-2">
      <Tabs id={`estimate-${estimate.id}`} aria-label="Редакции сметы" className="flex-1 min-w-0"
        items={tabs.map(r => ({ value: r.id, label: revisionLabel(r) }))} value={selected} onChange={onSelect} />
      {more.length > 0 && (
        <DropdownMenu.Root>
          <DropdownMenu.Trigger asChild>
            <Chip menu className="shrink-0">ещё {formatCount(more.length)}</Chip>
          </DropdownMenu.Trigger>
          <DropdownMenu.Portal>
            <DropdownMenu.Content align="start" sideOffset={4}
              className="z-50 min-w-44 rounded-md border border-stroke bg-surface py-1"
              style={{ boxShadow: 'var(--f-shadow28)' }}>
              {more.map(r => (
                <DropdownMenu.Item key={r.id} onSelect={() => onSelect(r.id)}
                  className="flex items-center gap-2 px-3 py-1.5 text-sm text-fg2 cursor-pointer select-none outline-none data-[highlighted]:bg-base">
                  {revisionLabel(r)}
                  <span className="ml-auto text-xs text-fg4">{formatDate(r.importedAt)}</span>
                </DropdownMenu.Item>
              ))}
            </DropdownMenu.Content>
          </DropdownMenu.Portal>
        </DropdownMenu.Root>
      )}
    </div>
  );
}

/**
 * Полоса редакции: откуда она, сколько строк, действующая ли и чем зафиксирована. «Не действующая» — тоном
 * neutral, а не warning (§75 E): это не ошибка, а состояние, у живой сметы таких редакций большинство.
 */
function RevisionBand({ estimate, revision }: { estimate: Estimate; revision: EstimateRevision }) {
  const can = useCan();
  const wide = useMedia(WIDE);
  const [asked, setAsked] = useState<{ id: string; number: number } | null>(null);
  const editable = can.permission('plan.estimate.edit') && wide;
  const closed = costHidden(estimate.hidden);
  const current = estimate.revisions.find(r => r.current) ?? null;

  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-2 rounded-md bg-muted px-3 py-2 text-sm text-fg2">
      <span>
        {formatDate(revision.importedAt)}
        {revision.importedBy && ` · ${revision.importedBy}`}
        {revision.sourceFileName && ` · ${revision.sourceFileName}`}
        {` · ${formatCount(revision.rows)} строк`}
      </span>
      {revision.current
        ? <StatusBadge tone="brand">действующая</StatusBadge>
        : (
          <StatusBadge tone="neutral" title={current ? `План стройки считается по ред. ${current.number}` : 'Действующей редакции у сметы нет'}>
            не действующая
          </StatusBadge>
        )}
      {revision.frozen && (
        <StatusBadge tone="neutral" icon={Lock} title={`Зафиксирована ${formatDate(revision.frozen.at)}: из неё сделано «${revision.frozen.label}». Строки не правятся.`}>
          зафиксирована: {revision.frozen.label}
        </StatusBadge>
      )}
      {revision.partial && (
        <StatusBadge tone="warning" title="Редакция создана из неполного разбора файла: часть позиций не вошла">
          частично {formatCount(revision.partial.parsed)} из {formatCount(revision.partial.inFile)}
        </StatusBadge>
      )}
      {/* Итог — только с правом: без него суммы нет вовсе, причину называет колонка стоимости таблицы. */}
      {!closed && revision.cost && <span>итог <span className="font-medium text-fg1">{formatMoney(revision.cost.total)}</span></span>}

      {editable && !revision.current && (
        <Button variant="tonal" size="sm" className="ml-auto" onClick={() => setAsked({ id: revision.id, number: revision.number })}>
          Сделать действующей
        </Button>
      )}
      <MakeCurrentDialog estimateId={estimate.id} revision={asked} onClose={() => setAsked(null)} />
    </div>
  );
}
