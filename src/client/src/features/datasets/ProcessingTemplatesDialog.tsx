import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Add, Check, Delete, Edit, FilterAlt, FunctionIcon, Layers, RouteIcon, SwapVert } from '@/shared/ui/icons';
import { Modal } from '@/shared/ui/Modal';
import { IconButton } from '@/shared/ui/Button';
import {
  useListProcessingTemplates, useCreateProcessingTemplate,
  useUpdateProcessingTemplate, useDeleteProcessingTemplate,
} from '@/shared/api/datasetProcessing';
import { countFilterConditions } from '@/shared/api/datasetHelpers';
import { apiError } from '@/shared/utils/apiError';
import { RowFilterDialog } from './RowFilterDialog';
import { ComputedColumnsDialog } from './ComputedColumnsDialog';
import { SortSpecDialog } from './SortSpecDialog';
import type { ColumnExprDef, ComputedColumn, DataSetProcessingTemplate, RowFilterDef, SortSpec } from '@/shared/api/types';
import { Tooltip } from '@/shared/ui/Tooltip';

const FIELD_CLS = 'border border-outline-variant rounded-md px-3 py-1.5 text-sm bg-surface text-on-surface';

interface TemplateFormState {
  name: string;
  sheetOrPath: string | null;
  columnExpressions: ColumnExprDef[] | null;
  rowFilter: RowFilterDef | null;
  computedColumns: ComputedColumn[] | null;
  sortSpec: SortSpec | null;
}

function parseColumnExpressions(json: string | null | undefined): ColumnExprDef[] {
  if (!json) return [];
  try { const parsed = JSON.parse(json); return Array.isArray(parsed) ? parsed : []; }
  catch { return []; }
}

// ─── Extraction — без файлового контекста, поэтому обычный текст без builder'а/предпросмотра ───

function ExtractionFields({
  sheetOrPath, onSheetOrPathChange, columns, onColumnsChange,
}: {
  sheetOrPath: string; onSheetOrPathChange: (v: string) => void;
  columns: ColumnExprDef[]; onColumnsChange: (v: ColumnExprDef[]) => void;
}) {
  function addColumn() { onColumnsChange([...columns, { name: '', expr: '' }]); }
  function updateColumn(i: number, patch: Partial<ColumnExprDef>) {
    onColumnsChange(columns.map((c, idx) => idx === i ? { ...c, ...patch } : c));
  }
  function removeColumn(i: number) { onColumnsChange(columns.filter((_, idx) => idx !== i)); }

  return (
    <div className="space-y-2 rounded-lg p-3 border border-outline-variant bg-surface-container-low">
      <div>
        <label className="block text-xs font-medium mb-1 text-on-surface-variant">
          Extraction — row-selector <span className="font-normal">(XPath/JSONPath/имя листа — по формату файла; необязательно)</span>
        </label>
        <input value={sheetOrPath} onChange={e => onSheetOrPathChange(e.target.value)}
          placeholder="Напр.: //Position[not(Resources)]"
          className={`w-full font-mono text-xs ${FIELD_CLS}`} />
      </div>
      <div>
        <div className="flex items-center justify-between mb-1">
          <label className="block text-xs font-medium text-on-surface-variant">Колонки — относительно строки</label>
          <button type="button" onClick={addColumn} className="flex items-center gap-1 text-xs text-primary state-layer">
            <Icon icon={Add} size={18} /> Колонка
          </button>
        </div>
        {columns.length === 0 && <p className="text-xs text-on-surface-variant py-0.5">Колонки не заданы.</p>}
        <div className="space-y-1.5">
          {columns.map((col, i) => (
            <div key={i} className="flex items-center gap-1.5">
              <input value={col.name} onChange={e => updateColumn(i, { name: e.target.value })}
                placeholder="Название" className={`w-32 shrink-0 text-xs ${FIELD_CLS} px-2 py-1`} />
              <input value={col.expr} onChange={e => updateColumn(i, { expr: e.target.value })}
                placeholder="Выражение" className={`flex-1 min-w-0 font-mono text-xs ${FIELD_CLS} px-2 py-1`} />
              <button type="button" onClick={() => removeColumn(i)} className="p-1 text-on-surface-variant hover:text-error shrink-0">
                <Icon icon={Delete} size={18} />
              </button>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

// ─── Template form ────────────────────────────────────────────────────────────

function TemplateForm({
  initial, onSave, onCancel, saving,
}: {
  initial?: DataSetProcessingTemplate;
  onSave: (state: TemplateFormState) => Promise<unknown>;
  onCancel: () => void;
  saving: boolean;
}) {
  // Шаблон с негодным отбором сервер не сохранит (issue #1137) — причину показываем в форме.
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState(initial?.name ?? '');
  const [sheetOrPath, setSheetOrPath] = useState(initial?.sheetOrPath ?? '');
  const [columns, setColumns] = useState<ColumnExprDef[]>(() => parseColumnExpressions(initial?.columnExpressions));
  const [rowFilter, setRowFilter] = useState<RowFilterDef | null>(initial?.rowFilter ?? null);
  const [computedColumns, setComputedColumns] = useState<ComputedColumn[] | null>(initial?.computedColumns ?? null);
  const [sortSpec, setSortSpec] = useState<SortSpec | null>(initial?.sortSpec ?? null);
  const [filterOpen, setFilterOpen] = useState(false);
  const [transformsOpen, setTransformsOpen] = useState(false);
  const [sortOpen, setSortOpen] = useState(false);

  const filterCount = countFilterConditions(rowFilter);
  const transformCount = computedColumns?.length ?? 0;
  const sortCount = sortSpec?.length ?? 0;

  const toggleCls = (active: boolean) =>
    `flex items-center gap-1.5 px-3 py-1.5 rounded-md text-xs font-medium border transition-colors ${
      active ? 'border-primary text-on-primary-container bg-primary-container' : 'border-outline-variant text-on-surface-variant bg-surface-container-low'
    }`;

  function handleSave() {
    const cleanColumns = columns.filter(c => c.name.trim() && c.expr.trim());
    setError(null);
    onSave({
      name, sheetOrPath: sheetOrPath.trim() || null, columnExpressions: cleanColumns.length ? cleanColumns : null,
      rowFilter, computedColumns, sortSpec,
    }).catch(e => setError(apiError(e, 'Не удалось сохранить шаблон')));
  }

  return (
    <div className="space-y-4">
      <div>
        <label className="block text-xs font-medium mb-1 text-on-surface-variant">Название шаблона</label>
        <input
          value={name}
          onChange={e => setName(e.target.value)}
          placeholder="Напр.: Без аннулированных, по артикулу"
          className={`w-full ${FIELD_CLS}`}
        />
      </div>

      <ExtractionFields sheetOrPath={sheetOrPath} onSheetOrPathChange={setSheetOrPath}
        columns={columns} onColumnsChange={setColumns} />

      <div className="flex gap-2 flex-wrap">
        <button type="button" onClick={() => setFilterOpen(true)} className={toggleCls(filterCount > 0)}>
          <Icon icon={FilterAlt} size={18} /> Фильтрация
          {filterCount > 0 && <span className="ml-1 px-1.5 py-0.5 rounded-full text-on-primary text-caption bg-primary">{filterCount}</span>}
        </button>
        <button type="button" onClick={() => setTransformsOpen(true)} className={toggleCls(transformCount > 0)}>
          <Icon icon={FunctionIcon} size={18} /> Преобразования
          {transformCount > 0 && <span className="ml-1 px-1.5 py-0.5 rounded-full text-on-primary text-caption bg-primary">{transformCount}</span>}
        </button>
        <button type="button" onClick={() => setSortOpen(true)} className={toggleCls(sortCount > 0)}>
          <Icon icon={SwapVert} size={18} /> Сортировка
          {sortCount > 0 && <span className="ml-1 px-1.5 py-0.5 rounded-full text-on-primary text-caption bg-primary">{sortCount}</span>}
        </button>
      </div>

      {error && <p role="alert" className="text-xs text-error">{error}</p>}

      <div className="flex gap-2 pt-1">
        <button
          onClick={handleSave}
          disabled={saving || !name.trim()}
          className="flex items-center gap-1.5 px-3 py-1.5 rounded-md text-sm font-medium text-on-primary disabled-container bg-primary"
        >
          <Icon icon={Check} size={18} />
          {saving ? 'Сохранение...' : 'Сохранить'}
        </button>
        <button onClick={onCancel} className="px-3 py-1.5 rounded-md text-sm font-medium text-on-surface-variant bg-surface-container">
          Отмена
        </button>
      </div>

      {filterOpen && (
        <RowFilterDialog initial={rowFilter} onSave={f => setRowFilter(f)} onClose={() => setFilterOpen(false)} />
      )}
      {transformsOpen && (
        <ComputedColumnsDialog initial={computedColumns} onSave={c => setComputedColumns(c)} onClose={() => setTransformsOpen(false)} />
      )}
      {sortOpen && (
        <SortSpecDialog initial={sortSpec} onSave={s => setSortSpec(s)} onClose={() => setSortOpen(false)} />
      )}
    </div>
  );
}

// ─── Template row ─────────────────────────────────────────────────────────────

function TemplateRow({ template }: { template: DataSetProcessingTemplate }) {
  const [editing, setEditing] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const update = useUpdateProcessingTemplate();
  const del = useDeleteProcessingTemplate();

  const filterCount = countFilterConditions(template.rowFilter);
  const transformCount = template.computedColumns?.length ?? 0;
  const sortCount = template.sortSpec?.length ?? 0;

  async function handleSave(state: TemplateFormState) {
    await update.mutateAsync({ id: template.id, ...state });
    setEditing(false);
  }

  return (
    <div className="border-b border-outline-variant last:border-0">
      <div className="flex items-center gap-3 px-4 py-3">
        <Icon icon={Layers} size={18} className="text-primary shrink-0" />
        <div className="flex-1 min-w-0">
          <div className="text-sm font-medium text-on-surface">{template.name}</div>
          <div className="flex items-center gap-2 mt-0.5 flex-wrap">
            {!template.sheetOrPath && filterCount === 0 && transformCount === 0 && sortCount === 0 && (
              <span className="text-xs text-on-surface-variant">Без обработки</span>
            )}
            {template.sheetOrPath && (
              <Tooltip content={template.sheetOrPath}>
                <span className="flex items-center gap-1 text-caption px-1.5 py-0.5 rounded-full bg-primary-container text-on-primary-container">
                  <Icon icon={RouteIcon} size={18} /> Extraction
                </span>
              </Tooltip>
            )}
            {filterCount > 0 && (
              <span className="flex items-center gap-1 text-caption px-1.5 py-0.5 rounded-full bg-primary-container text-on-primary-container">
                <Icon icon={FilterAlt} size={18} /> {filterCount}
              </span>
            )}
            {transformCount > 0 && (
              <span className="flex items-center gap-1 text-caption px-1.5 py-0.5 rounded-full bg-primary-container text-on-primary-container">
                <Icon icon={FunctionIcon} size={18} /> {transformCount}
              </span>
            )}
            {sortCount > 0 && (
              <span className="flex items-center gap-1 text-caption px-1.5 py-0.5 rounded-full bg-primary-container text-on-primary-container">
                <Icon icon={SwapVert} size={18} /> {sortCount}
              </span>
            )}
          </div>
        </div>
        <IconButton label="Редактировать" size="sm" onClick={() => setEditing(e => !e)}>
          <Icon icon={Edit} size={18} />
        </IconButton>
        {!confirming ? (
          <IconButton label="Удалить" size="sm" danger onClick={() => setConfirming(true)}>
            <Icon icon={Delete} size={18} />
          </IconButton>
        ) : (
          <div className="flex items-center gap-1.5 text-xs">
            <span className="text-on-surface-variant">Удалить?</span>
            <button onClick={() => del.mutateAsync({ id: template.id }).then(() => setConfirming(false))}
              disabled={del.isPending} className="px-2 py-0.5 rounded text-on-error bg-error" style={{ fontSize: '11px' }}>
              Да
            </button>
            <button onClick={() => setConfirming(false)} className="px-2 py-0.5 rounded bg-surface-container text-on-surface-variant" style={{ fontSize: '11px' }}>
              Нет
            </button>
          </div>
        )}
      </div>

      {editing && (
        <div className="px-4 pb-4 bg-surface-container-low">
          <TemplateForm initial={template} onSave={handleSave} onCancel={() => setEditing(false)} saving={update.isPending} />
        </div>
      )}
    </div>
  );
}

// ─── Main dialog ──────────────────────────────────────────────────────────────

export function ProcessingTemplatesDialog({ onClose }: { onClose: () => void }) {
  const { data: templates = [], isLoading } = useListProcessingTemplates();
  const create = useCreateProcessingTemplate();
  const [adding, setAdding] = useState(false);

  async function handleCreate(state: TemplateFormState) {
    await create.mutateAsync(state);
    setAdding(false);
  }

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Шаблоны обработки" wide>
      <p className="text-xs mb-4 text-on-surface-variant">
        Переиспользуемые рецепты источника (Extraction + Filter/Transformation/Sort). Применение
        к источнику копирует значения единожды (не живая ссылка) — дальше источник независим,
        можно свободно скорректировать (см. «применить шаблон» у каждого источника).
      </p>

      <div className="rounded-xl overflow-hidden mb-4 border border-outline-variant bg-surface">
        {isLoading ? (
          <div className="p-6 text-center text-sm text-on-surface-variant">Загрузка...</div>
        ) : templates.length === 0 && !adding ? (
          <div className="p-6 text-center text-sm text-on-surface-variant">Нет шаблонов обработки.</div>
        ) : (
          templates.map(t => <TemplateRow key={t.id} template={t} />)
        )}
      </div>

      {adding ? (
        <div className="rounded-xl p-4 border border-outline-variant bg-surface-container-low">
          <p className="text-xs font-semibold mb-3 text-on-surface-variant">Новый шаблон</p>
          <TemplateForm onSave={handleCreate} onCancel={() => setAdding(false)} saving={create.isPending} />
        </div>
      ) : (
        <button onClick={() => setAdding(true)} className="flex items-center gap-2 text-sm font-medium px-3 py-2 rounded-md text-on-primary-container bg-primary-container">
          <Icon icon={Add} size={18} /> Добавить шаблон
        </button>
      )}
    </Modal>
  );
}
