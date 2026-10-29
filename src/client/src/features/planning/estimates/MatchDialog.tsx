import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Add } from '@/shared/ui/icons';
import { useCan } from '@/shared/api/access';
import {
  planRefusal, useWorkTypeNames, type EstimateRevision, type EstimateRow, type PlanRefusal, type RevisionSection,
} from '@/shared/api/planningEstimates';
import { useMatchLines, useMatchRow } from '@/shared/api/planningMatches';
import { Button } from '@/shared/ui/Button';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Chip } from '@/shared/ui/Chip';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { useToast } from '@/shared/ui/Toast';
import { formatCount, formatQuantity } from '@/shared/format/format';
import { ruPlural } from '@/shared/utils/pluralize';
import { memoryText, nextWaitingSection, planVolume, sectionRows, waiting, waitingUnits } from './matchRows';
import { NewWorkTypeStep } from './NewWorkTypeStep';
import { RateChoiceFields } from './RateChoiceFields';
import { useRateChoice, type RateChoiceState } from './useRateChoice';
import { rowNumber } from './estimateRows';

/** Что сопоставляют: раздел ЛСР целиком либо одну строку. */
export type MatchTarget = { kind: 'section'; code: string | null } | { kind: 'row'; rowId: string };

/**
 * Окно сопоставления строк с видами работ (ТЗ PLAN-6.1, макет П1 1г): раздел ЛСР целиком одним действием,
 * исключения — построчно.
 *
 * - Раздел берёт одну единицу расценки — чипами сверху; строки других единиц остаются ждать и названы заранее.
 *   Ждущие строки выбранной единицы отмечены флажками; снятые тоже остаются ждать.
 * - «Сопоставить и к следующему» — следующий раздел, где ждут, в этом же окне (из варианта 3).
 * - «Запомнить» включено по умолчанию: неверно запомненное исправляют в списке запомненного (ответ П1).
 * - «Новый вид работы» — шагом окна и только при `core.worktypes.edit`: без права пункта нет в разметке.
 * - Отказ 409 — над кнопками словами сервера, набранное цело; следующая попытка — по перечитанной редакции.
 */
export function MatchDialog({ revision, rows, sections, target, onClose }: {
  revision: EstimateRevision;
  /** Все строки редакции — окно берёт из них раздел и следующий раздел. */
  rows: readonly EstimateRow[];
  sections: readonly RevisionSection[];
  target: MatchTarget;
  onClose: () => void;
}) {
  const [at, setAt] = useState(target);
  // Версия из ответа последней правки: перечитанная смета догнала бы её позже следующего нажатия. `null` — версия
  // редакции с экрана (после отказа 409 — перечитанная).
  const [version, setVersion] = useState<string | null>(null);
  const seen = version ?? revision.version;
  const matchRow = useMatchRow(revision.id);
  const matchLines = useMatchLines(revision.id);
  const busy = matchRow.isPending || matchLines.isPending;

  const row = at.kind === 'row' ? rows.find(r => r.id === at.rowId) ?? null : null;
  const title = at.kind === 'section'
    ? (at.code === null ? 'Сопоставить строки вне разделов' : `Сопоставить раздел ${at.code}`)
    : `Строка ${row ? rowNumber(row) : ''}: вид работы`;
  const common = { revision, seen, setVersion, busy, onClose };

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title={title} wide busy={busy}>
      {at.kind === 'section' ? (
        <SectionMatch key={at.code ?? ''} code={at.code} rows={rows} sections={sections} match={matchLines}
          onNext={code => setAt({ kind: 'section', code })} {...common} />
      ) : row ? (
        <RowMatch key={row.id} row={row} match={matchRow} {...common} />
      ) : (
        <p role="alert" className="text-sm text-error">Строки больше нет в редакции — перечитайте смету.</p>
      )}
    </Modal>
  );
}

interface Common {
  revision: EstimateRevision;
  seen: string;
  setVersion: (v: string | null) => void;
  busy: boolean;
  onClose: () => void;
}

const rowsWord = (n: number) => `${formatCount(n)} ${ruPlural(n, 'строку', 'строки', 'строк')}`;

function SectionMatch({ code, rows, sections, match, onNext, revision, seen, setVersion, busy, onClose }: Common & {
  code: string | null;
  rows: readonly EstimateRow[];
  sections: readonly RevisionSection[];
  match: ReturnType<typeof useMatchLines>;
  onNext: (code: string | null) => void;
}) {
  const toast = useToast();
  const inSection = sectionRows(rows, code);
  const units = waitingUnits(inSection);
  const [unit, setUnit] = useState<string | null | undefined>(undefined);
  const chosenUnit = unit === undefined ? units[0]?.unit ?? null : unit;
  const state = useRateChoice(chosenUnit);
  // Снятые флажки — исключения; по умолчанию отмечены все ждущие строки выбранной единицы.
  const [off, setOff] = useState<ReadonlySet<string>>(new Set());
  const [remember, setRemember] = useState(true);
  const [refusal, setRefusal] = useState<PlanRefusal | null>(null);
  const [creating, setCreating] = useState(false);
  const canCreate = useCan().permission('core.worktypes.edit');

  const candidates = inSection.filter(r => waiting(r) && r.sourceUnit === chosenUnit);
  const picked = candidates.filter(r => !off.has(r.id));
  const section = sections.find(s => s.code === code);
  const next = nextWaitingSection(sections, rows, code);
  const matched = inSection.filter(r => r.kind === 'work' && r.workTypeId).length;
  const works = inSection.filter(r => r.kind === 'work').length;

  const submit = (then: 'close' | 'next') => {
    if (!state.choice || picked.length === 0) return;
    setRefusal(null);
    match.mutate({ lineIds: picked.map(r => r.id), version: seen, choice: state.choice, sourceUnit: chosenUnit, remember }, {
      onSuccess: done => {
        setVersion(done.version);
        const skipped = done.skipped.length ? ` · пропущено: ${formatCount(done.skipped.length)} — ${done.skipped[0].text}` : '';
        toast.success(`Сопоставлено: ${rowsWord(done.matched)} — «${state.chosen?.name}»${memoryText(done.memory)}${skipped}`);
        if (then === 'next' && next) onNext(next.code); else onClose();
      },
      onError: e => { setRefusal(planRefusal(e)); setVersion(null); },
    });
  };

  const ready = state.choice !== null && picked.length > 0;
  const hint = state.missing ?? (picked.length === 0 ? 'Отметьте строки, которые сопоставить.' : null);

  return (
    <div className="space-y-4">
      <p className="text-sm text-on-surface-variant">
        {section?.name ?? (code === null ? 'Строки вне разделов ЛСР' : '')} · ред. {revision.number} ·{' '}
        {formatCount(works)} {ruPlural(works, 'строка-работа', 'строки-работы', 'строк-работ')}, сопоставлено {formatCount(matched)} ·{' '}
        <b className="font-medium">ждут {formatCount(inSection.filter(waiting).length)}</b>
      </p>

      {units.length > 1 && (
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs text-on-surface-variant">В разделе:</span>
          {units.map(u => (
            <Chip key={u.unit ?? ''} selected={u.unit === chosenUnit} count={formatCount(u.rows)}
              // Набранный коэффициент — для прежней единицы: перенесённый на другую, он дал бы план во сто раз больше.
              onClick={() => { if (u.unit !== chosenUnit) { setUnit(u.unit); state.type(''); } }}>
              {u.unit ?? 'без единицы'}
            </Chip>
          ))}
          <span className="text-xs text-on-surface-variant">— раздел берёт одну единицу; строки других единиц останутся ждать</span>
        </div>
      )}

      <RateChoiceFields state={state}
        sample={picked[0] ? { label: rowNumber(picked[0]), volume: picked[0].sourceVolume } : null} />

      <SectionRows rows={inSection} unit={chosenUnit} off={off} state={state}
        onToggle={(id, on) => setOff(prev => { const n = new Set(prev); if (on) n.delete(id); else n.add(id); return n; })} />

      <Checkbox checked={remember} onChange={setRemember}
        label="Запомнить для шифров раздела — следующий импорт подставит сам" />
      {refusal && <p role="alert" className="text-sm text-error">{refusal.text}</p>}

      {creating && (
        <NewWorkTypeStep sourceUnit={chosenUnit} onBack={() => setCreating(false)}
          onCreated={made => { state.created(made); setCreating(false); }} />
      )}

      <Footer canCreate={canCreate} onNew={() => setCreating(true)} hint={hint}>
        {next && (
          <Button variant="outlined" disabled={!ready || busy} onClick={() => submit('next')}>Сопоставить и к следующему</Button>
        )}
        <Button variant="filled" disabled={!ready} loading={busy} onClick={() => submit('close')}>
          Сопоставить {picked.length ? rowsWord(picked.length) : ''}
        </Button>
      </Footer>
    </div>
  );
}

/** Строки раздела в окне: ждущие выбранной единицы — с флажком, остальные — почему не в выборе. */
function SectionRows({ rows, unit, off, state, onToggle }: {
  rows: readonly EstimateRow[];
  unit: string | null;
  off: ReadonlySet<string>;
  state: RateChoiceState;
  onToggle: (id: string, on: boolean) => void;
}) {
  const names = useWorkTypeNames();
  const works = rows.filter(r => r.kind === 'work');
  const pickable = (r: EstimateRow) => waiting(r) && r.sourceUnit === unit;
  const columns: DataGridColumn<EstimateRow>[] = [
    {
      key: 'pick', label: 'Выбрать', width: 'w-10',
      cell: r => (pickable(r)
        ? <Checkbox size="sm" checked={!off.has(r.id)} onChange={on => onToggle(r.id, on)} aria-label={`Строка ${rowNumber(r)}`} />
        : null),
    },
    { key: 'number', label: '№', width: 'w-14', cell: r => <span className="tabular-nums text-on-surface-variant">{rowNumber(r)}</span> },
    { key: 'name', label: 'Строка', wrap: true, rowHeader: true, cell: r => <span className="text-sm">{r.name}</span> },
    {
      key: 'volume', label: 'Объём', align: 'right', width: 'w-24',
      cell: r => <span className="tabular-nums">{r.sourceVolume === null ? '' : formatQuantity(r.sourceVolume)} {r.sourceUnit}</span>,
    },
    {
      key: 'becomes', label: 'В плане станет', width: 'w-56', wrap: true,
      cell: r => {
        if (r.workTypeId) return (
          <span className="text-xs text-on-surface-variant">
            {names.data?.get(r.workTypeId) ?? '…'}{r.volume !== null ? ` · ${formatQuantity(r.volume)}` : ''}
            {r.marks.some(m => m.code === 'remembered') && <> <StatusBadge tone="quiet">запомнено</StatusBadge></>}
          </span>
        );
        if (!waiting(r)) return null;
        if (r.sourceUnit !== unit) return <span className="text-xs text-warning">другая единица — останется ждать</span>;
        if (off.has(r.id)) return <span className="text-xs text-on-surface-variant">не отмечена — останется ждать</span>;
        const v = state.factor === null ? null : planVolume(r, state.factor);
        return v === null ? null : <span className="tabular-nums text-sm">{formatQuantity(v)} {state.chosen?.unitName ?? ''}</span>;
      },
    },
  ];
  return <DataGrid columns={columns} rows={works} rowKey={r => r.id} fixed density="compact"
    tone={r => (r.marks.some(m => m.code === 'unit-changed') ? 'warning' : null)} />;
}

function RowMatch({ row, match, revision, seen, setVersion, busy, onClose }: Common & {
  row: EstimateRow;
  match: ReturnType<typeof useMatchRow>;
}) {
  const toast = useToast();
  const names = useWorkTypeNames();
  const state = useRateChoice(row.sourceUnit, row.workTypeId ? { workTypeId: row.workTypeId, factor: row.factor } : undefined);
  const [remember, setRemember] = useState(true);
  const [refusal, setRefusal] = useState<PlanRefusal | null>(null);
  const [creating, setCreating] = useState(false);
  const canCreate = useCan().permission('core.worktypes.edit');
  const remembered = row.marks.find(m => m.code === 'remembered');
  const current = row.workTypeId ? names.data?.get(row.workTypeId) ?? '…' : null;

  const send = (choice: typeof state.choice, done: (version: string) => void) => {
    setRefusal(null);
    match.mutate({ lineId: row.id, version: seen, choice, remember }, {
      onSuccess: result => { setVersion(result.version); done(result.version); },
      onError: e => { setRefusal(planRefusal(e)); setVersion(null); },
    });
  };
  const submit = () => {
    if (!state.choice) return;
    send(state.choice, () => {
      toast.success(`Строка ${rowNumber(row)} — «${state.chosen?.name}»`);
      onClose();
    });
  };
  // Отмена подстановки и снятие — одним действием сервера (`clear`): строка возвращается в «не сопоставлена».
  // Запомненное при этом остаётся — его меняют и забывают в списке запомненного.
  const clear = () => send(null, () => {
    toast.success(`Строка ${rowNumber(row)} снова ждёт сопоставления`);
    onClose();
  });

  return (
    <div className="space-y-4">
      <div className="rounded-lg bg-surface-container px-3 py-2 space-y-0.5">
        <p className="font-mono text-xs text-on-surface-variant">{row.rateCode}</p>
        <p className="text-sm text-on-surface">{row.name}</p>
        <p className="text-xs text-on-surface-variant tabular-nums">
          {row.sourceVolume !== null ? formatQuantity(row.sourceVolume) : '—'} {row.sourceUnit} · ред. {revision.number}
        </p>
      </div>

      {current && (
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <span className="text-on-surface-variant">Сейчас: <b className="font-medium text-on-surface">{current}</b>
            {row.volume !== null && <span className="text-on-surface-variant tabular-nums"> · {formatQuantity(row.volume)}</span>}</span>
          {remembered && <StatusBadge tone="quiet" title={remembered.text}>запомнено</StatusBadge>}
          <Button size="sm" variant="text" disabled={busy} onClick={clear}>
            {remembered ? 'Отменить подстановку' : 'Снять сопоставление'}
          </Button>
        </div>
      )}

      <RateChoiceFields state={state} sample={{ label: rowNumber(row), volume: row.sourceVolume }} />
      <Checkbox checked={remember} onChange={setRemember}
        label={row.rateCode ? `Запомнить для шифра «${row.rateCode}» — следующий импорт подставит сам` : 'Запомнить для шифра'} />
      {refusal && <p role="alert" className="text-sm text-error">{refusal.text}</p>}

      {creating && (
        <NewWorkTypeStep sourceUnit={row.sourceUnit} onBack={() => setCreating(false)}
          onCreated={made => { state.created(made); setCreating(false); }} />
      )}

      <Footer canCreate={canCreate} onNew={() => setCreating(true)} hint={state.missing}>
        <Button variant="filled" disabled={state.choice === null} loading={busy} onClick={submit}>Сопоставить</Button>
      </Footer>
    </div>
  );
}

/** Подвал тела: «Новый вид работы» слева — только с правом; без права его нет в разметке вовсе. */
function Footer({ canCreate, onNew, hint, children }: {
  canCreate: boolean; onNew: () => void; hint: string | null; children: React.ReactNode;
}) {
  return (
    <div className="flex flex-wrap items-center justify-end gap-2 border-t border-outline-variant pt-3">
      {canCreate && (
        <Button size="sm" variant="text" icon={<Icon icon={Add} size={18} />} className="mr-auto" onClick={onNew}>Новый вид работы</Button>
      )}
      {hint && <span className={`text-xs text-on-surface-variant ${canCreate ? '' : 'mr-auto'}`}>{hint}</span>}
      <ModalExit />
      {children}
    </div>
  );
}
