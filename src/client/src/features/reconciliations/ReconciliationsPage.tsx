import { useMemo, useState } from 'react';
import {
  Plus, Play, AlertTriangle, CheckCircle2, CircleSlash, History, Scale,
} from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { useToast } from '@/shared/ui/Toast';
import { ListDetailShell, NavSearchInput, NavItem } from '@/shared/ui/ListDetailShell';
import { useAuth } from '@/shared/hooks/useAuth';
import {
  useReconciliations, useReconciliationRuns, useFindings, useRunReconciliation,
  useDeleteReconciliation, useSetDecision, useRemoveDecision,
  STATUS_LABELS, DECISION_LABELS, needsAttention, runSummary,
  type Finding, type ReconciliationRun,
} from '@/shared/api/reconciliations';
import { ReconciliationEditor } from './ReconciliationEditor';
import { DecisionDialog } from './DecisionDialog';

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

function FindingRow({ f, onDecide }: { f: Finding; onDecide: (f: Finding) => void }) {
  const left = f.provenance.left;
  const right = f.provenance.right;
  return (
    <div className="px-3 py-2 border-b border-stroke hover:bg-muted/40 group">
      <div className="flex items-center gap-2">
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
        {left && <span>слева: {left.column}, строк {left.rows.length}</span>}
        {right && <span>справа: {right.column}, строк {right.rows.length}</span>}
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

  const { data: items = [], isLoading } = useReconciliations();
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

  const attentionCount = findings.filter(needsAttention).length;
  const shown = onlyAttention ? findings.filter(needsAttention) : findings;

  async function onRun() {
    if (!selectedId) return;
    const result = await run.mutateAsync(selectedId);
    setRunId(null); // после прогона смотрим последний
    if (result.status === 'Failed') toast.error(result.error ?? 'Прогон не выполнен');
    else toast.success(runSummary(result));
  }

  const nav = (
    <>
      <NavSearchInput value={search} onChange={setSearch} placeholder="Поиск сверки…" />
      <div className="flex-1 overflow-y-auto px-2 pb-2">
        {filtered.map(i => (
          <NavItem key={i.id} icon={<Scale size={16} />} label={i.name}
            active={i.id === selectedId}
            onClick={() => { setSelectedId(i.id); setRunId(null); }} />
        ))}
        {filtered.length === 0 && !isLoading && (
          <p className="px-3 py-2 text-sm text-fg4">Сверок нет</p>
        )}
      </div>
    </>
  );

  const detail = !selected ? (
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

      <div className="flex-1 min-h-0 overflow-y-auto">
        {findingsLoading && <p className="px-4 py-3 text-sm text-fg4">Загрузка…</p>}
        {!findingsLoading && shown.length === 0 && (
          <p className="px-4 py-3 text-sm text-fg4">
            {runs.length === 0
              ? 'Нажмите «Прогнать», чтобы сверить источники.'
              : onlyAttention ? 'Всё разобрано.' : 'Находок нет.'}
          </p>
        )}
        {shown.map(f => <FindingRow key={f.id} f={f} onDecide={setDeciding} />)}
      </div>
    </div>
  );

  return (
    <>
      <ListDetailShell
        title="Сверка"
        subtitle="Сопоставление источников по доменному ключу"
        headerAction={isAdmin ? (
          <Button variant="filled" onClick={() => setEditing('new')}><Plus size={14} /> Создать</Button>
        ) : undefined}
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
