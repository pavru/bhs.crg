import { useMemo, useState } from 'react';
import { useVolumeLines } from '@/shared/api/planningConjuncture';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { SearchInput } from '@/shared/ui/SearchInput';
import { formatQuantity } from '@/shared/format/format';
import { apiError } from '@/shared/utils/apiError';
import { justificationWords, vorLines, type VorLine } from './attachmentRows';

/**
 * Ведомость объёмов работ, раскрытая под списком приложений (change `plan-11-conjuncture`, макет П11, вариант 1):
 * главы — заголовками групп; у работы под наименованием — формула и обоснование (шифр, чертежи, примечание);
 * подчинённая — отступом и «входит в № 12». Итогов объёма по главам нет: объёмы разных работ не складываются.
 * Листы PDF, переменные и таблица расчёта в этот change не входят (вопрос 7 листа 0, предложенный ответ).
 */
export function VolumeStatementView({ statementId }: { statementId: string }) {
  const page = useVolumeLines(statementId);
  const [query, setQuery] = useState('');
  const lines = useMemo(() => vorLines(page.data?.lines ?? [], query), [page.data, query]);

  if (page.isError) return <p role="alert" className="text-body-medium text-error">{apiError(page.error, 'Строки ведомости не прочитались')}</p>;
  if (!page.data) return <p className="text-body-medium text-on-surface-variant">Читаем строки ведомости…</p>;

  return (
    <div className="space-y-3">
      <SearchInput value={query} onChange={setQuery} placeholder="Найти работу" ariaLabel="Поиск работы по номеру, наименованию, шифру или чертежу"
        className="w-64 max-w-full" />
      <DataGrid<VorLine> columns={VOR_COLUMNS} rows={lines} rowKey={l => l.key} framed={false} density="compact" fixed
        state={query.trim() && lines.length === 0 ? 'filtered-out' : undefined}
        section={l => (l.kind === 'chapter' ? <span className="text-title-small">{l.caption}</span> : null)} />
    </div>
  );
}

const lineOf = (l: VorLine) => (l.kind === 'line' ? l.line : null);

const VOR_COLUMNS: DataGridColumn<VorLine>[] = [
  { key: 'number', label: '№', width: 'w-14', cell: l => <span className="tabular-nums text-on-surface-variant">{lineOf(l)!.number}</span> },
  { key: 'caption', label: 'Работа · обоснование', wrap: true, rowHeader: true, cell: l => <WorkCell line={l} /> },
  { key: 'units', label: 'Ед.', width: 'w-16', cell: l => lineOf(l)!.units },
  {
    key: 'quantity', label: 'Объём', align: 'right', width: 'w-24',
    cell: l => <span className="tabular-nums">{formatQuantity(lineOf(l)!.quantity)}</span>,
  },
];

function WorkCell({ line }: { line: VorLine }) {
  const work = lineOf(line)!;
  const why = justificationWords(work);
  return (
    <span className={`flex flex-col ${work.subordinate ? 'pl-4' : ''}`}>
      <span className="text-body-medium">{work.caption}</span>
      {work.formula && <span className="font-mono text-body-small text-on-surface-variant break-all">= {work.formula}</span>}
      {why && <span className="text-body-small text-on-surface-variant">{why}</span>}
      {work.subordinate && work.parentNumber && <span className="text-body-small text-on-surface-variant">входит в № {work.parentNumber}</span>}
    </span>
  );
}
