import { formatDateTime } from '@/shared/format/format';
import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import { Icon } from '@/shared/ui/Icon';
import { Add, Balance, Block, CheckCircle, HistoryIcon, LinkIcon, PlayArrow, SmartToy, TableChart, Warning } from '@/shared/ui/icons';
import { useServerForm } from '@/shared/hooks/useServerForm';
import { Button } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useListConstructions } from '@/shared/api/constructions';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { ListDetailShell, NavSearchInput, NavItem, NavSection } from '@/shared/ui/ListDetailShell';
import { useListDetailView } from '@/shared/ui/listDetailView';
import { Checkbox } from '@/shared/ui/Checkbox';
import { useAccess, NO_ACCESS, hasPermission } from '@/shared/api/access';
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
import { Chip } from '@/shared/ui/Chip';
import { StatusText } from '@/shared/ui/StatusText';

/**
 * Сверка на непротиворечивость (issue #414, фаза Ф1 — экран находок #436).
 *
 * До этой страницы результаты анализа жили только в переписке с внешним агентом: память между
 * прогонами не копилась, отчёт не с чем было сравнить. Здесь они становятся объектом системы.
 */

/** Состояние находки — общая пометка с тоном по смыслу ([UI-43]); значок различает состояния без цвета. */
function FindingStatus({ f }: { f: Finding }) {
  if (f.resolved) return <StatusText tone="success" icon={CheckCircle}>Устранено</StatusText>;
  if (f.status === 'Match') {
    return <span className="text-body-small text-on-surface-variant shrink-0">Совпадает</span>;
  }
  // Решение снимает вопрос, даже если расхождение осталось: в этом и смысл персистентного решения —
  // «давальческое оборудование» не должно всплывать каждый прогон.
  const muted = !!f.decision;
  return (
    <StatusText tone={muted ? 'quiet' : 'error'} icon={muted ? Block : Warning}>{STATUS_LABELS[f.status]}</StatusText>
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
    <div className="px-3 py-2 border-b border-outline-variant state-layer group">
      <div className="flex items-center gap-2">
        {/* Связывать имеет смысл только позиции, не нашедшие пары: у остальных пара уже есть. */}
        {linkable && (
          <Checkbox checked={checked} onChange={onToggle}
            aria-label={`Выбрать «${f.label}» для связывания`} />
        )}
        <FindingStatus f={f} />
        <span className="flex-1 truncate text-body-medium text-on-surface" title={f.label}>{f.label}</span>
        <span className="text-body-medium tabular-nums text-on-surface-variant shrink-0">
          {num(f.leftValue)} <span className="text-on-surface-variant">/</span> {num(f.rightValue)}
        </span>
        <Button variant="text" onClick={() => onDecide(f)}
          className="opacity-0 group-hover:opacity-100 focus:opacity-100 shrink-0">
          {f.decision ? 'Изменить' : 'Разобрать'}
        </Button>
      </div>
      <div className="flex items-center gap-3 mt-0.5 text-label-small text-on-surface-variant">
        {/* Провенанс: без него находку нельзя проверить глазами по документу. */}
        {provenanceSummary(left) && <span>слева: {provenanceSummary(left)}</span>}
        {provenanceSummary(right) && <span>справа: {provenanceSummary(right)}</span>}
        {f.decision && (
          <span className="text-on-surface-variant">
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
    <Select density="dense" value={value ?? runs[0]?.id ?? ''} onValueChange={onChange} aria-label="Прогон"
      className="w-56">
      {runs.map((r, i) => (
        <SelectItem key={r.id} value={r.id}>
          {formatDateTime(r.startedAt)}{i === 0 ? ' — последний' : ''}
        </SelectItem>
      ))}
    </Select>
  );
}

/** Секции раздела: сама сверка, замечания агента и алиасы — все три отвечают «что не так». */
type ReconciliationView = 'reconciliation' | 'observations' | 'aliases';

/** Что говорит адрес: своя секция либо открытая сверка. Разбор чистый — по строке запроса. */
function navFromQuery(query: string): { view: ReconciliationView; selectedId: string | null; runId: string | null } {
  const params = new URLSearchParams(query);
  const requested = params.get('view');
  if (requested === 'observations' || requested === 'aliases') {
    return { view: requested, selectedId: null, runId: null };
  }
  const id = params.get('id');
  return { view: 'reconciliation', selectedId: id, runId: null };
}

export function ReconciliationsPage() {
  // Правка определения сверки — по ПРАВУ, и по тому же, что и прогон: сервер разрешает их ОДНИМ
  // правом (ReconciliationEndpoints). Имя роли здесь изображало границу, которой в коде нет, — а
  // спрятанная кнопка и не граница вовсе: её обходит запрос к API. Нужна граница — нужно право.
  const { data: access = NO_ACCESS } = useAccess();
  const canEdit = hasPermission(access, 'core.reconciliation.run');
  const toast = useSnackbar();

  const [search, setSearch] = useState('');

  const [editing, setEditing] = useState<'new' | string | null>(null);
  const [deleting, setDeleting] = useState<string | null>(null);
  const [deciding, setDeciding] = useState<Finding | null>(null);
  const [onlyAttention, setOnlyAttention] = useState(false);
  // Связывание позиций: выбираем ровно две несопоставленные находки.
  const [linking, setLinking] = useState<string[]>([]);

  /**
   * Вход снаружи: `?id=<сверка>` и `?view=observations|aliases` (#731). Вкладка «Проблемы»
   * комплекта отсылает сюда за тем, чего там нет — историей прогонов, алиасами, чисткой журнала, —
   * и приводить на пустой экран «Выберите сверку» после явного «Открыть в „Сверке“» нельзя.
   *
   * <p>Адрес читается ВЫЧИСЛЕНИЕМ, а выбор человека живёт поверх него (issue #858). Прежде это
   * делал эффект — ради того, чтобы вторая такая ссылка подряд сработала: смена одной строки
   * запроса компонент не размонтирует. Теперь это выходит само: новая строка запроса — другое
   * «серверное» значение, и прежний выбор ей уже не отвечает.</p>
   *
   * <p>Замечание про `?view=`: он приводит и на свою секцию, и к снятому выбору сверки. Прежний
   * эффект выбор не трогал, но добраться до этой разницы можно было только второй ссылкой подряд
   * — а такая ссылка и есть новая команда, после которой держаться за прежний выбор нечестно.</p>
   */
  const [searchParams] = useSearchParams();
  const [place, setPlace] = useServerForm(searchParams.toString(), navFromQuery);
  const { view, selectedId, runId } = place;
  const setView = (v: ReconciliationView) => setPlace(p => ({ ...p, view: v }));
  const setSelectedId = (id: string | null) => setPlace(p => ({ ...p, selectedId: id }));
  const ld = useListDetailView();
  const setRunId = (id: string | null) => setPlace(p => ({ ...p, runId: id }));

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
    <NavItem key={i.id} icon={<Icon icon={Balance} size={18} />} label={i.name}
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
          <p className="px-3 py-2 text-body-medium text-on-surface-variant">
            {outsideSearch ? 'Больше ничего не найдено' : 'Сверок нет'}
          </p>
        )}

        {/* Отдельной секцией, а не в общем списке: находка сверки — результат арифметики,
            замечание — утверждение агента. Смешать значит выдать одно за другое. */}
        <NavSection label="Внешний анализ" />
        <NavItem icon={<Icon icon={SmartToy} size={18} />} label="Замечания агента"
          count={observations.filter(isUnreviewed).length}
          active={view === 'observations'}
          onClick={() => setView('observations')} />
        <NavItem icon={<Icon icon={LinkIcon} size={18} />} label="Алиасы позиций"
          count={aliases.filter(a => a.status === 'Proposed').length}
          active={view === 'aliases'}
          onClick={() => setView('aliases')} />
      </div>
    </>
  );

  const detail = view === 'observations' ? <ObservationsPanel />
    : view === 'aliases' ? <AliasesPanel />
    : !selected ? (
    <EmptyState icon={<Icon icon={Balance} size={40} />} title="Выберите сверку"
      description="Сверка сопоставляет два источника по доменному ключу и показывает расхождения." />
  ) : (
    <div className="flex-1 min-w-0 flex flex-col">
      <div className="flex items-center gap-2 px-4 py-3 border-b border-outline-variant shrink-0">
        <div className="min-w-0 flex-1">
          <h2 className="text-body-large font-semibold text-on-surface truncate">{selected.name}</h2>
          <p className="text-body-small text-on-surface-variant mt-0.5">
            {currentRun ? runSummary(currentRun) : 'Прогонов ещё не было'}
          </p>
        </div>
        {runs.length > 1 && (
          <div className="shrink-0 flex items-center gap-1.5 text-on-surface-variant">
            <Icon icon={HistoryIcon} size={18} />
            <RunPicker runs={runs} value={runId} onChange={setRunId} />
          </div>
        )}
        <Button variant="filled" onClick={onRun} disabled={run.isPending} className="shrink-0">
          <Icon icon={PlayArrow} size={18} /> {run.isPending ? 'Считаю…' : 'Прогнать'}
        </Button>
        {canEdit && (
          <RowActionsMenu actions={[
            { key: 'edit', label: 'Изменить', onSelect: () => setEditing(selected.id) },
            { key: 'delete', label: 'Удалить', danger: true, onSelect: () => setDeleting(selected.id) },
          ]} />
        )}
      </div>

      {/* Неудачный прогон обязан быть виден: пустой список читается как «расхождений нет», и это
          самое опасное недоразумение в подсистеме. */}
      {currentRun?.status === 'Failed' && (
        <div className="mx-4 mt-3 px-3 py-2 rounded-md bg-error-container text-on-error-container text-body-medium">
          <strong>Прогон не выполнен.</strong> {currentRun.error}
        </div>
      )}

      {currentRun?.status === 'Completed' && (
        <div className="flex items-center gap-2 px-4 py-2 border-b border-outline-variant text-body-small shrink-0">
          <Chip selected={onlyAttention} count={attentionCount} onClick={() => setOnlyAttention(v => !v)}>
            Требует внимания
          </Chip>
          <span className="text-on-surface-variant">
            совпало {currentRun.matchCount} · расхождений {currentRun.mismatchCount} ·
            нет слева {currentRun.missingLeftCount} · нет справа {currentRun.missingRightCount}
          </span>
        </div>
      )}

      {linking.length > 0 && (
        <div className="flex items-center gap-2 px-4 py-2 border-b border-outline-variant bg-primary-container shrink-0">
          <Icon icon={LinkIcon} size={18} className="text-primary shrink-0" />
          <span className="text-body-small text-primary flex-1 truncate">
            {linking.length === 1
              ? 'Выберите вторую позицию, чтобы связать их как одну'
              : findings.filter(f => linking.includes(f.id)).map(f => f.label).join('  →  ')}
          </span>
          <Button variant="text" onClick={() => setLinking([])}>Отмена</Button>
          <Button variant="filled" disabled={linking.length !== 2}
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
        {findingsLoading && <p className="px-4 py-3 text-body-medium text-on-surface-variant">Загрузка…</p>}
        {!findingsLoading && shown.length === 0 && (
          <p className="px-4 py-3 text-body-medium text-on-surface-variant">
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
      <ListDetailShell view={ld}
        title="Сверка"
        subtitle="Сопоставление источников по доменному ключу"
        actions={
          <>
            {sets.length > 0 && (
              <>
                <Select density="dense" value={reportSetId || sets[0].id} onValueChange={setReportSetId}
                  aria-label="Комплект для отчёта" className="w-72">
                  {sets.map(s => <SelectItem key={s.id} value={s.id}>{s.label}</SelectItem>)}
                </Select>
                <Button onClick={downloadReport} loading={reportBusy}>
                  <Icon icon={TableChart} size={18} /> Отчёт
                </Button>
              </>
            )}
            {canEdit && (
              <Button variant="filled" onClick={() => setEditing('new')}><Icon icon={Add} size={18} /> Создать</Button>
            )}
          </>
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
