import { useState } from 'react';
import { Plus, Trash2, Pencil, Check, Layers, Filter, FunctionSquare, ArrowUpDown } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import {
  useListProcessingTemplates, useCreateProcessingTemplate,
  useUpdateProcessingTemplate, useDeleteProcessingTemplate,
} from '@/shared/api/datasets';
import { countFilterConditions } from '@/shared/api/datasetHelpers';
import { RowFilterDialog } from './RowFilterDialog';
import { ComputedColumnsDialog } from './ComputedColumnsDialog';
import { SortSpecDialog } from './SortSpecDialog';
import type { ComputedColumn, DataSetProcessingTemplate, RowFilterDef, SortSpec } from '@/shared/api/types';

const FIELD_CLS = 'border border-stroke rounded-md px-3 py-1.5 text-sm bg-surface text-fg1';

interface TemplateFormState {
  name: string;
  rowFilter: RowFilterDef | null;
  computedColumns: ComputedColumn[] | null;
  sortSpec: SortSpec | null;
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
          onClick={() => onSave({ name, rowFilter, computedColumns, sortSpec })}
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
            {filterCount === 0 && transformCount === 0 && sortCount === 0 && (
              <span className="text-xs text-fg4">Без обработки</span>
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
        Переиспользуемые рецепты Filter/Conversion/Sort — источник данных может сослаться на
        шаблон вместо своей настройки; правка шаблона сразу применяется ко всем источникам,
        которые на него ссылаются (см. выбор «шаблон обработки» у каждого источника).
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
