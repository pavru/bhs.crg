import { useState } from 'react';
import { Link, useParams } from 'react-router';
import { Play, FileSpreadsheet, AlertTriangle, Bot, Scale, ExternalLink } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { useToast } from '@/shared/ui/Toast';
import {
  useRelatedProblems, useRunReconciliation, useFindings, useSetDecision, useRemoveDecision,
  downloadDiscrepancyReport, STATUS_LABELS, DECISION_LABELS, provenanceSummary, needsAttention,
  type Finding,
} from '@/shared/api/reconciliations';
import {
  useObservations, SEVERITY_LABELS, OBSERVATION_STATUS_LABELS, isUnreviewed,
  type Observation,
} from '@/shared/api/observations';
import { DecisionDialog } from '@/features/reconciliations/DecisionDialog';
import { ObservationReviewDialog } from '@/features/reconciliations/ObservationReviewDialog';

/**
 * Проблемы комплекта (issue #452) — дом подсистемы сверки в иерархии.
 *
 * Две подписанные секции, а НЕ общий список и не табы: находка сверки — результат арифметики
 * системы, замечание — утверждение внешнего агента. Смешать их значило бы выдать одно за другое.
 *
 * Разбирают их здесь же (#731): вкладка была читальным залом и отсылала в раздел «Сверка», хотя
 * комплект — именно тот уровень, на котором с проблемами и работают. Диалоги разбора те же самые,
 * что и на главной странице, — общие компоненты, а не копии разметки. В разделе «Сверка» остаётся
 * то, что к разбору не относится: история прогонов, алиасы, правка определения, чистка журнала
 * агента; ссылки «Открыть в „Сверке“» ведут туда.
 */

function num(v: number | null): string {
  return v == null ? '—' : String(Math.round(v * 1000) / 1000);
}

function FindingLine({ f, onDecide }: { f: Finding; onDecide: (f: Finding) => void }) {
  const open = needsAttention(f);
  return (
    // Вся строка — вход в разбор, и подпись действия видна всегда, а не на hover (#680): скрытое
    // действие на непривычном экране просто не находят.
    <button type="button" onClick={() => onDecide(f)}
      className={`w-full text-left px-3 py-2 border-b border-stroke text-sm transition-colors
        hover:bg-muted/40 focus-visible:outline-none focus-visible:bg-muted/40 ${open ? '' : 'opacity-60'}`}>
      <div className="flex items-center gap-2">
        <span className={`text-[11px] px-1.5 py-0.5 rounded-full shrink-0 ${
          f.status === 'Match' ? 'text-fg4' : open ? 'bg-danger-subtle text-danger' : 'bg-muted text-fg3'}`}>
          {f.resolved ? 'Устранено' : STATUS_LABELS[f.status]}
        </span>
        <span className="flex-1 truncate text-fg1" title={f.label}>{f.label}</span>
        <span className="tabular-nums text-fg2 shrink-0">
          {num(f.leftValue)} <span className="text-fg4">/</span> {num(f.rightValue)}
        </span>
        <span className="text-xs text-brand shrink-0">{f.decision ? 'Изменить' : 'Разобрать'}</span>
      </div>
      <div className="flex items-center gap-3 mt-0.5 text-[11px] text-fg4">
        {provenanceSummary(f.provenance.left) && <span>слева: {provenanceSummary(f.provenance.left)}</span>}
        {provenanceSummary(f.provenance.right) && <span>справа: {provenanceSummary(f.provenance.right)}</span>}
        {f.decision && (
          <span className="text-fg3">
            {DECISION_LABELS[f.decision.kind]}
            {f.decision.note ? ` — ${f.decision.note}` : ''}
          </span>
        )}
      </div>
    </button>
  );
}

function ReconciliationBlock({ id, name, unresolved }: {
  id: string; name: string; unresolved: number;
}) {
  const { data: findings = [] } = useFindings(id);
  const run = useRunReconciliation();
  const setDecision = useSetDecision();
  const removeDecision = useRemoveDecision();
  const toast = useToast();
  const [onlyOpen, setOnlyOpen] = useState(true);
  const [deciding, setDeciding] = useState<Finding | null>(null);

  const shown = onlyOpen ? findings.filter(needsAttention) : findings;

  return (
    <div className="border border-stroke rounded-lg overflow-hidden">
      <div className="flex items-center gap-2 px-3 py-2 bg-muted/40">
        <Scale size={14} className="text-fg4 shrink-0" />
        <span className="flex-1 truncate text-sm font-medium text-fg1">{name}</span>
        {/* История прогонов, алиасы и правка определения живут только в разделе «Сверка»: здесь
            разбирают находки, а не настраивают сверку. */}
        <Link to={`/reconciliations?id=${id}`}
          className="inline-flex items-center gap-1 text-[11px] text-brand hover:underline shrink-0">
          <ExternalLink size={11} /> Открыть в «Сверке»
        </Link>
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
        : shown.map(f => <FindingLine key={f.id} f={f} onDecide={setDeciding} />)}

      {deciding && (
        <DecisionDialog
          finding={deciding}
          onClose={() => setDeciding(null)}
          onSave={async (kind, note) => {
            await setDecision.mutateAsync({ id, key: deciding.key, kind, note });
            setDeciding(null);
            // Решение адресовано ключом позиции, поэтому переживёт следующий прогон — говорим это
            // прямо, иначе непонятно, почему отметка не исчезает после пересчёта.
            toast.success('Решение сохранено и переживёт следующий прогон');
          }}
          onRemove={deciding.decision ? async () => {
            await removeDecision.mutateAsync({ id, key: deciding.key });
            setDeciding(null);
            toast.info('Решение снято');
          } : undefined}
        />
      )}
    </div>
  );
}

function ObservationCard({ o, documentNames, onReview }: {
  o: Observation;
  documentNames: Map<string, string>;
  onReview: (o: Observation) => void;
}) {
  return (
    <button type="button" onClick={() => onReview(o)}
      className={`w-full text-left px-3 py-2 border border-stroke rounded-lg transition-colors
        hover:bg-muted/40 focus-visible:outline-none focus-visible:bg-muted/40 ${
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
          {/* Чем кончился разбор — на карточке: иначе видно только «Отклонено» без причины. */}
          {o.reviewNote && (
            <p className="text-[11px] text-fg3 mt-1">
              {OBSERVATION_STATUS_LABELS[o.status]}: {o.reviewNote}
              {o.reviewedBy ? ` (${o.reviewedBy})` : ''}
            </p>
          )}
        </div>
        <span className="text-[11px] text-fg4 shrink-0">{OBSERVATION_STATUS_LABELS[o.status]}</span>
        <span className="text-xs text-brand shrink-0">
          {isUnreviewed(o) ? 'Разобрать' : 'Изменить'}
        </span>
      </div>
    </button>
  );
}

export function SetProblemsPanel({ setId, setName, documentNames }: {
  setId: string;
  setName: string;
  /** Имена документов комплекта: в ссылках замечаний иначе стоят нечитаемые идентификаторы. */
  documentNames: Map<string, string>;
}) {
  const { constructionId } = useParams<{ constructionId: string }>();
  const { data: problems } = useRelatedProblems('Set', setId);
  const { data: observations = [] } = useObservations(setId);
  const toast = useToast();
  const [busy, setBusy] = useState(false);
  const [reviewing, setReviewing] = useState<Observation | null>(null);

  // Deep-link документа из диалога разбора: замечание знает комплект, а путь собирается из стройки
  // и комплекта. Стройки в адресе нет — ссылки в диалоге не рисуем, битая ссылка хуже её отсутствия.
  const setPath = constructionId ? `/document-sets/${constructionId}/sets/${setId}` : null;

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
          <span className="flex-1" />
          {/* Журнал агента целиком (и его чистка) — в разделе «Сверка»: удалять записи с вкладки
              комплекта незачем, здесь их разбирают. */}
          <Link to="/reconciliations?view=observations"
            className="inline-flex items-center gap-1 text-[11px] font-normal normal-case tracking-normal text-brand hover:underline">
            <ExternalLink size={11} /> Весь журнал
          </Link>
        </h3>
        {/* Происхождение обязано быть видно рядом с данными: это утверждения, а не результат системы. */}
        <p className="text-[11px] text-fg4 -mt-1">
          Утверждения внешнего ИИ-агента. Система их не проверяла — подтвердите или отклоните.
        </p>

        {observations.length === 0 && (
          <EmptyState icon={<AlertTriangle size={28} />} title="Замечаний нет"
            description="Здесь появятся находки внешнего агента, если попросить его записать их в журнал." />
        )}

        {observations.map(o => (
          <ObservationCard key={o.id} o={o} documentNames={documentNames} onReview={setReviewing} />
        ))}
      </section>

      {reviewing && (
        <ObservationReviewDialog observation={reviewing} setPath={setPath}
          nameOf={id => documentNames.get(id)}
          onClose={() => setReviewing(null)} />
      )}
    </div>
  );
}
