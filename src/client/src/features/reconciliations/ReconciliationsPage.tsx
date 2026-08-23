import { useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import {
  Plus, Play, AlertTriangle, CheckCircle2, CircleSlash, History, Scale, Bot, FileSpreadsheet, Link2,
} from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useListConstructions } from '@/shared/api/constructions';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { useToast } from '@/shared/ui/Toast';
import { ListDetailShell, NavSearchInput, NavItem, NavSection } from '@/shared/ui/ListDetailShell';
import { useAuth } from '@/shared/hooks/useAuth';
import {
  useReconciliations, useReconciliationRuns, useFindings, useRunReconciliation,
  useDeleteReconciliation, useSetDecision, useRemoveDecision, downloadDiscrepancyReport,
  useAliases, useCreateAlias, isUnmatched, provenanceSummary,
  STATUS_LABELS, DECISION_LABELS, needsAttention, runSummary,
  type Finding, type ReconciliationRun,
} from '@/shared/api/reconciliations';
import { ReconciliationEditor } from './ReconciliationEditor';
import { DecisionDialog } from './DecisionDialog';
import { ObservationsPanel } from './ObservationsPanel';
import { AliasesPanel } from './AliasesPanel';
import { useObservations, isUnreviewed } from '@/shared/api/observations';

/**
 * Сверка на непротиворечивость (issue #414, фаза Ф1 — экран находок #436).
 *
 * До этой страницы результаты анализа жили только в переписке с внешним агентом: память между
 * прогонами не копилась, отчёт не с чем было сравнить. Здесь они становятся объектом системы.
 */

function StatusBadge({ f }: { f: Finding }) {
  if (f.resolved) {
    return (
      <span className="inline-flex items-center gap-1 text-xs px-1.5 py-0.5 rounded-full bg-brand-subtle text-brand shrink-0">
        <CheckCircle2 size={12} /> Устранено
      </span>
    );
  }
  if (f.status === 'Match') {
    return <span className="text-xs text-fg4 shrink-0">Совпадает</span>;
  }
  // Решение снимает вопрос, даже если расхождение осталось: в этом и смысл персистентного решения —
  // «давальческое оборудование» не должно всплывать каждый прогон.
  const muted = !!f.decision;
  return (
    <span className={`inline-flex items-center gap-1 text-xs px-1.5 py-0.5 rounded-full shrink-0 ${
      muted ? 'bg-muted text-fg3' : 'bg-danger-subtle text-danger'}`}>
      {muted ? <CircleSlash size={12} /> : <AlertTriangle size={12} />} {STATUS_LABELS[f.status]}
    </span>
  );
}

function num(v: number | null): string {
  return v == null ? '—' : String(Math.round(v * 1000) / 1000);
}

function FindingRow({ f, onDecide, linkable, checked, onToggle }: {
  f: Finding; onDecide: (f: Finding) => void;
  linkable: boolean; checked: boolean; onToggle: () => void;
}) {
  const left = f.provenance.left;
  const right = f.provenance.right;
  return (
    <div className="px-3 py-2 border-b border-stroke hover:bg-muted/40 group">
      <div className="flex items-center gap-2">
        {/* Связывать имеет смысл только позиции, не нашедшие пары: у остальных пара уже есть. */}
        {linkable && (
          <input type="checkbox" checked={checked} onChange={onToggle}
            aria-label={`Выбрать «${f.label}» для связывания`} className="shrink-0 accent-brand" />
        )}
        <StatusBadge f={f} />
        <span className="flex-1 truncate text-sm text-fg1" title={f.label}>{f.label}</span>
        <span className="text-sm tabular-nums text-fg2 shrink-0">
          {num(f.leftValue)} <span className="text-fg4">/</span> {num(f.rightValue)}
        </span>
        <Button variant="text" size="sm" onClick={() => onDecide(f)}
          className="opacity-0 group-hover:opacity-100 focus:opacity-100 shrink-0">
          {f.decision ? 'Изменить' : 'Разобрать'}
        </Button>
      </div>
      <div className="flex items-center gap-3 mt-0.5 text-[11px] text-fg4">
        {/* Провенанс: без него находку нельзя проверить глазами по документу. */}
        {provenanceSummary(left) && <span>слева: {provenanceSummary(left)}</span>}
        {provenanceSummary(right) && <span>справа: {provenanceSummary(right)}</span>}
        {f.decision && (
          <span className="text-fg3">
            {DECISION_LABELS[f.decision.kind]}
            {f.decision.note ? ` — ${f.decision.note}` : ''}
            {f.decision.decidedBy ? ` (${f.decision.decidedBy})` : ''}
          </span>
        )}
      </div>
    </div>
  );
}

function RunPicker({ runs, value, onChange }: {
  runs: ReconciliationRun[]; value: string | null; onChange: (v: string | null) => void;
}) {
  if (runs.length === 0) return null;
  return (
    <Select value={value ?? runs[0]?.id ?? ''} onValueChange={onChange} aria-label="Прогон"
      className="w-56">
      {runs.map((r, i) => (
        <SelectItem key={r.id} value={r.id}>
          {new Date(r.startedAt).toLocaleString('ru-RU')}{i === 0 ? ' — последний' : ''}
        </SelectItem>
      ))}
    </Select>
  );
}

export function ReconciliationsPage() {
  const { user } = useAuth();
  const isAdmin = user?.role === 'Admin';
  const toast = useToast();

  const [search, setSearch] = useState('');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [runId, setRunId] = useState<string | null>(null);
  const [editing, setEditing] = useState<'new' | string | null>(null);
  const [deleting, setDeleting] = useState<string | null>(null);
  const [deciding, setDeciding] = useState<Finding | null>(null);
  const [onlyAttention, setOnlyAttention] = useState(false);
  // Замечания агента — вторая секция того же раздела: оба отвечают «что не так с комплектом».
  const [view, setView] = useState<'reconciliation' | 'observations' | 'aliases'>('reconciliation');
  // Связывание позиций: выбираем ровно две несопоставленные находки.
  const [linking, setLinking] = useState<string[]>([]);

  /**
   * Вход снаружи: `?id=<сверка>` и `?view=observations|aliases` (#731). Вкладка «Проблемы»
   * комплекта отсылает сюда за тем, чего там нет — историей прогонов, алиасами, чисткой журнала, —
   * и приводить на пустой экран «Выберите сверку» после явного «Открыть в „Сверке“» нельзя.
   *
   * Эффектом, а не только начальным значением состояния: смена одной строки запроса компонент не
   * размонтирует, и вторая такая ссылка подряд иначе ничего бы не сделала. Дальше выбор ведёт
   * обычное состояние — держать его в адресе целиком эта страница не умеет.
   */
  const [searchParams] = useSearchParams();
  useEffect(() => {
    const id = searchParams.get('id');
    const requested = searchParams.get('view');
    if (requested === 'observations' || requested === 'aliases') setView(requested);
    else if (id) { setView('reconciliation'); setSelectedId(id); setRunId(null); }
  }, [searchParams]);

  const { data: items = [], isLoading } = useReconciliations();
  const { data: observations = [] } = useObservations();
  const { data: constructions = [] } = useListConstructions();
  const { data: aliases = [] } = useAliases();
  const createAlias = useCreateAlias();

  // Отчёт собирается по КОМПЛЕКТУ — как и тот, что сегодня ведут руками.
  const sets = useMemo(
    () => constructions.flatMap(c => (c.sections ?? []).flatMap(sec =>
      (sec.documentSets ?? []).map(ds => ({ id: ds.id, label: `${c.name} / ${sec.name} / ${ds.name}` })))),
    [constructions]);
  const [reportSetId, setReportSetId] = useState<string>('');
  const [reportBusy, setReportBusy] = useState(false);

  async function downloadReport() {
    const target = sets.find(s => s.id === reportSetId) ?? sets[0];
    if (!target) return;
    setReportBusy(true);
    try {
      await downloadDiscrepancyReport(target.id, target.label);
      toast.success('Отчёт выгружен');
    } catch (e) {
      toast.apiError(e, 'Не удалось выгрузить отчёт');
    } finally {
      setReportBusy(false);
    }
  }
  const { data: runs = [] } = useReconciliationRuns(selectedId);
  const { data: findings = [], isLoading: findingsLoading } = useFindings(selectedId, runId);

  const run = useRunReconciliation();
  const remove = useDeleteReconciliation();
  const setDecision = useSetDecision();
  const removeDecision = useRemoveDecision();

  const selected = items.find(i => i.id === selectedId) ?? null;
  const currentRun = runs.find(r => r.id === (runId ?? runs[0]?.id)) ?? runs[0] ?? null;

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    return q ? items.filter(i => i.name.toLowerCase().includes(q)) : items;
  }, [items, search]);

  // Открытая сверка, не прошедшая поиск, остаётся в рейле отдельной строкой (issue #792): иначе
  // деталь показывает её, а в списке ни строки, ни подсветки.
  const outsideSearch = selected && !filtered.some(i => i.id === selected.id) ? selected : null;

  const attentionCount = findings.filter(needsAttention).length;
  const shown = onlyAttention ? findings.filter(needsAttention) : findings;

  async function onRun() {
    if (!selectedId) return;
    const result = await run.mutateAsync(selectedId);
    setRunId(null); // после прогона смотрим последний
    if (result.status === 'Failed') toast.error(result.error ?? 'Прогон не выполнен');
    else toast.success(runSummary(result));
  }

  const reconciliationRow = (i: typeof items[number]) => (
    <NavItem key={i.id} icon={<Scale size={16} />} label={i.name}
      active={view === 'reconciliation' && i.id === selectedId}
      onClick={() => { setView('reconciliation'); setSelectedId(i.id); setRunId(null); }} />
  );

  const nav = (
    <>
      <NavSearchInput value={search} onChange={setSearch} placeholder="Поиск сверки…" />
      <div className="flex-1 overflow-y-auto px-2 pb-2">
        {outsideSearch && (
          <>
            <NavSection label="Открыта, вне поиска" />
            {reconciliationRow(outsideSearch)}
          </>
        )}
        {filtered.length > 0 && <NavSection label="Сверки" />}
        {filtered.map(reconciliationRow)}
        {filtered.length === 0 && !isLoading && (
          <p className="px-3 py-2 text-sm text-fg4">
            {outsideSearch ? 'Больше ничего не найдено' : 'Сверок нет'}
          </p>
        )}

        {/* Отдельной секцией, а не в общем списке: находка сверки — результат арифметики,
            замечание — утверждение агента. Смешать значит выдать одно за другое. */}
        <NavSection label="Внешний анализ" />
        <NavItem icon={<Bot size={16} />} label="Замечания агента"
          count={observations.filter(isUnreviewed).length}
          active={view === 'observations'}
          onClick={() => setView('observations')} />
        <NavItem icon={<Link2 size={16} />} label="Алиасы позиций"
          count={aliases.filter(a => a.status === 'Proposed').length}
          active={view === 'aliases'}
          onClick={() => setView('aliases')} />
      </div>
    </>
  );

  const detail = view === 'observations' ? <ObservationsPanel />
    : view === 'aliases' ? <AliasesPanel />
    : !selected ? (
    <EmptyState icon={<Scale size={32} />} title="Выберите сверку"
      description="Сверка сопоставляет два источника по доменному ключу и показывает расхождения." />
  ) : (
    <div className="flex-1 min-w-0 flex flex-col">
      <div className="flex items-center gap-2 px-4 py-3 border-b border-stroke shrink-0">
        <div className="min-w-0 flex-1">
          <h2 className="text-base font-semibold text-fg1 truncate">{selected.name}</h2>
          <p className="text-xs text-fg3 mt-0.5">
            {currentRun ? runSummary(currentRun) : 'Прогонов ещё не было'}
          </p>
        </div>
        {runs.length > 1 && (
          <div className="shrink-0 flex items-center gap-1.5 text-fg4">
            <History size={14} />
            <RunPicker runs={runs} value={runId} onChange={setRunId} />
          </div>
        )}
        <Button variant="filled" onClick={onRun} disabled={run.isPending} className="shrink-0">
          <Play size={14} /> {run.isPending ? 'Считаю…' : 'Прогнать'}
        </Button>
        {isAdmin && (
          <RowActionsMenu actions={[
            { key: 'edit', label: 'Изменить', onSelect: () => setEditing(selected.id) },
            { key: 'delete', label: 'Удалить', danger: true, onSelect: () => setDeleting(selected.id) },
          ]} />
        )}
      </div>

      {/* Неудачный прогон обязан быть виден: пустой список читается как «расхождений нет», и это
          самое опасное недоразумение в подсистеме. */}
      {currentRun?.status === 'Failed' && (
        <div className="mx-4 mt-3 px-3 py-2 rounded-lg bg-danger-subtle text-danger text-sm">
          <strong>Прогон не выполнен.</strong> {currentRun.error}
        </div>
      )}

      {currentRun?.status === 'Completed' && (
        <div className="flex items-center gap-2 px-4 py-2 border-b border-stroke text-xs shrink-0">
          <button type="button" onClick={() => setOnlyAttention(v => !v)}
            className={`px-2 py-1 rounded-full transition-colors ${
              onlyAttention ? 'bg-brand-subtle text-brand font-medium' : 'text-fg3 hover:bg-muted'}`}>
            Требует внимания: {attentionCount}
          </button>
          <span className="text-fg4">
            совпало {currentRun.matchCount} · расхождений {currentRun.mismatchCount} ·
            нет слева {currentRun.missingLeftCount} · нет справа {currentRun.missingRightCount}
          </span>
        </div>
      )}

      {linking.length > 0 && (
        <div className="flex items-center gap-2 px-4 py-2 border-b border-stroke bg-brand-subtle shrink-0">
          <Link2 size={14} className="text-brand shrink-0" />
          <span className="text-xs text-brand flex-1 truncate">
            {linking.length === 1
              ? 'Выберите вторую позицию, чтобы связать их как одну'
              : findings.filter(f => linking.includes(f.id)).map(f => f.label).join('  →  ')}
          </span>
          <Button variant="text" size="sm" onClick={() => setLinking([])}>Отмена</Button>
          <Button variant="filled" size="sm" disabled={linking.length !== 2}
            onClick={async () => {
              const [a, b] = linking.map(id => findings.find(f => f.id === id)!);
              await createAlias.mutateAsync({
                aliasKey: a.key, aliasLabel: a.label,
                canonicalKey: b.key, canonicalLabel: b.label,
              });
              setLinking([]);
              toast.success('Связано. Прогоните сверку, чтобы позиции слились');
            }}>
            Связать
          </Button>
        </div>
      )}

      <div className="flex-1 min-h-0 overflow-y-auto">
        {findingsLoading && <p className="px-4 py-3 text-sm text-fg4">Загрузка…</p>}
        {!findingsLoading && shown.length === 0 && (
          <p className="px-4 py-3 text-sm text-fg4">
            {runs.length === 0
              ? 'Нажмите «Прогнать», чтобы сверить источники.'
              : onlyAttention ? 'Всё разобрано.' : 'Находок нет.'}
          </p>
        )}
        {shown.map(f => (
          <FindingRow key={f.id} f={f} onDecide={setDeciding}
            linkable={isUnmatched(f)}
            checked={linking.includes(f.id)}
            onToggle={() => setLinking(prev => prev.includes(f.id)
              ? prev.filter(x => x !== f.id)
              // Больше двух — уже не пара: держим последние две.
              : [...prev, f.id].slice(-2))} />
        ))}
      </div>
    </div>
  );

  return (
    <>
      <ListDetailShell
        title="Сверка"
        subtitle="Сопоставление источников по доменному ключу"
        headerAction={
          <div className="flex items-center gap-2">
            {sets.length > 0 && (
              <>
                <Select value={reportSetId || sets[0].id} onValueChange={setReportSetId}
                  aria-label="Комплект для отчёта" className="w-72">
                  {sets.map(s => <SelectItem key={s.id} value={s.id}>{s.label}</SelectItem>)}
                </Select>
                <Button onClick={downloadReport} loading={reportBusy}>
                  <FileSpreadsheet size={14} /> Отчёт
                </Button>
              </>
            )}
            {isAdmin && (
              <Button variant="filled" onClick={() => setEditing('new')}><Plus size={14} /> Создать</Button>
            )}
          </div>
        }
        nav={nav}
        detail={detail}
      />

      {editing && (
        <ReconciliationEditor
          existing={editing === 'new' ? null : items.find(i => i.id === editing) ?? null}
          onClose={() => setEditing(null)}
        />
      )}

      {deciding && selectedId && (
        <DecisionDialog
          finding={deciding}
          onClose={() => setDeciding(null)}
          onSave={async (kind, note) => {
            await setDecision.mutateAsync({ id: selectedId, key: deciding.key, kind, note });
            setDeciding(null);
            // Решение адресовано ключом позиции, поэтому переживёт следующий прогон — говорим это
            // прямо, иначе непонятно, почему отметка не исчезает после пересчёта.
            toast.success('Решение сохранено и переживёт следующий прогон');
          }}
          onRemove={deciding.decision ? async () => {
            await removeDecision.mutateAsync({ id: selectedId, key: deciding.key });
            setDeciding(null);
            toast.info('Решение снято');
          } : undefined}
        />
      )}

      <ConfirmDialog
        open={!!deleting}
        title="Удалить сверку?"
        description="Вместе с ней будут удалены все прогоны, находки и принятые решения."
        confirmLabel="Удалить"
        onOpenChange={open => { if (!open) setDeleting(null); }}
        onConfirm={async () => {
          const id = deleting!;
          await remove.mutateAsync(id);
          if (selectedId === id) setSelectedId(null);
          setDeleting(null);
          toast.success('Сверка удалена');
        }}
      />
    </>
  );
}
