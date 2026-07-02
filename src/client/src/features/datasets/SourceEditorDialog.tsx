import { useState } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { useCreateDataSetSource, useUpdateDataSetSource, useListZipXmlEntries } from '@/shared/api/datasets';
import { XPathBuilder } from './xpath/XPathBuilder';
import { JsonPathBuilder } from './jsonpath/JsonPathBuilder';
import type { ColumnExprDef, DataSetSource } from '@/shared/api/types';

type SourceFormat = 'Xml' | 'Json';

const DEFAULT_ROW_SELECTOR: Record<SourceFormat, string> = { Xml: '/Root/Item', Json: '$.items[*]' };

/** Для ZIP-архивов sheetOrPath хранится как "путь/в/архиве.xml::/Row/Selector" (см. ZipDataSetParser). */
function splitZipPath(sheetOrPath: string | undefined, isZip: boolean, format: SourceFormat): { entryPath: string; rowSelector: string } {
  if (!isZip) return { entryPath: '', rowSelector: sheetOrPath ?? DEFAULT_ROW_SELECTOR[format] };
  if (!sheetOrPath) return { entryPath: '', rowSelector: DEFAULT_ROW_SELECTOR[format] };
  const idx = sheetOrPath.indexOf('::');
  return idx < 0
    ? { entryPath: sheetOrPath, rowSelector: DEFAULT_ROW_SELECTOR[format] }
    : { entryPath: sheetOrPath.slice(0, idx), rowSelector: sheetOrPath.slice(idx + 2) };
}

/**
 * Ручное создание/редактирование источника XML- или JSON-файла (в т.ч. XML внутри ZIP/GSFX):
 * имя + (для ZIP — выбор файла в архиве) + row-selector (XPath/JSONPath-builder) + список колонок
 * (каждая — относительный путь). Скаляр — частный случай: row-selector с условиями/фильтром
 * сужен до одного узла, одна колонка (или несколько — тоже ок).
 */
export function SourceEditorDialog({ fileId, isZip = false, format = 'Xml', initial, onClose }: {
  fileId: string;
  isZip?: boolean;
  format?: SourceFormat;
  initial?: DataSetSource;
  onClose: () => void;
}) {
  const PathBuilder = format === 'Json' ? JsonPathBuilder : XPathBuilder;
  const initialSplit = splitZipPath(initial?.sheetOrPath, isZip, format);
  const [name, setName] = useState(initial?.name ?? '');
  const [entryPath, setEntryPath] = useState(initialSplit.entryPath);
  const [sheetOrPath, setSheetOrPath] = useState(initialSplit.rowSelector);
  const [columns, setColumns] = useState<ColumnExprDef[]>(() => {
    try { return initial?.columnExpressions ? JSON.parse(initial.columnExpressions) : []; }
    catch { return []; }
  });
  const [error, setError] = useState('');

  const { data: zipEntries = [] } = useListZipXmlEntries(isZip ? fileId : undefined);
  const create = useCreateDataSetSource();
  const update = useUpdateDataSetSource();
  const isPending = create.isPending || update.isPending;

  // Текущее значение может отсутствовать в списке (архив обновился) — не терять его молча.
  const entryOptions = entryPath && !zipEntries.includes(entryPath) ? [entryPath, ...zipEntries] : zipEntries;

  // Полный row-selector с учётом ZIP-адресации — для предпросмотра (null = ещё не готов, напр.
  // архив без выбранного файла). Колонки предпросматриваются относительно него же.
  function composeRowSelector(rs: string): string | null {
    if (!rs.trim()) return null;
    if (!isZip) return rs;
    return entryPath.trim() ? `${entryPath.trim()}::${rs.trim()}` : null;
  }

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
    if (isZip && !entryPath.trim()) { setError('Выберите файл внутри архива'); return; }
    if (!sheetOrPath.trim()) { setError('Укажите row-selector (путь к строкам)'); return; }
    const cleanColumns = columns.filter(c => c.name.trim() && c.expr.trim());
    const finalSheetOrPath = isZip ? `${entryPath.trim()}::${sheetOrPath.trim()}` : sheetOrPath.trim();

    try {
      if (initial) {
        await update.mutateAsync({
          id: initial.id, name: name.trim(), sheetOrPath: finalSheetOrPath,
          columnExpressions: cleanColumns.length ? cleanColumns : null,
        });
      } else {
        await create.mutateAsync({
          fileId, name: name.trim(), sheetOrPath: finalSheetOrPath,
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
      title={initial ? 'Редактировать источник' : `Новый источник (${isZip ? 'XML в архиве' : format})`} wide
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

        {isZip && (
          <div>
            <label className="block text-sm font-medium text-fg1 mb-1">Файл в архиве</label>
            <select value={entryPath} onChange={e => setEntryPath(e.target.value)}
              className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm font-mono">
              <option value="">— выберите XML-файл в архиве —</option>
              {entryOptions.map(p => <option key={p} value={p}>{p}</option>)}
            </select>
            {entryOptions.length === 0 && (
              <p className="text-xs text-fg4 mt-1">В архиве не найдено XML-файлов.</p>
            )}
          </div>
        )}

        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">
            Row-selector — путь к строкам (одна строка = один узел; условия сужают до конкретных)
          </label>
          <PathBuilder value={sheetOrPath} onChange={setSheetOrPath} placeholder={DEFAULT_ROW_SELECTOR[format]}
            preview={v => { const rs = composeRowSelector(v); return rs ? { fileId, rowSelector: rs } : null; }} />
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
                  <PathBuilder value={col.expr} onChange={expr => updateColumn(i, { expr })}
                    placeholder={format === 'Json' ? 'id или name или info.code' : '@id или Name или Info/Code'}
                    preview={v => { const rs = composeRowSelector(sheetOrPath); return rs && v.trim() ? { fileId, rowSelector: rs, expr: v } : null; }} />
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
