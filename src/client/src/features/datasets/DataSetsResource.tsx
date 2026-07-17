import { useRef, useState } from 'react';
import { Upload, Trash2, Database, RefreshCw, Download } from 'lucide-react';
import { Button, IconButton } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useListDataSetFiles, useUploadDataSetFile } from '@/shared/api/datasets';
import type { CatalogScope, DataSetFile } from '@/shared/api/types';
import { DATA_SET_FORMAT_LABELS } from '@/shared/api/types';
import { SourcesExpander } from './SourcesExpander';
import { useDataSetFileActions } from './useDataSetFileActions';

const ACCEPT = '.csv,.txt,.xlsx,.xls,.xml,.json,.zip,.gsfx,.pdf';

function FileRow({ file, scope, scopeId }: { file: DataSetFile; scope: CatalogScope; scopeId?: string }) {
  const {
    update, confirming, setConfirming, downloading,
    updateInputRef, handleReplace, handleDownload, handleDelete,
  } = useDataSetFileActions(file, scope, scopeId);

  return (
    <div className="flex flex-col gap-2 px-4 py-3 border-b border-stroke last:border-0">
      <div className="flex items-center gap-3">
        <Database size={16} className="text-brand shrink-0" />
        <div className="flex-1 min-w-0">
          <div className="font-medium text-sm text-fg1">{file.name}</div>
          <div className="text-xs mt-0.5 text-fg4">{DATA_SET_FORMAT_LABELS[file.format]}</div>
        </div>
        <input ref={updateInputRef} type="file" accept={ACCEPT} className="hidden" onChange={handleReplace} />
        <IconButton label="Скачать оригинальный файл" size="sm" onClick={handleDownload} disabled={downloading}>
          <Download size={14} className={downloading ? 'opacity-50' : ''} />
        </IconButton>
        <IconButton label="Обновить файл (привязки сохранятся)" size="sm"
          onClick={() => updateInputRef.current?.click()} disabled={update.isPending}>
          <RefreshCw size={14} className={update.isPending ? 'animate-spin' : ''} />
        </IconButton>
        <IconButton label="Удалить" size="sm" danger onClick={() => setConfirming(true)}>
          <Trash2 size={14} />
        </IconButton>
      </div>
      <div className="pl-7">
        <SourcesExpander file={file} />
      </div>
      <ConfirmDialog open={confirming} onOpenChange={o => { if (!o) setConfirming(false); }}
        title={`Удалить файл «${file.name}»?`}
        description={<p>Файл и все его источники будут удалены. Привязки, ссылающиеся на него, перестанут работать.</p>}
        confirmLabel="Удалить файл" onConfirm={() => { void handleDelete(); }} />
    </div>
  );
}

/**
 * Браузер наборов данных для ЛЮБОГО scope (issue #210, ось видимости): загрузка + список файлов
 * с источниками. Единый компонент для system-страницы и scoped-панелей — область выражается
 * положением, НЕ чипом. Заголовок/контекст даёт вызывающий.
 */
export function DataSetsResource({ scope, scopeId }: { scope: CatalogScope; scopeId?: string }) {
  const { data: files = [], isLoading } = useListDataSetFiles(scope, scopeId);
  const upload = useUploadDataSetFile();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [uploading, setUploading] = useState(false);
  const [uploadError, setUploadError] = useState('');

  async function handleFileInput(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (!file) return;
    setUploading(true);
    setUploadError('');
    try {
      await upload.mutateAsync({ file, name: file.name.replace(/\.[^.]+$/, ''), scope, scopeId });
    } catch (err: unknown) {
      setUploadError(err instanceof Error ? err.message : 'Ошибка загрузки');
    } finally {
      setUploading(false);
      if (fileInputRef.current) fileInputRef.current.value = '';
    }
  }

  return (
    <div className="rounded-lg overflow-hidden border border-stroke bg-surface">
      <div className="flex items-center justify-between px-4 py-3 border-b border-stroke bg-base">
        <span className="text-sm font-medium text-fg2">Наборы данных</span>
        <div className="flex items-center gap-2">
          {uploadError && <span className="text-xs text-danger">{uploadError}</span>}
          <input ref={fileInputRef} type="file" accept={ACCEPT} className="hidden" onChange={handleFileInput} />
          <Button variant="tonal" size="sm" onClick={() => fileInputRef.current?.click()}
            loading={uploading} icon={<Upload size={14} />}>
            {uploading ? 'Загрузка…' : 'Загрузить файл'}
          </Button>
        </div>
      </div>

      {isLoading ? (
        <div className="p-8 text-center text-sm text-fg4">Загрузка...</div>
      ) : files.length === 0 ? (
        <EmptyState className="m-4 border-0" icon={<Database size={30} />} title="Пока нет наборов данных"
          description="Загрузите файл (CSV, XLSX, XML, JSON, ZIP, PDF) — из него можно собирать источники для колонок и таблиц документов."
          action={<Button variant="filled" size="sm" onClick={() => fileInputRef.current?.click()}
            loading={uploading} icon={<Upload size={14} />}>Загрузить файл</Button>} />
      ) : (
        files.map(f => <FileRow key={f.id} file={f} scope={scope} scopeId={scopeId} />)
      )}
    </div>
  );
}
