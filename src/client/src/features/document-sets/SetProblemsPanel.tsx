import { useState } from 'react';
import { Play, FileSpreadsheet, AlertTriangle, Bot, Scale } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { useToast } from '@/shared/ui/Toast';
import {
  useRelatedProblems, useRunReconciliation, useFindings,
  downloadDiscrepancyReport, STATUS_LABELS, DECISION_LABELS, provenanceSummary, needsAttention,
  type Finding,
} from '@/shared/api/reconciliations';
import { useObservations, SEVERITY_LABELS, OBSERVATION_STATUS_LABELS, isUnreviewed } from '@/shared/api/observations';

/**
 * Проблемы комплекта (issue #452) — дом подсистемы сверки в иерархии.
 *
 * Две подписанные секции, а НЕ общий список и не табы: находка сверки — результат арифметики
 * системы, замечание — утверждение внешнего агента. Смешать их значило бы выдать одно за другое.
 */

function num(v: number | null): string {
  return v == null ? '—' : String(Math.round(v * 1000) / 1000);
}

function FindingLine({ f }: { f: Finding }) {
  const open = needsAttention(f);
  return (
    <div className={`px-3 py-2 border-b border-stroke text-sm ${open ? '' : 'opacity-60'}`}>
      <div className="flex items-center gap-2">
        <span className={`text-[11px] px-1.5 py-0.5 rounded-full shrink-0 ${
          f.status === 'Match' ? 'text-fg4' : open ? 'bg-danger-subtle text-danger' : 'bg-muted text-fg3'}`}>
          {f.resolved ? 'Устранено' : STATUS_LABELS[f.status]}
        </span>
        <span className="flex-1 truncate text-fg1" title={f.label}>{f.label}</span>
        <span className="tabular-nums text-fg2 shrink-0">
          {num(f.leftValue)} <span className="text-fg4">/</span> {num(f.rightValue)}
        </span>
      </div>
      <div className="flex items-center gap-3 mt-0.5 text-[11px] text-fg4">
        {provenanceSummary(f.provenance.left) && <span>слева: {provenanceSummary(f.provenance.left)}</span>}
        {provenanceSummary(f.provenance.right) && <span>справа: {provenanceSummary(f.provenance.right)}</span>}
        {f.decision && <span className="text-fg3">{DECISION_LABELS[f.decision.kind]}</span>}
      </div>
    </div>
  );
}

function ReconciliationBlock({ id, name, unresolved }: {
  id: string; name: string; unresolved: number;
}) {
  const { data: findings = [] } = useFindings(id);
  const run = useRunReconciliation();
  const toast = useToast();
  const [onlyOpen, setOnlyOpen] = useState(true);

  const shown = onlyOpen ? findings.filter(needsAttention) : findings;

  return (
    <div className="border border-stroke rounded-lg overflow-hidden">
      <div className="flex items-center gap-2 px-3 py-2 bg-muted/40">
        <Scale size={14} className="text-fg4 shrink-0" />
        <span className="flex-1 truncate text-sm font-medium text-fg1">{name}</span>
        <button type="button" onClick={() => setOnlyOpen(v => !v)}
          className={`text-[11px] px-2 py-0.5 rounded-full transition-colors ${
            onlyOpen ? 'bg-brand-subtle text-brand font-medium' : 'text-fg3 hover:bg-base'}`}>
          не разобрано: {unresolved}
        </button>
        <Button variant="text" size="sm" loading={run.isPending}
          onClick={async () => {
            const r = await run.mutateAsync(id);
            if (r.status === 'Failed') toast.error(r.error ?? 'Прогон не выполнен');
            else toast.success('Пересчитано');
          }}>
          <Play size={13} /> Прогнать
        </Button>
      </div>
      {shown.length === 0
        ? <p className="px-3 py-2 text-xs text-fg4">{onlyOpen ? 'Всё разобрано.' : 'Находок нет.'}</p>
        : shown.map(f => <FindingLine key={f.id} f={f} />)}
    </div>
  );
}

export function SetProblemsPanel({ setId, setName, documentNames }: {
  setId: string;
  setName: string;
  /** Имена документов комплекта: в ссылках замечаний иначе стоят нечитаемые идентификаторы. */
  documentNames: Map<string, string>;
}) {
  const { data: problems } = useRelatedProblems('Set', setId);
  const { data: observations = [] } = useObservations(setId);
  const toast = useToast();
  const [busy, setBusy] = useState(false);

  const open = observations.filter(isUnreviewed);

  return (
    <div className="flex-1 min-w-0 overflow-y-auto p-4 space-y-5">
      <div className="flex items-center gap-2">
        <div className="flex-1 min-w-0">
          <p className="text-sm text-fg2">
            {problems && problems.needsAttention > 0
              ? <>Требует разбора: <b className="text-fg1">{problems.needsAttention}</b>
                  <span className="text-fg4"> — {problems.unresolvedFindings} по сверке,
                  {' '}{problems.unreviewedObservations} замечаний анализа</span></>
              : 'Всё разобрано.'}
          </p>
        </div>
        <Button variant="outlined" size="sm" loading={busy}
          onClick={async () => {
            setBusy(true);
            try {
              await downloadDiscrepancyReport(setId, setName);
              toast.success('Отчёт выгружен');
            } catch { toast.error('Не удалось выгрузить отчёт'); }
            finally { setBusy(false); }
          }}>
          <FileSpreadsheet size={14} /> Отчёт
        </Button>
      </div>

      {/* ── Арифметика системы ──────────────────────────────────────────────── */}
      <section className="space-y-2">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-fg4">Находки сверки</h3>
        {problems?.reconciliations.length
          ? problems.reconciliations.map(r => (
              <ReconciliationBlock key={r.id} id={r.id} name={r.name} unresolved={r.unresolvedFindings} />
            ))
          : (
            <p className="text-sm text-fg4">
              К этому комплекту сверки не относятся. Сверка привязывается к уровню через области
              своих источников данных.
            </p>
          )}
      </section>

      {/* ── Утверждения агента ──────────────────────────────────────────────── */}
      <section className="space-y-2">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-fg4 flex items-center gap-1.5">
          <Bot size={13} /> Замечания анализа
        </h3>
        {/* Происхождение обязано быть видно рядом с данными: это утверждения, а не результат системы. */}
        <p className="text-[11px] text-fg4 -mt-1">
          Утверждения внешнего ИИ-агента. Система их не проверяла.
        </p>

        {observations.length === 0 && (
          <EmptyState icon={<AlertTriangle size={28} />} title="Замечаний нет"
            description="Здесь появятся находки внешнего агента, если попросить его записать их в журнал." />
        )}

        {observations.map(o => (
          <div key={o.id} className={`px-3 py-2 border border-stroke rounded-lg ${
            isUnreviewed(o) ? '' : 'opacity-60'}`}>
            <div className="flex items-start gap-2">
              <span className={`text-[11px] px-1.5 py-0.5 rounded-full shrink-0 mt-0.5 ${
                o.severity === 'Error' ? 'bg-warning-subtle text-warning' : 'bg-muted text-fg3'}`}>
                {SEVERITY_LABELS[o.severity]}
              </span>
              <div className="min-w-0 flex-1">
                <div className="text-sm text-fg1">{o.title}</div>
                {o.detail && <p className="text-xs text-fg3 mt-0.5 line-clamp-2">{o.detail}</p>}
                {/* Имена документов, а не идентификаторы: «документ 77de9193» человеку ничего не говорит. */}
                {(o.references.documentIds ?? []).length > 0 && (
                  <p className="text-[11px] text-fg4 mt-1">
                    {(o.references.documentIds ?? [])
                      .map(id => documentNames.get(id) ?? `документ ${id.slice(0, 8)}`)
                      .join(' · ')}
                  </p>
                )}
              </div>
              <span className="text-[11px] text-fg4 shrink-0">{OBSERVATION_STATUS_LABELS[o.status]}</span>
            </div>
          </div>
        ))}

        {observations.length > 0 && open.length === 0 && (
          <p className="text-xs text-fg4">Всё разобрано. Разбирать замечания — в разделе «Сверка».</p>
        )}
      </section>
    </div>
  );
}
