import { useState } from 'react';
import { Plus, Trash2, Pencil, Check, Layers, Filter, FunctionSquare, ArrowUpDown, Route } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import {
  useListProcessingTemplates, useCreateProcessingTemplate,
  useUpdateProcessingTemplate, useDeleteProcessingTemplate,
} from '@/shared/api/datasets';
import { countFilterConditions } from '@/shared/api/datasetHelpers';
import { RowFilterDialog } from './RowFilterDialog';
import { ComputedColumnsDialog } from './ComputedColumnsDialog';
import { SortSpecDialog } from './SortSpecDialog';
import type { ColumnExprDef, ComputedColumn, DataSetProcessingTemplate, RowFilterDef, SortSpec } from '@/shared/api/types';

const FIELD_CLS = 'border border-stroke rounded-md px-3 py-1.5 text-sm bg-surface text-fg1';

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
    <div className="space-y-2 rounded-lg p-3 border border-stroke bg-base">
      <div>
        <label className="block text-xs font-medium mb-1 text-fg3">
          Extraction — row-selector <span className="font-normal">(XPath/JSONPath/имя листа — по формату файла; необязательно)</span>
        </label>
        <input value={sheetOrPath} onChange={e => onSheetOrPathChange(e.target.value)}
          placeholder="Напр.: //Position[not(Resources)]"
          className={`w-full font-mono text-xs ${FIELD_CLS}`} />
      </div>
      <div>
        <div className="flex items-center justify-between mb-1">
          <label className="block text-xs font-medium text-fg3">Колонки — относительно строки</label>
          <button type="button" onClick={addColumn} className="flex items-center gap-1 text-xs text-brand hover:text-brand-hover">
            <Plus size={12} /> Колонка
          </button>
        </div>
        {columns.length === 0 && <p className="text-xs text-fg4 py-0.5">Колонки не заданы.</p>}
        <div className="space-y-1.5">
          {columns.map((col, i) => (
            <div key={i} className="flex items-center gap-1.5">
              <input value={col.name} onChange={e => updateColumn(i, { name: e.target.value })}
                placeholder="Название" className={`w-32 shrink-0 text-xs ${FIELD_CLS} px-2 py-1`} />
              <input value={col.expr} onChange={e => updateColumn(i, { expr: e.target.value })}
                placeholder="Выражение" className={`flex-1 min-w-0 font-mono text-xs ${FIELD_CLS} px-2 py-1`} />
              <button type="button" onClick={() => removeColumn(i)} className="p-1 text-fg4 hover:text-danger shrink-0">
                <Trash2 size={13} />
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
  onSave: (state: TemplateFormState) => void;
  onCancel: () => void;
  saving: boolean;
}) {
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
      active ? 'border-brand text-brand bg-brand-subtle' : 'border-stroke text-fg3 bg-base'
    }`;

  function handleSave() {
    const cleanColumns = columns.filter(c => c.name.trim() && c.expr.trim());
    onSave({
      name, sheetOrPath: sheetOrPath.trim() || null, columnExpressions: cleanColumns.length ? cleanColumns : null,
      rowFilter, computedColumns, sortSpec,
    });
  }

  return (
    <div className="space-y-4">
      <div>
        <label className="block text-xs font-medium mb-1 text-fg3">Название шаблона</label>
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
          <Filter size={12} /> Фильтрация
          {filterCount > 0 && <span className="ml-1 px-1.5 py-0.5 rounded-full text-white text-[10px] bg-brand">{filterCount}</span>}
        </button>
        <button type="button" onClick={() => setTransformsOpen(true)} className={toggleCls(transformCount > 0)}>
          <FunctionSquare size={12} /> Преобразования
          {transformCount > 0 && <span className="ml-1 px-1.5 py-0.5 rounded-full text-white text-[10px] bg-brand">{transformCount}</span>}
        </button>
        <button type="button" onClick={() => setSortOpen(true)} className={toggleCls(sortCount > 0)}>
          <ArrowUpDown size={12} /> Сортировка
          {sortCount > 0 && <span className="ml-1 px-1.5 py-0.5 rounded-full text-white text-[10px] bg-brand">{sortCount}</span>}
        </button>
      </div>

      <div className="flex gap-2 pt-1">
        <button
          onClick={handleSave}
          disabled={saving || !name.trim()}
          className="flex items-center gap-1.5 px-3 py-1.5 rounded-md text-sm font-medium text-white disabled:opacity-40 bg-brand"
        >
          <Check size={13} />
          {saving ? 'Сохранение...' : 'Сохранить'}
        </button>
        <button onClick={onCancel} className="px-3 py-1.5 rounded-md text-sm font-medium text-fg2 bg-muted">
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
    <div className="border-b border-stroke last:border-0">
      <div className="flex items-center gap-3 px-4 py-3">
        <Layers size={13} className="text-brand shrink-0" />
        <div className="flex-1 min-w-0">
          <div className="text-sm font-medium text-fg1">{template.name}</div>
          <div className="flex items-center gap-2 mt-0.5 flex-wrap">
            {!template.sheetOrPath && filterCount === 0 && transformCount === 0 && sortCount === 0 && (
              <span className="text-xs text-fg4">Без обработки</span>
            )}
            {template.sheetOrPath && (
              <span className="flex items-center gap-1 text-[10px] px-1.5 py-0.5 rounded-full bg-brand-subtle text-brand" title={template.sheetOrPath}>
                <Route size={9} /> Extraction
              </span>
            )}
            {filterCount > 0 && (
              <span className="flex items-center gap-1 text-[10px] px-1.5 py-0.5 rounded-full bg-brand-subtle text-brand">
                <Filter size={9} /> {filterCount}
              </span>
            )}
            {transformCount > 0 && (
              <span className="flex items-center gap-1 text-[10px] px-1.5 py-0.5 rounded-full bg-brand-subtle text-brand">
                <FunctionSquare size={9} /> {transformCount}
              </span>
            )}
            {sortCount > 0 && (
              <span className="flex items-center gap-1 text-[10px] px-1.5 py-0.5 rounded-full bg-brand-subtle text-brand">
                <ArrowUpDown size={9} /> {sortCount}
              </span>
            )}
          </div>
        </div>
        <button onClick={() => setEditing(e => !e)} className="p-1.5 rounded text-fg3" title="Редактировать">
          <Pencil size={13} />
        </button>
        {!confirming ? (
          <button onClick={() => setConfirming(true)} className="p-1.5 rounded text-fg4 hover:text-danger transition-colors" title="Удалить">
            <Trash2 size={13} />
          </button>
        ) : (
          <div className="flex items-center gap-1.5 text-xs">
            <span className="text-fg3">Удалить?</span>
            <button onClick={() => del.mutateAsync({ id: template.id }).then(() => setConfirming(false))}
              disabled={del.isPending} className="px-2 py-0.5 rounded text-white bg-danger" style={{ fontSize: '11px' }}>
              Да
            </button>
            <button onClick={() => setConfirming(false)} className="px-2 py-0.5 rounded bg-muted text-fg2" style={{ fontSize: '11px' }}>
              Нет
            </button>
          </div>
        )}
      </div>

      {editing && (
        <div className="px-4 pb-4 bg-base">
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
      <p className="text-xs mb-4 text-fg4">
        Переиспользуемые рецепты источника (Extraction + Filter/Transformation/Sort). Применение
        к источнику копирует значения единожды (не живая ссылка) — дальше источник независим,
        можно свободно скорректировать (см. «применить шаблон» у каждого источника).
      </p>

      <div className="rounded-xl overflow-hidden mb-4 border border-stroke bg-surface">
        {isLoading ? (
          <div className="p-6 text-center text-sm text-fg4">Загрузка...</div>
        ) : templates.length === 0 && !adding ? (
          <div className="p-6 text-center text-sm text-fg4">Нет шаблонов обработки.</div>
        ) : (
          templates.map(t => <TemplateRow key={t.id} template={t} />)
        )}
      </div>

      {adding ? (
        <div className="rounded-xl p-4 border border-stroke bg-base">
          <p className="text-xs font-semibold mb-3 text-fg3">Новый шаблон</p>
          <TemplateForm onSave={handleCreate} onCancel={() => setAdding(false)} saving={create.isPending} />
        </div>
      ) : (
        <button onClick={() => setAdding(true)} className="flex items-center gap-2 text-sm font-medium px-3 py-2 rounded-md text-brand bg-brand-subtle">
          <Plus size={14} /> Добавить шаблон
        </button>
      )}
    </Modal>
  );
}
