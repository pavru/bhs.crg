import { useState } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { useCreateDataSetSource, useUpdateDataSetSource } from '@/shared/api/datasets';
import { XPathBuilder } from './xpath/XPathBuilder';
import type { ColumnExprDef, DataSetSource } from '@/shared/api/types';

/**
 * Ручное создание/редактирование источника XML-файла: имя + row-selector (XPathBuilder)
 * + список колонок (каждая — относительный XPathBuilder). Скаляр — частный случай:
 * row-selector с условиями сужен до одного узла, одна колонка (или несколько — тоже ок).
 */
export function SourceEditorDialog({ fileId, initial, onClose }: {
  fileId: string;
  initial?: DataSetSource;
  onClose: () => void;
}) {
  const [name, setName] = useState(initial?.name ?? '');
  const [sheetOrPath, setSheetOrPath] = useState(initial?.sheetOrPath ?? '/Root/Item');
  const [columns, setColumns] = useState<ColumnExprDef[]>(() => {
    try { return initial?.columnExpressions ? JSON.parse(initial.columnExpressions) : []; }
    catch { return []; }
  });
  const [error, setError] = useState('');

  const create = useCreateDataSetSource();
  const update = useUpdateDataSetSource();
  const isPending = create.isPending || update.isPending;

  function addColumn() {
    setColumns(prev => [...prev, { name: '', expr: '' }]);
  }
  function updateColumn(i: number, patch: Partial<ColumnExprDef>) {
    setColumns(prev => prev.map((c, idx) => idx === i ? { ...c, ...patch } : c));
  }
  function removeColumn(i: number) {
    setColumns(prev => prev.filter((_, idx) => idx !== i));
  }

  async function handleSave() {
    setError('');
    if (!name.trim()) { setError('Укажите название'); return; }
    if (!sheetOrPath.trim()) { setError('Укажите row-selector (путь к строкам)'); return; }
    const cleanColumns = columns.filter(c => c.name.trim() && c.expr.trim());

    try {
      if (initial) {
        await update.mutateAsync({
          id: initial.id, name: name.trim(), sheetOrPath: sheetOrPath.trim(),
          columnExpressions: cleanColumns.length ? cleanColumns : null,
        });
      } else {
        await create.mutateAsync({
          fileId, name: name.trim(), sheetOrPath: sheetOrPath.trim(),
          columnExpressions: cleanColumns.length ? cleanColumns : null,
        });
      }
      onClose();
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { error?: string } } })?.response?.data?.error;
      setError(msg ?? (e instanceof Error ? e.message : 'Ошибка сохранения'));
    }
  }

  return (
    <Modal open onOpenChange={open => { if (!open) onClose(); }}
      title={initial ? 'Редактировать источник' : 'Новый источник (XML)'} wide
      footer={
        <div className="flex justify-end gap-2">
          <button type="button" onClick={onClose}
            className="px-4 py-2 rounded-lg text-sm font-medium bg-base text-fg2 hover:bg-muted">
            Отмена
          </button>
          <button type="button" onClick={handleSave} disabled={isPending}
            className="px-4 py-2 rounded-lg text-sm font-medium bg-brand text-white hover:bg-brand-hover disabled:opacity-50">
            {isPending ? 'Сохранение…' : 'Сохранить'}
          </button>
        </div>
      }>
      <div className="space-y-4 min-w-[520px]">
        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">Название</label>
          <input value={name} onChange={e => setName(e.target.value)}
            placeholder="Позиции спецификации"
            className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm" />
        </div>

        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">
            Row-selector — путь к строкам (одна строка = один узел; условия сужают до конкретных)
          </label>
          <XPathBuilder value={sheetOrPath} onChange={setSheetOrPath} placeholder="/Root/Item" />
        </div>

        <div className="border-t border-stroke pt-3">
          <div className="flex items-center justify-between mb-2">
            <p className="text-sm font-medium text-fg1">
              Колонки <span className="text-xs font-normal text-fg4">— относительно узла строки</span>
            </p>
            <button type="button" onClick={addColumn}
              className="flex items-center gap-1 text-xs text-brand hover:text-brand-hover">
              <Plus size={13} /> Колонка
            </button>
          </div>
          {columns.length === 0 && (
            <p className="text-xs text-fg4 py-1">
              Колонок нет — будут определены автоматически по дочерним элементам/атрибутам строки.
            </p>
          )}
          <div className="space-y-2">
            {columns.map((col, i) => (
              <div key={i} className="flex items-start gap-2">
                <input value={col.name} onChange={e => updateColumn(i, { name: e.target.value })}
                  placeholder="Название колонки"
                  className="w-40 shrink-0 px-2 py-1.5 rounded-md border border-stroke-strong bg-surface text-sm" />
                <div className="flex-1 min-w-0">
                  <XPathBuilder value={col.expr} onChange={expr => updateColumn(i, { expr })}
                    placeholder="@id или Name или Info/Code" />
                </div>
                <button type="button" onClick={() => removeColumn(i)}
                  className="p-1.5 text-fg4 hover:text-danger shrink-0">
                  <Trash2 size={14} />
                </button>
              </div>
            ))}
          </div>
        </div>

        {error && <p className="text-sm text-danger">{error}</p>}
      </div>
    </Modal>
  );
}
