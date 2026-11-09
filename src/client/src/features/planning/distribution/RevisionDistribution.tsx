import { useMemo, useState } from 'react';
import { Link } from 'react-router';
import {
  distributionRefusal, useDecideCarryOver, useDistribution, useDistributionVersion, useRereadDistribution, useWriteLines, useWriteRule,
  type CarryOverDecision, type CarryOverItem, type Distribution, type DistributionLine, type DistributionRefusal,
  type DistributionRule, type DistributionTarget, type DistributionWrite,
} from '@/shared/api/planningDistribution';
import { useRevisionRows, type EstimateRevision } from '@/shared/api/planningEstimates';
import { Banner } from '@/shared/ui/Banner';
import { Chip } from '@/shared/ui/Chip';
import { EmptyState } from '@/shared/ui/EmptyState';
import { Icon } from '@/shared/ui/Icon';
import { AccountTree, CallSplit, Undo, Warning } from '@/shared/ui/icons';
import { SearchInput } from '@/shared/ui/SearchInput';
import { SelectionBar } from '@/shared/ui/SelectionBar';
import { SNACKBAR_UNDO_MS, useSnackbar } from '@/shared/ui/Snackbar';
import { StatusText } from '@/shared/ui/StatusText';
import { apiError } from '@/shared/utils/apiError';
import { formatCount } from '@/shared/format/format';
import { CarryOverList } from './CarryOverList';
import { DistributionGrid } from './DistributionGrid';
import {
  conflictText, distGridLines, pendingItems, queueCounts, sectionNamer, targetText, type DistQueue, type TargetFilter,
} from './distributionView';
import { LineDialog } from './LineDialog';
import { SectionPickDialog } from './SectionPickDialog';
import { useDistributionEdit } from './useDistributionEdit';

/**
 * Режим «Распределение» карточки сметы (change `plan-10-distribution`, макет П2: основа — вариант 1, итог по целям
 * и явная «стройка в целом» — из 2 и 3). Запись — сразу, каждым действием; после правила раздела — снэкбар
 * «Отменить» по `previous` ответа.
 *
 * Правка — своим флагом (`useDistributionEdit`): и у зафиксированной редакции, с пометкой о графике. С одним
 * `read` видны правила и части, действий нет.
 */
export function RevisionDistribution({ revision, constructionId, initialQueue = 'all' }: {
  revision: EstimateRevision;
  constructionId: string;
  initialQueue?: DistQueue;
}) {
  const distribution = useDistribution(revision.id);
  const rows = useRevisionRows(revision.id);
  if (distribution.isError)
    return <p role="alert" className="text-body-medium text-error">{apiError(distribution.error, 'Распределение не прочиталось')}</p>;
  if (!distribution.data || !rows.data) return <p className="text-body-medium text-on-surface-variant">Читаем распределение…</p>;
  const d = distribution.data;
  if (!d.available) return (
    <p className="text-body-medium text-on-surface-variant">
      Смета составлена на раздел «{d.reason?.sectionName ?? 'удалённый раздел'}» — все её строки в нём, распределять их не нужно.
    </p>
  );
  if (d.targets.sections.length === 0) return (
    <EmptyState icon={<Icon icon={AccountTree} size={48} aria-hidden />} title="У стройки нет разделов"
      description="Распределять строки сметы не по чему: разделы заводятся в карточке стройки."
      action={<Link to={`/document-sets/${constructionId}`} className="text-body-medium text-primary hover:underline">Открыть стройку</Link>} />
  );
  return <DistributionView d={d} rows={rows.data} initialQueue={initialQueue} />;
}

function DistributionView({ d, rows, initialQueue }: {
  d: Distribution;
  rows: NonNullable<ReturnType<typeof useRevisionRows>['data']>;
  initialQueue: DistQueue;
}) {
  const editable = useDistributionEdit();
  const toast = useSnackbar();
  const writeRule = useWriteRule(d.revisionId);
  const writeLines = useWriteLines(d.revisionId);
  const decide = useDecideCarryOver(d.revisionId);
  const reread = useRereadDistribution(d.revisionId);
  const busy = writeRule.isPending || writeLines.isPending || decide.isPending;
  // Версия на момент «Отменить» — свежая, из кэша: снэкбар живёт дольше ответа, после которого появился.
  const versionNow = useDistributionVersion(d.revisionId);

  const [queue, setQueue] = useState<DistQueue>(initialQueue);
  const [target, setTarget] = useState<TargetFilter>(null);
  const [query, setQuery] = useState('');
  const [opened, setOpened] = useState<ReadonlySet<string>>(new Set());
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [splitting, setSplitting] = useState<string | null>(null);
  const [picking, setPicking] = useState(false);
  const [refusal, setRefusal] = useState<DistributionRefusal | null>(null);

  const name = useMemo(() => sectionNamer(d), [d]);
  const counts = queueCounts(d);
  const lines = useMemo(() => distGridLines(d, queue, query, target, opened), [d, queue, query, target, opened]);
  const byRow = useMemo(() => new Map(rows.items.map(r => [r.id, r])), [rows.items]);
  const filtering = queue !== 'all' || query.trim() !== '' || target !== null;
  const chosen = d.lines.filter(l => selected.has(l.rowId));
  const items = pendingItems(d);

  const failed = (e: unknown) => {
    const r = distributionRefusal(e);
    if (r.kind === 'conflict' || r.kind === 'balance') setRefusal(r); else toast.error(r.text);
  };

  const ruleBody = (rule: DistributionRule, next: DistributionTarget | null, reset = false) =>
    ({ lsrSectionCode: rule.lsrSectionCode, target: next, resetExceptions: reset });
  const undo = (done: DistributionWrite) => {
    const prev = done.previous;
    if (!prev.rule) return;
    writeRule.mutate({
      body: { lsrSectionCode: prev.rule.lsrSectionCode, target: prev.rule.target, lines: prev.lines.length > 0 ? prev.lines : undefined },
      version: versionNow() ?? d.version,
    }, { onSuccess: () => toast.info('Правило возвращено'), onError: failed });
  };
  const onRule = (rule: DistributionRule, next: DistributionTarget | null, reset = false) => {
    setRefusal(null);
    writeRule.mutate({ body: ruleBody(rule, next, reset), version: d.version }, {
      onSuccess: done => toast.success(
        reset ? `Раздел ${rule.lsrSectionCode ?? '«вне разделов»'}: исключения сброшены`
          : `Раздел ${rule.lsrSectionCode ?? '«вне разделов»'} → ${targetText(next, sectionNamer(done.distribution))}`,
        { action: { label: 'Отменить', onClick: () => undo(done) }, duration: SNACKBAR_UNDO_MS }),
      onError: failed,
    });
  };
  const toRule = () => {
    const own = chosen.filter(l => l.source === 'parts');
    if (own.length === 0) { toast.info('У выбранных строк своих частей нет — они уже по правилу раздела'); return; }
    setRefusal(null);
    writeLines.mutate({ body: own.map(l => ({ rowId: l.rowId, unit: 'row', parts: null })), version: d.version }, {
      onSuccess: () => { toast.success(`К правилу раздела: ${formatCount(own.length)}`); setSelected(new Set()); },
      onError: failed,
    });
  };
  const onDecide = (item: CarryOverItem, decision: CarryOverDecision) => {
    setRefusal(null);
    decide.mutate({ body: { itemId: item.id, decision }, version: d.version }, { onError: failed });
  };
  const pickQueue = (next: DistQueue) => { setQueue(prev => (prev === next ? 'all' : next)); setSelected(new Set()); };
  const pickTarget = (next: TargetFilter) => setTarget(prev => (JSON.stringify(prev) === JSON.stringify(next) ? null : next));
  const select = (ids: string[], on: boolean) => setSelected(prev => {
    const n = new Set(prev);
    for (const id of ids) if (on) n.add(id); else n.delete(id);
    return n;
  });
  const splitLine: DistributionLine | undefined = splitting ? d.lines.find(l => l.rowId === splitting) : undefined;

  return (
    <div className="space-y-3">
      <Notes d={d} />
      {d.carryOver && d.carryOver.pending > 0 && queue !== 'carry-over' && (
        <Banner tone="warning" actions={[{ label: 'Проверить', onClick: () => pickQueue('carry-over') }]}>
          Распределение перенесено из ред. {d.carryOver.counts.sourceNumber ?? '—'}: без пометки —{' '}
          {formatCount(d.carryOver.counts.carried)} строк. Проверьте {formatCount(d.carryOver.pending)}.
        </Banner>
      )}
      {refusal?.kind === 'conflict' && (
        <Banner tone="warning" actions={[{ label: 'Перечитать', onClick: () => { void reread(); setRefusal(null); } }]}>
          {conflictText(refusal)}
        </Banner>
      )}
      {refusal?.kind === 'balance' && <Banner tone="error">{refusal.text}</Banner>}

      <Totals d={d} target={target} onPick={pickTarget} />

      <SelectionBar count={chosen.length} onClear={() => setSelected(new Set())} actions={[
        { label: 'На раздел…', icon: CallSplit, onClick: () => setPicking(true), variant: 'filled' },
        { label: 'К правилу', icon: Undo, onClick: toRule },
      ]}>
        <div className="flex flex-wrap items-center gap-2">
          <SearchInput value={query} onChange={setQuery} placeholder="№ или наименование" ariaLabel="Поиск строки по номеру или наименованию"
            className="w-64 max-w-full" />
          <Chip selected={queue === 'all'} count={formatCount(d.counts.rows)} onClick={() => pickQueue('all')}>Все</Chip>
          {counts.undistributed > 0 && (
            <Chip selected={queue === 'undistributed'} count={formatCount(counts.undistributed)} onClick={() => pickQueue('undistributed')}>Не распределено</Chip>
          )}
          {counts.partial > 0 && (
            <Chip selected={queue === 'partial'} count={formatCount(counts.partial)} onClick={() => pickQueue('partial')}>Частично</Chip>
          )}
          {counts.exceptions > 0 && (
            <Chip selected={queue === 'exceptions'} count={formatCount(counts.exceptions)} onClick={() => pickQueue('exceptions')}>Исключения</Chip>
          )}
          {counts['carry-over'] > 0 && (
            <Chip selected={queue === 'carry-over'} count={formatCount(counts['carry-over'])} onClick={() => pickQueue('carry-over')}>Перенос: проверить</Chip>
          )}
        </div>
      </SelectionBar>

      {queue === 'carry-over' ? (
        <CarryOverList distribution={d} items={items} name={name} busy={busy}
          onDecide={editable ? onDecide : null} onOpenLine={editable ? setSplitting : null} />
      ) : (
        <DistributionGrid distribution={d} lines={lines} rows={byRow} hidden={rows.hidden} name={name} filtering={filtering}
          onToggle={key => setOpened(prev => { const n = new Set(prev); if (n.has(key)) n.delete(key); else n.add(key); return n; })}
          actions={{
            onRule: editable ? (rule, next) => onRule(rule, next) : null,
            onReset: editable ? rule => onRule(rule, rule.target, true) : null,
            onSplit: editable ? l => setSplitting(l.rowId) : null,
            selected, onSelect: editable ? select : null, busy,
          }} />
      )}

      {splitLine && <LineDialog distribution={d} line={splitLine} onClose={() => setSplitting(null)} />}
      {picking && (
        <SectionPickDialog distribution={d} lines={chosen} onClose={() => setPicking(false)}
          onDone={() => { setPicking(false); setSelected(new Set()); }} />
      )}
    </div>
  );
}

/**
 * Слова о редакции: не действующая — «на план не влияет»; зафиксированная — правится, но график получит пометку
 * (§77 C). Тоном neutral: это состояние, а не ошибка.
 */
function Notes({ d }: { d: Distribution }) {
  if (d.current && !d.frozen) return null;
  return (
    <div className="flex flex-wrap gap-x-3 gap-y-1">
      {!d.current && <StatusText tone="neutral">Редакция не действующая: её распределение на план стройки не влияет</StatusText>}
      {d.frozen && (
        <StatusText tone="neutral">Редакция зафиксирована: правка распределения пометит график «распределение изменено после построения»</StatusText>
      )}
    </div>
  );
}

/**
 * Итог по целям строкой над таблицей (из вариантов 2 и 3 П2): разделы стройки, «стройка в целом», «не
 * распределено». Щелчок ставит отбор — «что в корпусе» без матрицы. Удалённая цель — тоном warning.
 */
function Totals({ d, target, onPick }: { d: Distribution; target: TargetFilter; onPick: (t: TargetFilter) => void }) {
  const is = (t: TargetFilter) => JSON.stringify(t) === JSON.stringify(target);
  return (
    <div role="group" aria-label="Итог по целям" className="flex flex-wrap items-center gap-2">
      {d.targets.sections.filter(s => s.rows > 0 || !s.deleted).map(s => {
        const t: TargetFilter = { kind: 'section', sectionId: s.sectionId };
        return (
          s.deleted ? (
            <Chip key={s.sectionId} selected={is(t)} count={formatCount(s.rows)} onClick={() => onPick(t)} icon={Warning} iconTone="warning"
              iconLabel="Раздела в стройке больше нет">
              {s.name ?? 'удалённый раздел'}
            </Chip>
          ) : (
            <Chip key={s.sectionId} selected={is(t)} count={formatCount(s.rows)} onClick={() => onPick(t)}>{s.name}</Chip>
          )
        );
      })}
      <Chip selected={is({ kind: 'whole' })} count={formatCount(d.targets.wholeRows)} onClick={() => onPick({ kind: 'whole' })}>
        стройка в целом
      </Chip>
      <Chip selected={is({ kind: 'undistributed' })} count={formatCount(d.targets.undistributedRows)}
        onClick={() => onPick({ kind: 'undistributed' })}>
        не распределено
      </Chip>
    </div>
  );
}
