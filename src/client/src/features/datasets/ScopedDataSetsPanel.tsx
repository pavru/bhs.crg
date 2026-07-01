import { useState, useRef } from 'react';
import { Upload, Trash2, ChevronDown, ChevronRight, Database, RefreshCw, Download } from 'lucide-react';
import {
  useListDataSetFiles,
  useUploadDataSetFile,
} from '@/shared/api/datasets';
import type { CatalogScope, DataSetFile } from '@/shared/api/types';
import { DATA_SET_FORMAT_LABELS } from '@/shared/api/types';
import { SourcesExpander } from './SourcesExpander';
import { useDataSetFileActions } from './useDataSetFileActions';

function FileRow({ file, scope, scopeId }: { file: DataSetFile; scope: CatalogScope; scopeId?: string }) {
  const {
    del, update, confirming, setConfirming, downloading,
    updateInputRef, handleReplace, handleDownload, handleDelete,
  } = useDataSetFileActions(file, scope, scopeId);

  return (
    <div className="flex flex-col gap-2 px-3 py-2.5 border-b border-stroke last:border-0">
      <div className="flex items-center gap-2">
        <Database size={13} className="text-brand shrink-0" />
        <div className="flex-1 min-w-0">
          <span className="text-sm font-medium text-fg1">{file.name}</span>
          <span className="ml-2 text-xs text-fg4">{DATA_SET_FORMAT_LABELS[file.format]}</span>
        </div>
        <input ref={updateInputRef} type="file" accept=".csv,.txt,.xlsx,.xls,.xml,.json,.zip,.gsfx" className="hidden" onChange={handleReplace} />
        <button
          onClick={handleDownload}
          disabled={downloading}
          className="p-1 rounded transition-colors disabled:opacity-40 text-fg4 hover:text-brand"
          title="Скачать оригинальный файл"
        >
          <Download size={13} className={downloading ? 'opacity-50' : ''} />
        </button>
        <button
          onClick={() => updateInputRef.current?.click()}
          disabled={update.isPending}
          className="p-1 rounded transition-colors disabled:opacity-40 text-fg4 hover:text-brand"
          title="Обновить файл (привязки сохранятся)"
        >
          <RefreshCw size={13} className={update.isPending ? 'animate-spin' : ''} />
        </button>
        {!confirming ? (
          <button
            onClick={() => setConfirming(true)}
            className="p-1 rounded transition-colors text-fg4 hover:text-danger"
            title="Удалить"
          >
            <Trash2 size={13} />
          </button>
        ) : (
          <div className="flex items-center gap-1.5 text-xs">
            <span className="text-fg3">Удалить?</span>
            <button onClick={handleDelete}
              disabled={del.isPending}
              className="px-2 py-0.5 rounded text-white bg-danger" style={{ fontSize: '11px' }}>
              Да
            </button>
            <button onClick={() => setConfirming(false)}
              className="px-2 py-0.5 rounded bg-muted text-fg2" style={{ fontSize: '11px' }}>
              Нет
            </button>
          </div>
        )}
      </div>
      <div className="pl-5">
        <SourcesExpander file={file} maxColumns={6} />
      </div>
    </div>
  );
}

export function ScopedDataSetsPanel({ scope, scopeId }: { scope: CatalogScope; scopeId?: string }) {
  const [expanded, setExpanded] = useState(false);
  const { data: files = [], isLoading } = useListDataSetFiles(scope, scopeId);
  const upload = useUploadDataSetFile();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [uploading, setUploading] = useState(false);
  const [uploadError, setUploadError] = useState('');

  async function handleFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (!file) return;
    setUploading(true);
    setUploadError('');
    try {
      await upload.mutateAsync({ file, name: file.name.replace(/\.[^.]+$/, ''), scope, scopeId });
      setExpanded(true);
    } catch (err: unknown) {
      setUploadError(err instanceof Error ? err.message : 'Ошибка загрузки');
    } finally {
      setUploading(false);
      if (fileInputRef.current) fileInputRef.current.value = '';
    }
  }

  const count = files.length;

  return (
    <div className="mt-3 rounded-xl overflow-hidden border border-stroke">
      {/* Header */}
      <div
        className="flex items-center gap-2 px-3 py-2 cursor-pointer select-none bg-base"
        onClick={() => setExpanded(o => !o)}
      >
        <Database size={13} className="text-brand" />
        <span className="text-xs font-medium flex-1 text-fg2">
          Наборы данных
          {count > 0 && <span className="ml-1.5 text-[10px] px-1.5 py-0.5 rounded-full bg-brand-subtle text-brand">{count}</span>}
        </span>

        <div onClick={e => e.stopPropagation()} className="flex items-center gap-1">
          {uploadError && <span className="text-xs text-danger">{uploadError}</span>}
          <input ref={fileInputRef} type="file" accept=".csv,.txt,.xlsx,.xls,.xml,.json,.zip,.gsfx" className="hidden" onChange={handleFile} />
          <button
            onClick={() => fileInputRef.current?.click()}
            disabled={uploading}
            className="flex items-center gap-1 text-xs px-2 py-1 rounded transition-colors disabled:opacity-40 text-brand"
          >
            <Upload size={11} />
            {uploading ? 'Загрузка...' : 'Загрузить'}
          </button>
        </div>

        {expanded ? <ChevronDown size={13} className="text-fg4" /> : <ChevronRight size={13} className="text-fg4" />}
      </div>

      {/* Files */}
      {expanded && (
        <div className="bg-surface">
          {isLoading ? (
            <div className="px-3 py-3 text-xs text-fg4">Загрузка...</div>
          ) : files.length === 0 ? (
            <div className="px-3 py-3 text-xs text-fg4">Нет загруженных наборов данных</div>
          ) : (
            files.map(f => <FileRow key={f.id} file={f} scope={scope} scopeId={scopeId} />)
          )}
        </div>
      )}
    </div>
  );
}
