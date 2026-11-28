import { useState } from 'react';
import { useSearchParams } from 'react-router';
import { exportEstimateVsCosts, useEstimateVsCosts, useEstimateVsCostsConstructions, type EstimateVsCostsRow } from '@/shared/api/estimateVsCosts';
import { formatAmount } from '@/shared/format/format';
import { useDebouncedValue } from '@/shared/hooks/useDebouncedValue';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Select, SelectItem } from '@/shared/ui/Select';
import { SegmentedButton } from '@/shared/ui/SegmentedButton';
import { TextField } from '@/shared/ui/TextField';
import { apiError } from '@/shared/utils/apiError';
import { comparisonGroup, estimateReason } from './estimateVsCostsRows';

function EstimateAmount({ row }: { row: EstimateVsCostsRow }) {
  if (row.estimate.amount === undefined || row.estimate.amount === null)
    return <span className="text-on-surface-variant">{estimateReason(row)}</span>;
  return <div>{formatAmount(row.estimate.amount)}
    {row.estimate.basisUnknown && <p className="text-body-small text-on-surface-variant">{estimateReason(row)}</p>}
  </div>;
}

function comparisonColumns(estimateCurrency: string | null, factCurrency: string, currencyMismatch: boolean): DataGridColumn<EstimateVsCostsRow>[] {
  const suffix = factCurrency ? `, ${factCurrency}` : '';
  return [
  { key: 'name', label: 'Материал', wrap: true, rowHeader: true },
  { key: 'estimate', label: 'Смета без НДС' + (estimateCurrency ? `, ${estimateCurrency}` : ''), align: 'right', wrap: true, cell: row => <EstimateAmount row={row} /> },
  { key: 'allocated', label: 'Разнесено без НДС' + suffix, align: 'right', wrap: true, cell: row => <div>
    {row.allocatedIncomplete && <p className="text-body-small text-on-surface-variant">Известная часть</p>}
    {formatAmount(row.allocated)}
    {row.allocatedIncomplete && <p className="text-body-small text-on-surface-variant">Стоимость остальных долей неизвестна</p>}
    {row.allocatedVatUnknown && <p className="text-body-small text-on-surface-variant">НДС неизвестен — учтено полной суммой</p>}
  </div> },
  { key: 'paid', label: 'Оплачено без НДС' + suffix, align: 'right', wrap: true, cell: row => <div>
    {row.paidIncomplete && <p className="text-body-small text-on-surface-variant">Известная часть</p>}
    {formatAmount(row.paid)}
    {row.paidIncomplete && <p className="text-body-small text-on-surface-variant">Стоимость остальных долей неизвестна</p>}
    {row.paidVatUnknown && <p className="text-body-small text-on-surface-variant">НДС неизвестен — учтено полной суммой</p>}
  </div> },
  { key: 'difference', label: 'Отклонение' + (currencyMismatch ? '' : suffix), align: 'right', wrap: true,
    cell: row => row.difference === undefined || row.difference === null
      ? <span className="text-on-surface-variant">Не определено · {estimateReason(row, currencyMismatch)}</span>
      : formatAmount(row.difference) },
  ];
}
const groupOrder = ['Превышение сметы', 'В пределах сметы', 'Совпадает со сметой', 'Сравнение недоступно'];

/** Вариант В, выбран владельцем 28.11.2026. Состояния и отклонения приходят из одного серверного расчёта. */
export function EstimateVsCosts() {
  const [params, setParams] = useSearchParams();
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState<string>();
  const constructions = useEstimateVsCostsConstructions();
  const site = params.get('site');
  const section = params.get('section');
  const search = params.get('search') ?? '';
  const debouncedSearch = useDebouncedValue(search, 250);
  const onlyExcess = params.get('excess') === 'true';
  const query = { site, section, search: debouncedSearch, onlyExcess };
  const report = useEstimateVsCosts(query);
  const data = report.data;
  const columns = comparisonColumns(data?.estimateCurrency ?? null, data?.factCurrency ?? '', data?.currencyMismatch ?? false);
  const selected = constructions.data?.find(construction => construction.id === site);
  function set(changes: Record<string, string | null>) {
    setExportError(undefined);
    setParams(previous => {
      const next = new URLSearchParams(previous);
      for (const [key, value] of Object.entries(changes)) {
        if (value) next.set(key, value);
        else next.delete(key);
      }
      return next;
    }, { replace: true });
  }
  async function download() {
    setExporting(true);
    setExportError(undefined);
    try { await exportEstimateVsCosts(query); }
    catch (error) { setExportError(apiError(error, 'Не удалось выгрузить отчёт')); }
    finally { setExporting(false); }
  }
  return <div className="grid min-w-0 gap-4">
    <div className="flex flex-wrap items-end gap-3">
      <div className="grid flex-1 gap-3 sm:grid-cols-2">
        <Select label="Стройка" value={site ?? ''} placeholder="Выберите стройку"
          disabled={constructions.isPending} onValueChange={value => set({ site: value, section: null })}>
          {(constructions.data ?? []).map(construction => <SelectItem key={construction.id} value={construction.id}>{construction.name}</SelectItem>)}
        </Select>
        <Select label="Раздел" value={section ?? 'all'} disabled={!selected}
          onValueChange={value => set({ section: value === 'all' ? null : value })}>
          <SelectItem value="all">Все разделы</SelectItem>
          {(selected?.sections ?? []).map(item => <SelectItem key={item.id} value={item.id}>{item.name}</SelectItem>)}
        </Select>
      </div>
      <Button variant="outlined" disabled={!data || report.isError || report.isFetching || search !== debouncedSearch}
        loading={exporting} onClick={() => void download()}>Выгрузить XLSX</Button>
    </div>
    <p className="text-body-small text-on-surface-variant">За всё время · с исправлениями · сравнение по разнесённому без НДС</p>
    {constructions.isError && <Banner tone="error">{apiError(constructions.error, 'Не удалось получить стройки')}</Banner>}
    {exportError && <Banner tone="error">{exportError}</Banner>}
    {!site ? <Banner tone="info">Выберите стройку для сравнения сметы и фактических затрат.</Banner> : <>
      <div className="grid gap-3 sm:grid-cols-2">
        <TextField label="Найти материал" value={search} onChange={event => set({ search: event.target.value })} />
        <SegmentedButton aria-label="Показать строки" value={onlyExcess ? 'excess' : 'all'}
          items={[{ value: 'all', label: 'Все материалы' }, { value: 'excess', label: 'Превышение сметы' }]}
          onChange={value => set({ excess: value === 'excess' ? 'true' : null })} />
      </div>
      {report.isError ? <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => { void report.refetch(); } }]}>
        {apiError(report.error, 'Отчёт не построился')}
      </Banner> : !data ? <p role="status" className="text-body-medium text-on-surface-variant">Отчёт строится…</p> : <>
        {data.message && <Banner tone="info">{data.message}</Banner>}
        {data.undecided > 0 && <Banner tone="warning">В смете {data.undecided} строк без решения о разделе. План выбранного раздела неполон.</Banner>}
        {data.rows.length === 0 ? <DataGrid columns={columns} rows={[]} state={search || onlyExcess ? 'filtered-out' : 'no-data'} />
          : groupOrder.filter(group => data.rows.some(row => comparisonGroup(row) === group)).map(group =>
            <section key={group} className="grid min-w-0 gap-2" aria-label={group}>
              <h2 className="text-title-medium">{group}</h2>
              <DataGrid columns={columns} rows={data.rows.filter(row => comparisonGroup(row) === group)}
                pinned={1} rowKey={row => row.nomenclatureId ?? 'unmatched'} />
            </section>)}
      </>}
    </>}
  </div>;
}
