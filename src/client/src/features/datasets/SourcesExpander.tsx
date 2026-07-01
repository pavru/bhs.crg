import { useState } from 'react';
import { ChevronDown, ChevronRight, Plus, Pencil, Trash2 } from 'lucide-react';
import { parseSourceColumnNames } from '@/shared/api/datasetHelpers';
import { useDeleteDataSetSource } from '@/shared/api/datasets';
import { SourceEditorDialog } from './SourceEditorDialog';
import type { DataSetFile, DataSetSource } from '@/shared/api/types';

/**
 * Collapsible list of a file's data sources with a preview of their column names.
 * For XML files — источники управляются вручную (создание/редактирование/удаление),
 * т.к. авто-детект по top-level элементам для XML не используется.
 */
export function SourcesExpander({
  file,
  maxColumns = 8,
}: {
  file: DataSetFile;
  maxColumns?: number;
}) {
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState<DataSetSource | 'new' | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<DataSetSource | null>(null);
  const deleteMutation = useDeleteDataSetSource();
  const isXml = file.format === 'Xml';
  const sources = file.sources;

  if (sources.length === 0 && !isXml)
    return <span className="text-xs text-fg4">Нет источников</span>;

  return (
    <div>
      <div className="flex items-center gap-2">
        <button onClick={() => setOpen(o => !o)} className="flex items-center gap-1 text-xs text-brand">
          {open ? <ChevronDown size={12} /> : <ChevronRight size={12} />}
          {sources.length > 0
            ? `${sources.length} ${sources.length === 1 ? 'источник' : 'источника(-ов)'}`
            : 'Нет источников'}
        </button>
        {isXml && (
          <button onClick={() => { setEditing('new'); setOpen(true); }}
            className="flex items-center gap-1 text-xs text-brand hover:text-brand-hover">
            <Plus size={11} /> Добавить источник
          </button>
        )}
      </div>
      {open && (
        <div className="mt-2 space-y-2 pl-3">
          {sources.map(src => {
            const cols = parseSourceColumnNames(src.cachedSchema);
            return (
              <div key={src.id} className="text-xs rounded-md p-2 bg-muted">
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <div className="font-medium text-fg1">
                      {src.name}
                      <span className="ml-2 font-normal text-fg4">{src.cachedRowCount} строк</span>
                    </div>
                    <div className="font-mono text-fg4 mt-0.5">{src.sheetOrPath}</div>
                    {cols.length > 0 && (
                      <div className="text-fg3 mt-0.5">
                        {cols.slice(0, maxColumns).join(', ')}{cols.length > maxColumns ? ` +${cols.length - maxColumns}` : ''}
                      </div>
                    )}
                  </div>
                  {isXml && (
                    <div className="flex items-center gap-1 shrink-0">
                      <button onClick={() => setEditing(src)} className="p-1 text-fg4 hover:text-brand" title="Редактировать">
                        <Pencil size={12} />
                      </button>
                      <button onClick={() => setConfirmDelete(src)} className="p-1 text-fg4 hover:text-danger" title="Удалить">
                        <Trash2 size={12} />
                      </button>
                    </div>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      )}

      {editing && (
        <SourceEditorDialog
          fileId={file.id}
          initial={editing === 'new' ? undefined : editing}
          onClose={() => setEditing(null)}
        />
      )}

      {confirmDelete && (
        <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/50 p-4"
          onClick={() => setConfirmDelete(null)}>
          <div className="rounded-xl p-5 w-full max-w-sm bg-surface border border-stroke shadow-2xl" onClick={e => e.stopPropagation()}>
            <p className="text-sm font-semibold mb-1 text-fg1">Удалить источник «{confirmDelete.name}»?</p>
            <p className="text-xs mb-4 text-fg3">
              Если он используется в привязках документов — удаление будет отклонено.
            </p>
            <div className="flex gap-2 justify-end">
              <button onClick={() => setConfirmDelete(null)}
                className="px-3 py-1.5 text-sm rounded-md border border-stroke text-fg2 hover:bg-muted">
                Отмена
              </button>
              <button
                onClick={() => deleteMutation.mutateAsync({ id: confirmDelete.id })
                  .then(() => setConfirmDelete(null))
                  .catch(() => {})}
                disabled={deleteMutation.isPending}
                className="px-3 py-1.5 text-sm rounded-md bg-danger text-white disabled:opacity-50">
                Удалить
              </button>
            </div>
            {deleteMutation.isError && (
              <p className="text-xs text-danger mt-2">
                {(deleteMutation.error as { response?: { data?: string } })?.response?.data
                  ?? 'Не удалось удалить — возможно, источник используется в привязках.'}
              </p>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
