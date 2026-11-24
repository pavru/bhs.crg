import { useRef, useState, type FormEvent } from 'react';
import { editReviewReport, type ReviewFullCard } from '@/shared/api/workReview';
import { formatInput } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { EditGrid, type EditGridColumn } from '@/shared/ui/EditGrid';
import { LeaveGuard } from '@/shared/ui/LeaveGuard';
import { useGuardedLeave } from '@/shared/ui/NavigationGuard';
import { TextField } from '@/shared/ui/TextField';
import { apiError } from '@/shared/utils/apiError';
import { newLocalId } from '@/shared/utils/localId';
import { parseNumber } from '@/shared/utils/parseNumber';
import { workLabel } from './workLabel';
import { ReportCustomerFields } from '../ReportCustomerFields';
import { sameContent } from '../entry/reportContents';

export function ReportLineEdit({ card, onClose, onSaved }: {
  card: ReviewFullCard; onClose: () => void; onSaved: () => void;
}) {
  const [original] = useState(card);
  const [rows, setRows] = useState(() => original.report.lines.map(line => ({ ...line, amount: formatInput(line.volume, 4) })));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const attempt = useRef<{ payload: string; changeId: string } | null>(null);
  // Общий `formatInput` задаёт десятичную запятую и обычные цифры для полей ввода во всех локалях.
  const values = rows.map(row => parseNumber(row.amount, 'ru-RU'));
  const valid = values.every(value => value != null && value >= 0);
  const changed = rows.some((row, i) => values[i] !== original.report.lines[i].volume
    || (row.location || null) !== original.report.lines[i].location
    || !sameContent(row.details, original.report.lines[i].details));
  const leave = useGuardedLeave();

  async function persist(): Promise<boolean> {
    if (!valid || !changed || busy) return false;
    const lines = rows.map((row, i) => ({ id: row.id, location: row.location, volume: values[i]!, details: row.details }));
    const payload = JSON.stringify(lines);
    if (attempt.current?.payload !== payload) attempt.current = { payload, changeId: newLocalId() };
    setBusy(true);
    setError(null);
    try {
      await editReviewReport(original.report, attempt.current.changeId, lines);
      onSaved();
      return true;
    } catch (failure) {
      setError(apiError(failure, 'Не удалось сохранить правку.'));
      return false;
    } finally {
      setBusy(false);
    }
  }

  function save(event: FormEvent) {
    event.preventDefault();
    void persist();
  }

  const columns: EditGridColumn<(typeof rows)[number]>[] = [
    { key: 'work', label: 'Работа', rowHeader: true, cell: row => workLabel(card, row) },
    { key: 'volume', label: 'Объём', width: 'w-32', cell: (row, i) => <TextField label={`Объём строки ${i + 1}`}
      value={row.amount} disabled={busy} inputMode="decimal" invalid={values[i] == null || values[i]! < 0}
      onChange={event => setRows(current => current.map(line => line.id === row.id ? { ...line, amount: event.target.value } : line))} /> },
    { key: 'unit', label: 'Ед.', cell: row => card.units[row.unitId] },
    { key: 'location', label: 'Место', cell: (row, i) => <TextField label={`Место строки ${i + 1}`}
      value={row.location ?? ''} disabled={busy}
      onChange={event => setRows(current => current.map(line => line.id === row.id ? { ...line, location: event.target.value } : line))} /> },
  ];

  return <form onSubmit={save} className="space-y-3" aria-label="Правка строк отчёта">
    {error && <Banner tone="error">{error}</Banner>}
    <EditGrid rows={rows} columns={columns} rowKey={row => row.id} label="Строки отчёта" numbered sheet />
    {rows.map((row, index) => <ReportCustomerFields key={row.id} title={`Поля заказчика · строка ${index + 1}`}
      details={row.details} missingFields={row.missingFields} siteId={card.report.constructionId} disabled={busy}
      onChange={details => setRows(current => current.map(line => line.id === row.id ? { ...line, details } : line))} />)}
    <div className="flex flex-wrap justify-end gap-2">
      <Button variant="text" onClick={() => leave(onClose)} disabled={busy}>Отмена</Button>
      <Button variant="filled" type="submit" disabled={!valid || !changed} loading={busy}>Сохранить правку</Button>
    </div>
    <LeaveGuard active={changed} saving={busy} save={valid ? persist : undefined} />
  </form>;
}
