import { useEffect, useRef, useState } from 'react';
import { correctWorkReport, readReviewCard, readReviewJournal, type ReviewFullCard } from '@/shared/api/workReview';
import { formatDate } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { LeaveGuard } from '@/shared/ui/LeaveGuard';
import { useGuardedLeave } from '@/shared/ui/NavigationGuard';
import { NumberInput } from '@/shared/ui/NumberInput';
import { PageHeader } from '@/shared/ui/PageHeader';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { useUploadsInFlight } from '@/shared/ui/uploadsInFlight';
import { apiError } from '@/shared/utils/apiError';
import { TrackFieldFiles } from '@/features/document-sets/fields/useFieldUploadChange';
import { contentsProblem } from '../entry/reportContents';
import { ReportLines } from '../entry/ReportLines';
import { correctionContents, correctionDifferences, correctionInput, describeCorrectionValue, reconcileCorrection,
  sameCorrectionValue, type CorrectionChoices, type CorrectionConflict } from './correctionContents';

type Refusal = { kind: 'invalid' | 'conflict' | 'unavailable' | 'unknown'; message: string };
type Checked = { card: ReviewFullCard; result: 'retry' | 'compare' | 'looks-saved' };
type Attempt = { report: ReviewFullCard['report']; body: ReturnType<typeof correctionInput>; reason: string };

function failureStatus(error: unknown) {
  return (error as { response?: { status?: number } } | null)?.response?.status;
}

function refusalOf(error: unknown): Refusal {
  const status = failureStatus(error);
  if (status === 403 || status === 404 || status === 501) return {
    kind: 'unavailable', message: status === 404 ? 'Отчёт недоступен.' : apiError(error, 'У вас больше нет доступа к исправлению.'),
  };
  if (status === 409) return { kind: 'conflict', message: apiError(error, 'Отчёт тем временем изменили.') };
  if (status === undefined || status >= 500) return { kind: 'unknown', message: 'Ответ на запись не получен. Проверьте действующую версию и журнал.' };
  return { kind: 'invalid', message: apiError(error, 'Не удалось записать исправление.') };
}

export function ReportCorrectionEditor({ card, onClose, onSaved }: {
  card: ReviewFullCard; onClose: () => void; onSaved: (version: number) => void;
}) {
  const [base, setBase] = useState(card);
  const [draft, setDraft] = useState(() => correctionContents(card.report));
  const [reason, setReason] = useState('');
  const [step, setStep] = useState<'edit' | 'review'>('edit');
  const [refusal, setRefusal] = useState<Refusal>();
  const [checked, setChecked] = useState<Checked>();
  const [choices, setChoices] = useState<CorrectionChoices>({});
  const [pickedNames, setPickedNames] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<'write' | 'read'>();
  const inFlight = useRef(false);
  const sent = useRef<Attempt>(null);
  const title = useRef<HTMLHeadingElement>(null);
  const refusalRegion = useRef<HTMLDivElement>(null);
  const leave = useGuardedLeave();
  const uploading = useUploadsInFlight();

  const original = correctionContents(base.report);
  const body = correctionInput(base.report, draft);
  const changed = !sameCorrectionValue(body, correctionInput(base.report, original));
  const dirty = changed || reason.length > 0;
  const problem = contentsProblem(draft);
  const ready = !busy && (!refusal || refusal.kind === 'invalid');
  const validReason = reason.trim().length > 0 && reason.trim().length <= 1000;
  const comparison = checked && checked.result !== 'retry'
    ? reconcileCorrection(original, draft, correctionContents(checked.card.report), choices) : null;

  useEffect(() => { title.current?.focus(); }, [step]);
  useEffect(() => { if (refusal) refusalRegion.current?.focus(); }, [refusal, checked]);

  async function save(attempt: Attempt) {
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy('write');
    setChecked(undefined);
    setRefusal(undefined);
    sent.current = attempt;
    try {
      const stored = await correctWorkReport(attempt.report, attempt.body, attempt.reason);
      onSaved(stored.currentVersion);
    } catch (error) {
      setRefusal(refusalOf(error));
    } finally {
      inFlight.current = false;
      setBusy(undefined);
    }
  }

  async function inspectCurrent() {
    if (!refusal || inFlight.current) return;
    inFlight.current = true;
    setBusy('read');
    try {
      const current = await readReviewCard(base.id);
      if (current.status === 'draft' || !current.actions.includes('correct')) {
        setRefusal({ kind: 'unavailable', message: current.status !== 'draft' && current.report.periodClosed === false
          ? 'Период открыт. Исправление новой версией больше недоступно.' : 'Исправление этого отчёта больше недоступно.' });
        return;
      }
      const attempt = sent.current;
      const unknown = refusal.kind === 'unknown';
      const journal = unknown ? await readReviewJournal(base.id) : [];
      if (current.report.version === base.report.version) {
        if (unknown) setChecked({ card: current, result: 'retry' });
        else setRefusal({ kind: 'invalid', message: refusal.message });
        return;
      }
      const replacement = current.report.versions.find(version => version.number === base.report.currentVersion);
      const looksSaved = unknown && attempt && current.report.currentVersion === base.report.currentVersion + 1
        && replacement?.reason === attempt.reason && journal.some(entry => entry.kind === 'corrected' && entry.comment === attempt.reason)
        && sameCorrectionValue(attempt.body, correctionInput(current.report, correctionContents(current.report)));
      setChecked({ card: current, result: looksSaved ? 'looks-saved' : 'compare' });
      setChoices({});
    } catch (error) {
      const status = failureStatus(error);
      setRefusal(status === 403 || status === 404 || status === 501 ? refusalOf(error) : {
        ...refusal, message: apiError(error, 'Не удалось перечитать отчёт. Черновик сохранён.'),
      });
    } finally {
      inFlight.current = false;
      setBusy(undefined);
    }
  }

  function adopt() {
    if (!checked || !comparison || comparison.conflicts.some(conflict => !choices[conflict.key])) return;
    setBase(checked.card);
    setDraft(comparison.contents);
    setChecked(undefined);
    setRefusal(undefined);
    setStep('edit');
    title.current?.focus();
  }

  return <>
    <PageHeader title={base.employeeName} subtitle={`${base.constructionName} · ${formatDate(base.report.date)} · ${base.report.shift === 'night' ? 'ночь' : 'день'}`} />
    <div className="flex flex-wrap gap-3">
      {refusal?.kind !== 'unavailable' && <StatusText>Период закрыт</StatusText>}
      <StatusText>Основа · версия {base.report.currentVersion}</StatusText>
    </div>
    <section className="min-w-0 space-y-4" aria-label="Редактор исправления">
      <h2 ref={title} tabIndex={-1} className="text-title-large">{step === 'edit' ? 'Исправление отчёта' : 'Проверка и причина'}</h2>
      {refusal && <div ref={refusalRegion} tabIndex={-1} className="space-y-3">
        <Banner tone={refusal.kind === 'invalid' || refusal.kind === 'unavailable' ? 'error' : 'warning'}>
          <span className="block">{refusal.message}</span>
          <span className="block">Введённые данные и причина сохранены в форме.</span>
        </Banner>
        {!checked && (refusal.kind === 'conflict' || refusal.kind === 'unknown') && <Button variant="outlined" loading={busy === 'read'}
          onClick={() => void inspectCurrent()}>{refusal.kind === 'unknown' ? 'Проверить версию и журнал' : 'Перечитать и сверить'}</Button>}
        {checked?.result === 'retry' && <div className="space-y-2">
          <p className="text-body-medium">Версия {checked.card.report.currentVersion} по-прежнему действующая. Можно повторить прежнюю отправку.</p>
          <Button variant="outlined" disabled={!!busy} onClick={() => { if (sent.current) void save(sent.current); }}>Повторить отправку</Button>
        </div>}
        {checked?.result === 'looks-saved' && <Banner tone="info" actions={[
          { label: 'Открыть действующую версию', onClick: () => onSaved(checked.card.report.currentVersion) },
          { label: 'Сверить черновик', onClick: () => setChecked({ ...checked, result: 'compare' }) },
        ]}>В версии {checked.card.report.currentVersion} и журнале найдены отправленные изменения и причина.</Banner>}
        {checked?.result === 'compare' && comparison && <Card title={`Сверка с версией ${checked.card.report.currentVersion}`}>
          <div className="space-y-3">
            {comparison.conflicts.length ? comparison.conflicts.map(conflict => <ConflictChoice key={conflict.key} conflict={conflict}
              choice={choices[conflict.key]} onChange={choice => setChoices(previous => ({ ...previous, [conflict.key]: choice }))} />)
              : <p className="text-body-medium">Параллельные правки не пересекаются с вашим вводом.</p>}
            <Button variant="tonal" disabled={comparison.conflicts.some(conflict => !choices[conflict.key])} onClick={adopt}>
              Подтвердить новую основу
            </Button>
          </div>
        </Card>}
      </div>}
      {step === 'edit' ? <>
        <div className="grid items-start gap-3 md:grid-cols-[minmax(0,1fr)_minmax(0,2fr)]">
          <NumberInput label="Часы за смену" value={draft.hours} disabled={!ready}
            onChange={hours => setDraft(previous => ({ ...previous, hours }))} />
          <TextAreaField label="Примечание к отчёту" rows={2} value={draft.note} readOnly={!ready}
            onChange={event => setDraft(previous => ({ ...previous, note: event.target.value }))} />
        </div>
        <TrackFieldFiles.Provider value>
          <ReportLines correction lines={draft.lines} savedLines={base.report.lines} disabled={!ready} siteReady
            scope={{ mode: 'entry', siteId: base.report.constructionId, date: base.report.date, reportId: base.id, reportVersion: base.report.version }}
            onPickType={type => setPickedNames(previous => ({ ...previous, [type.id]: type.displayName }))}
            onChange={update => setDraft(previous => ({ ...previous, lines: update(previous.lines) }))} />
        </TrackFieldFiles.Provider>
        {problem && <StatusText tone="error">{problem}</StatusText>}
      </> : <>
        <Card title="Проверка изменений"><ul className="space-y-1 break-words text-body-medium">
          {correctionDifferences(original, draft, id => pickedNames[id] ?? base.workTypes[id] ?? 'Работа')
            .map((line, index) => <li key={index}>{line}</li>)}
        </ul></Card>
        <TextAreaField label="Причина исправления" value={reason} rows={3} required maxLength={1000}
          readOnly={!ready} hint="Причину увидят в истории версий."
          onChange={event => setReason(event.target.value)} />
      </>}
      <div className="flex flex-wrap justify-end gap-2">
        {uploading && <StatusText>Загружаются вложения полей заказчика</StatusText>}
        <Button variant="text" disabled={!!busy || uploading} onClick={() => leave(onClose)}>Отмена</Button>
        {step === 'edit' ? <Button variant="filled" disabled={!ready || uploading || !changed || !!problem} onClick={() => setStep('review')}>
          Проверить изменения
        </Button> : <>
          <Button variant="outlined" disabled={!!busy} onClick={() => setStep('edit')}>Назад к полям</Button>
          <Button variant="filled" loading={busy === 'write'} disabled={!ready || uploading || !validReason || !changed || !!problem}
            onClick={() => void save({ report: base.report, body, reason: reason.trim() })}>Сохранить новой версией</Button>
        </>}
      </div>
    </section>
    <LeaveGuard active={dirty || !!busy || uploading} saving={!!busy || uploading} message="Введённые изменения и причина исправления не сохранены." />
  </>;
}

function ConflictChoice({ conflict, choice, onChange }: {
  conflict: CorrectionConflict; choice?: 'current' | 'draft'; onChange: (choice: 'current' | 'draft') => void;
}) {
  return <div className="space-y-1 break-words text-body-medium">
    <p>Было · {describeCorrectionValue(conflict.before)}</p>
    <p>Действующее · {describeCorrectionValue(conflict.current)}</p>
    <p>Ваш черновик · {describeCorrectionValue(conflict.draft)}</p>
    <Select label={conflict.label} value={choice} placeholder="Выберите значение" onValueChange={value => onChange(value as 'current' | 'draft')}>
      <SelectItem value="current">Оставить действующее значение</SelectItem>
      <SelectItem value="draft">Применить мой ввод</SelectItem>
    </Select>
  </div>;
}
