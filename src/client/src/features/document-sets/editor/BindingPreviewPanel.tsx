import { Icon } from '@/shared/ui/Icon';
import { CheckCircle, ErrorIcon, Warning } from '@/shared/ui/icons';
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
  if (isFileAttachment(v)) return <>📎 {v.fileName} <span className="text-on-surface-variant">({formatBytes(v.size)})</span></>;
  return String(v);
}

function columnsOf(r: DataSetBindingPreviewResult): DataGridColumn[] {
  return (r.columns ?? []).map(c => ({ key: c.key, label: c.label, unavailable: columnUnavailable(c.unavailable) }));
}

export function BindingPreviewPanel({ results }: { results: DataSetBindingPreviewResult[] }) {
  if (results.length === 0)
    return <p className="text-xs py-2 text-on-surface-variant">Нет привязок для проверки</p>;

  return (
    <div className="space-y-3">
      {results.map(r => (
        <div key={r.bindingId} className="rounded-md overflow-hidden border border-outline-variant">
          <div className="flex items-center gap-2 px-3 py-2 bg-surface-container-low">
            {/* Иконка — по режиму: у рабочей привязки `error` несёт предупреждение, и данные под ним есть. */}
            {r.mode === 'error'
              ? <Icon icon={ErrorIcon} size={18} className="text-error shrink-0" />
              : r.error
              ? <Icon icon={Warning} size={18} className="text-warning shrink-0" />
              : <Icon icon={CheckCircle} size={18} className="text-success shrink-0" />
            }
            <span className="text-xs font-medium flex-1 text-on-surface">
              {r.sourceName}
              <span className="font-normal ml-1.5 text-on-surface-variant">
                {r.fileName} · {r.mode === 'scalar' ? 'скалярный' : r.mode === 'tabular' ? `табличный → ${r.targetFieldKey}` : 'ошибка'}
              </span>
            </span>
            {r.mode !== 'error' && (
              <span className="text-xs text-on-surface-variant">{r.totalRows} строк</span>
            )}
          </div>

          {r.mode === 'error' ? (
            <div className="px-3 py-2 text-xs text-error bg-surface">{r.error}</div>
          ) : (
            <>
              {/* Предупреждение у рабочей привязки (пропущенные строки) — над данными, а не вместо них. */}
              {r.error && <div className="px-3 py-2 text-xs text-warning bg-surface border-b border-outline-variant">{r.error}</div>}
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
      {/* Одна запись — «поле — значение», а не таблица: колонок у неё нет, есть пары (#1354). */}
      <dl className="grid grid-cols-[minmax(0,1fr)_minmax(0,2fr)] text-xs">
        {columns.map(c => (
          <div key={c.key} className="contents [&:not(:last-child)>*]:border-b [&>*]:border-outline-variant">
            <dt className="py-1 pr-4 font-medium text-on-surface-variant">{c.label}</dt>
            <dd className="py-1 text-on-surface"><DataGridValue row={data} column={c} renderValue={renderPreviewValue} /></dd>
          </div>
        ))}
      </dl>
    </div>
  );
}

function TabularPreview({ rows, columns }: { rows: Record<string, unknown>[]; columns: DataGridColumn[] }) {
  // Ни одно поле не размечено — сетка без колонок нарисовала бы невидимые строки под зелёной галкой.
  if (columns.length === 0)
    return (
      <p className="px-3 py-2 text-xs text-warning bg-surface">
        Ни одному полю строки не назначена колонка источника — из источника в строки не придёт ничего.
      </p>
    );
  return (
    <div className="bg-surface">
      <DataGrid framed={false} columns={columns} rows={rows.slice(0, PREVIEW_ROWS)} renderValue={renderPreviewValue} />
      {rows.length > PREVIEW_ROWS && (
        <p className="px-3 py-1.5 text-xs border-t border-outline-variant text-on-surface-variant">
          +{rows.length - PREVIEW_ROWS} строк не показано
        </p>
      )}
    </div>
  );
}
