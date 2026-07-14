import { useRef, useState } from 'react';
import { Button, IconButton } from '@/shared/ui/Button';
import { Upload, Trash2, Database, RefreshCw, Download, Layers } from 'lucide-react';
import { useListDataSetFiles, useUploadDataSetFile } from '@/shared/api/datasets';
import type { DataSetFile } from '@/shared/api/types';
import { DATA_SET_FORMAT_LABELS } from '@/shared/api/types';
import { SourcesExpander } from './SourcesExpander';
import { useDataSetFileActions } from './useDataSetFileActions';
import { ProcessingTemplatesDialog } from './ProcessingTemplatesDialog';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';

function FileRow({ file }: { file: DataSetFile }) {
  const {
    update, confirming, setConfirming, downloading,
    updateInputRef, handleReplace, handleDownload, handleDelete,
  } = useDataSetFileActions(file, 'System');

  return (
    <div className="flex flex-col gap-2 px-4 py-3 border-b border-stroke last:border-0">
      <div className="flex items-center gap-3">
        <Database size={16} className="text-brand shrink-0" />
        <div className="flex-1 min-w-0">
          <div className="font-medium text-sm text-fg1">{file.name}</div>
          <div className="text-xs mt-0.5 text-fg4">{DATA_SET_FORMAT_LABELS[file.format]}</div>
        </div>
        <input ref={updateInputRef} type="file" accept=".csv,.txt,.xlsx,.xls,.xml,.json,.zip,.gsfx,.pdf" className="hidden" onChange={handleReplace} />
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
      <ConfirmDialog
        open={confirming}
        onOpenChange={o => { if (!o) setConfirming(false); }}
        title={`Удалить файл «${file.name}»?`}
        description={<p>Файл и все его источники будут удалены. Привязки, ссылающиеся на него, перестанут работать.</p>}
        confirmLabel="Удалить файл"
        onConfirm={() => { void handleDelete(); }}
      />
    </div>
  );
}

export function DataSetsPage() {
  const { data: files = [], isLoading } = useListDataSetFiles('System');
  const upload = useUploadDataSetFile();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [uploading, setUploading] = useState(false);
  const [uploadError, setUploadError] = useState('');
  const [templatesOpen, setTemplatesOpen] = useState(false);

  async function handleFileInput(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (!file) return;
    setUploading(true);
    setUploadError('');
    try {
      await upload.mutateAsync({ file, name: file.name.replace(/\.[^.]+$/, ''), scope: 'System' });
    } catch (err: unknown) {
      setUploadError(err instanceof Error ? err.message : 'Ошибка загрузки');
    } finally {
      setUploading(false);
      if (fileInputRef.current) fileInputRef.current.value = '';
    }
  }

  return (
    <div className="px-6 py-4 max-w-3xl">
      <div className="flex items-center justify-between mb-1">
        <h1 className="text-xl font-semibold text-fg1">
          Наборы данных
        </h1>
        <button onClick={() => setTemplatesOpen(true)}
          className="flex items-center gap-2 text-sm font-medium px-3 py-1.5 rounded-md transition-colors text-fg2 bg-muted hover:bg-brand-subtle hover:text-brand">
          <Layers size={14} /> Шаблоны обработки
        </button>
      </div>
      <p className="text-xs mb-4 text-fg4">
        Системные наборы доступны во всех комплектах. Наборы уровня стройки, раздела и комплекта управляются на соответствующих страницах.
      </p>

      <div className="rounded-lg overflow-hidden border border-stroke bg-surface">
        <div className="flex items-center justify-between px-4 py-3 border-b border-stroke bg-base">
          <span className="text-sm font-medium text-fg2">Системный уровень</span>
          <div className="flex items-center gap-2">
            {uploadError && <span className="text-xs text-danger">{uploadError}</span>}
            <input
              ref={fileInputRef}
              type="file"
              accept=".csv,.txt,.xlsx,.xls,.xml,.json,.zip,.gsfx,.pdf"
              className="hidden"
              onChange={handleFileInput}
            />
            <Button variant="filled" size="sm" onClick={() => fileInputRef.current?.click()}
              loading={uploading} icon={<Upload size={14} />}>
              {uploading ? 'Загрузка…' : 'Загрузить файл'}
            </Button>
          </div>
        </div>

        {isLoading ? (
          <div className="p-8 text-center text-sm text-fg4">Загрузка...</div>
        ) : files.length === 0 ? (
          <div className="p-8 text-center text-sm text-fg4">
            Нет загруженных наборов данных
          </div>
        ) : (
          files.map(f => <FileRow key={f.id} file={f} />)
        )}
      </div>

      <p className="mt-3 text-xs text-fg4">
        Поддерживаемые форматы: CSV, TXT, XLSX, XLS, XML, JSON, ZIP, PDF.
      </p>

      {templatesOpen && <ProcessingTemplatesDialog onClose={() => setTemplatesOpen(false)} />}
    </div>
  );
}
