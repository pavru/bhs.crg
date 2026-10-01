import { AlertCircle, CheckCircle2 } from 'lucide-react';
import type { DataSetBindingPreviewResult } from '@/shared/api/types';
import { isFileAttachment, formatBytes } from '@/shared/api/attachments';
import { DataGrid, DataGridValue, type DataGridColumn } from '@/shared/ui/DataGrid';
import { columnUnavailable } from '@/shared/ui/dataGridStates';

// ── Предпросмотр привязок наборов данных («Проверить данные») ───────────────────────────────────
// Вынесено из DataSetsTab (задача G1a, issue #1088) и рисуется общей сеткой — первым её
// потребителем. Раньше колонки брались из ПЕРВОЙ строки, а пустое значение печаталось словом «null»:
// у union терялись колонки остальных вариантов, а «поля нет» и «значение пустое» выглядели одинаково.

/** Сколько строк табличной привязки показываем: экран проверочный, а не просмотр набора. */
const PREVIEW_ROWS = 5;

/** Непустое значение клетки: строка как есть, файл (файловый маппинг) — имя и размер. */
function renderPreviewValue(v: unknown) {
  if (isFileAttachment(v)) return <>📎 {v.fileName} <span className="text-fg4">({formatBytes(v.size)})</span></>;
  return String(v);
}

function columnsOf(r: DataSetBindingPreviewResult): DataGridColumn[] {
  return (r.columns ?? []).map(c => ({ key: c.key, label: c.label, unavailable: columnUnavailable(c.unavailable) }));
}

export function BindingPreviewPanel({ results }: { results: DataSetBindingPreviewResult[] }) {
  if (results.length === 0)
    return <p className="text-xs py-2 text-fg4">Нет привязок для проверки</p>;

  return (
    <div className="space-y-3">
      {results.map(r => (
        <div key={r.bindingId} className="rounded-lg overflow-hidden border border-stroke">
          <div className="flex items-center gap-2 px-3 py-2 bg-base">
            {r.error
              ? <AlertCircle size={13} className="text-danger shrink-0" />
              : <CheckCircle2 size={13} className="text-success shrink-0" />
            }
            <span className="text-xs font-medium flex-1 text-fg1">
              {r.sourceName}
              <span className="font-normal ml-1.5 text-fg4">
                {r.fileName} · {r.mode === 'scalar' ? 'скалярный' : r.mode === 'tabular' ? `табличный → ${r.targetFieldKey}` : 'ошибка'}
              </span>
            </span>
            {r.mode !== 'error' && (
              <span className="text-xs text-fg4">{r.totalRows} строк</span>
            )}
          </div>

          {r.mode === 'error' ? (
            <div className="px-3 py-2 text-xs text-danger bg-surface">{r.error}</div>
          ) : (
            <>
              {/* Предупреждение у рабочей привязки (пропущенные строки) — над данными, а не вместо них. */}
              {r.error && <div className="px-3 py-2 text-xs text-warning bg-surface border-b border-stroke">{r.error}</div>}
              {r.mode === 'scalar'
                ? <ScalarPreview data={r.data as Record<string, unknown>} columns={columnsOf(r)} />
                : <TabularPreview rows={r.data as Record<string, unknown>[]} columns={columnsOf(r)} />}
            </>
          )}
        </div>
      ))}
    </div>
  );
}

/** Скалярная привязка — одна запись: поле слева, значение справа, с теми же состояниями, что у сетки. */
function ScalarPreview({ data, columns }: { data: Record<string, unknown>; columns: DataGridColumn[] }) {
  return (
    <div className="px-3 py-2 overflow-x-auto bg-surface">
      <table className="text-xs w-full">
        <tbody>
          {columns.map(c => (
            <tr key={c.key} className="border-b border-stroke last:border-0">
              <td className="py-1 pr-4 font-medium w-1/3 text-fg3">{c.label}</td>
              <td className="py-1 text-fg1"><DataGridValue row={data} column={c} renderValue={renderPreviewValue} /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function TabularPreview({ rows, columns }: { rows: Record<string, unknown>[]; columns: DataGridColumn[] }) {
  return (
    <div className="bg-surface">
      <DataGrid framed={false} columns={columns} rows={rows.slice(0, PREVIEW_ROWS)} renderValue={renderPreviewValue} />
      {rows.length > PREVIEW_ROWS && (
        <p className="px-3 py-1.5 text-xs border-t border-stroke text-fg4">
          +{rows.length - PREVIEW_ROWS} строк не показано
        </p>
      )}
    </div>
  );
}
