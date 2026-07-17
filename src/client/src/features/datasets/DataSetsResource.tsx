import { useRef, useState, type ReactNode } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Upload, Trash2, Database, RefreshCw, Download, FileText, LayoutGrid, ChevronRight } from 'lucide-react';
import { Button, IconButton } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useListDataSetFiles, useUploadDataSetFile } from '@/shared/api/datasets';
import type { CatalogScope, DataSetFile } from '@/shared/api/types';
import { DATA_SET_FORMAT_LABELS } from '@/shared/api/types';
import { ruCount } from '@/shared/utils/pluralize';
import { SourcesPanel } from './SourcesExpander';
import { useDataSetFileActions } from './useDataSetFileActions';

const ACCEPT = '.csv,.txt,.xlsx,.xls,.xml,.json,.zip,.gsfx,.pdf';
const ALL = 'all';

const sourcesLabel = (n: number) => `${ruCount(n, 'источник', 'источника', 'источников')}`;

/** Пункт рейла набора (issue #210, рейл-по-файлу): иконка формата + имя (2 строки: имя + «формат · N
 *  источников»), подсветка активного как у каталога. */
function FileNavItem({ icon, label, secondary, count, active, onClick }: {
  icon: ReactNode; label: string; secondary?: string; count?: number; active?: boolean; onClick: () => void;
}) {
  return (
    <button type="button" onClick={onClick} aria-current={active ? 'true' : undefined}
      className={`w-full flex items-center gap-2.5 px-2.5 py-1.5 rounded-lg text-left transition-colors ${
        active ? 'bg-brand-subtle text-brand-hover' : 'text-fg2 hover:bg-muted'}`}>
      <span className={`shrink-0 ${active ? 'text-brand-hover' : 'text-fg4'}`}>{icon}</span>
      <span className="flex-1 min-w-0">
        <span className={`block truncate text-sm ${active ? 'font-medium' : ''}`} title={label}>{label}</span>
        {secondary && <span className="block truncate text-[11px] text-fg4">{secondary}</span>}
      </span>
      {count != null && <span className="text-xs text-fg4 tabular-nums shrink-0">{count}</span>}
    </button>
  );
}

/** Detail выбранного набора: шапка (имя + формат + дата + скачать/обновить/удалить) + панель источников. */
function FileDetail({ file, scope, scopeId }: { file: DataSetFile; scope: CatalogScope; scopeId?: string }) {
  const {
    update, confirming, setConfirming, downloading,
    updateInputRef, handleReplace, handleDownload, handleDelete,
  } = useDataSetFileActions(file, scope, scopeId);

  return (
    <div className="rounded-lg border border-stroke bg-surface overflow-hidden">
      <div className="flex items-start justify-between gap-3 px-4 py-3 border-b border-stroke bg-base">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="text-sm font-semibold text-fg1 truncate" title={file.name}>{file.name}</span>
            <span className="shrink-0 text-[10px] font-medium px-1.5 py-0.5 rounded bg-muted text-fg3">{DATA_SET_FORMAT_LABELS[file.format]}</span>
          </div>
          <div className="text-xs text-fg4 mt-0.5">
            {sourcesLabel(file.sources.length)} · загружен {new Date(file.createdAt).toLocaleDateString('ru-RU')}
          </div>
        </div>
        <div className="flex items-center gap-1 shrink-0">
          <input ref={updateInputRef} type="file" accept={ACCEPT} className="hidden" onChange={handleReplace} />
          <IconButton label="Скачать оригинальный файл" size="sm" onClick={handleDownload} disabled={downloading}>
            <Download size={15} className={downloading ? 'opacity-50' : ''} />
          </IconButton>
          <IconButton label="Обновить файл (привязки сохранятся)" size="sm"
            onClick={() => updateInputRef.current?.click()} disabled={update.isPending}>
            <RefreshCw size={15} className={update.isPending ? 'animate-spin' : ''} />
          </IconButton>
          <IconButton label="Удалить файл" size="sm" danger onClick={() => setConfirming(true)}>
            <Trash2 size={15} />
          </IconButton>
        </div>
      </div>
      <div className="px-4 py-3">
        <SourcesPanel file={file} />
      </div>
      <ConfirmDialog open={confirming} onOpenChange={o => { if (!o) setConfirming(false); }}
        title={`Удалить файл «${file.name}»?`}
        description={<p>Файл и все его источники будут удалены. Привязки, ссылающиеся на него, перестанут работать.</p>}
        confirmLabel="Удалить файл" onConfirm={() => { void handleDelete(); }} />
    </div>
  );
}

/** Обзор «Все наборы»: карточки-сводки БЕЗ раскрытия источников (иначе «простыня» вернётся). */
function AllFilesOverview({ files, onOpen }: { files: DataSetFile[]; onOpen: (id: string) => void }) {
  return (
    <div className="space-y-2">
      {files.map(f => {
        const names = f.sources.map(s => s.name).slice(0, 4).join(' · ');
        return (
          <div key={f.id} className="flex items-center gap-3 rounded-lg border border-stroke bg-surface px-4 py-3">
            <FileText size={16} className="text-brand shrink-0" />
            <div className="flex-1 min-w-0">
              <div className="flex items-center gap-2">
                <span className="text-sm font-medium text-fg1 truncate" title={f.name}>{f.name}</span>
                <span className="shrink-0 text-[10px] font-medium px-1.5 py-0.5 rounded bg-muted text-fg3">{DATA_SET_FORMAT_LABELS[f.format]}</span>
              </div>
              <div className="text-xs text-fg4 mt-0.5 truncate">
                {sourcesLabel(f.sources.length)}{names && `: ${names}`}{f.sources.length > 4 ? ' …' : ''}
              </div>
            </div>
            <Button variant="outlined" size="sm" onClick={() => onOpen(f.id)} icon={<ChevronRight size={14} />}>Открыть</Button>
          </div>
        );
      })}
    </div>
  );
}

/**
 * Браузер наборов данных для ЛЮБОГО scope (issue #210, ось видимости): list-detail с рейлом ПО ФАЙЛУ —
 * слева «Все наборы» + файлы, справа detail выбранного набора (источники) или обзор-сводки. Рейл ради
 * ФОКУСА (один файл = один экран), инлайн-«простыня» источников исключена. Выбор — в URL `?file=id`.
 */
export function DataSetsResource({ scope, scopeId }: { scope: CatalogScope; scopeId?: string }) {
  const { data: files = [], isLoading } = useListDataSetFiles(scope, scopeId);
  const upload = useUploadDataSetFile();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [uploading, setUploading] = useState(false);
  const [uploadError, setUploadError] = useState('');

  const [searchParams, setSearchParams] = useSearchParams();
  const fileParam = searchParams.get('file');
  const setSelected = (v: string | null) => setSearchParams(prev => {
    const next = new URLSearchParams(prev);
    if (v) next.set('file', v); else next.delete('file');
    return next;
  }, { replace: true });

  // Дефолт по числу файлов: без явного выбора — 1 файл открывается сразу, 2+ → «Все наборы».
  const selected = fileParam ?? (files.length === 1 ? files[0].id : ALL);
  const isAll = selected === ALL;
  const selectedFile = !isAll ? files.find(f => f.id === selected) : undefined;

  async function handleFileInput(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (!file) return;
    setUploading(true);
    setUploadError('');
    try {
      const created = await upload.mutateAsync({ file, name: file.name.replace(/\.[^.]+$/, ''), scope, scopeId });
      if (created?.id) setSelected(created.id); // новый файл — открыть его
    } catch (err: unknown) {
      setUploadError(err instanceof Error ? err.message : 'Ошибка загрузки');
    } finally {
      setUploading(false);
      if (fileInputRef.current) fileInputRef.current.value = '';
    }
  }

  const uploadBtn = (
    <>
      <input ref={fileInputRef} type="file" accept={ACCEPT} className="hidden" onChange={handleFileInput} />
      <Button variant="tonal" size="sm" onClick={() => fileInputRef.current?.click()}
        loading={uploading} icon={<Upload size={14} />}>
        {uploading ? 'Загрузка…' : 'Загрузить файл'}
      </Button>
    </>
  );

  if (isLoading) return <div className="p-8 text-center text-sm text-fg4">Загрузка...</div>;

  if (files.length === 0) {
    return (
      <>
        <input ref={fileInputRef} type="file" accept={ACCEPT} className="hidden" onChange={handleFileInput} />
        <EmptyState icon={<Database size={30} />} title="Пока нет наборов данных"
          description="Загрузите файл (CSV, XLSX, XML, JSON, ZIP, PDF) — из него можно собирать источники для колонок и таблиц документов."
          action={<Button variant="filled" size="sm" onClick={() => fileInputRef.current?.click()}
            loading={uploading} icon={<Upload size={14} />}>Загрузить файл</Button>} />
        {uploadError && <p className="text-xs text-danger text-center mt-2">{uploadError}</p>}
      </>
    );
  }

  return (
    <div className="flex gap-5 items-start">
      {/* Рейл файлов */}
      <aside className="w-56 shrink-0 sticky top-0 self-start space-y-0.5">
        <FileNavItem icon={<LayoutGrid size={15} />} label="Все наборы" count={files.length}
          active={isAll} onClick={() => setSelected(ALL)} />
        {files.map(f => (
          <FileNavItem key={f.id} icon={<FileText size={15} />} label={f.name}
            secondary={`${DATA_SET_FORMAT_LABELS[f.format]} · ${sourcesLabel(f.sources.length)}`}
            active={selected === f.id} onClick={() => setSelected(f.id)} />
        ))}
        <div className="pt-2">{uploadBtn}</div>
        {uploadError && <p className="text-[11px] text-danger px-1 pt-1">{uploadError}</p>}
      </aside>

      {/* Detail */}
      <div className="flex-1 min-w-0">
        {isAll ? (
          <AllFilesOverview files={files} onOpen={setSelected} />
        ) : selectedFile ? (
          <FileDetail file={selectedFile} scope={scope} scopeId={scopeId} />
        ) : (
          <div className="text-center py-10 text-fg4 text-sm">
            Набор не найден. <button className="text-brand hover:text-brand-hover" onClick={() => setSelected(ALL)}>Ко всем наборам</button>
          </div>
        )}
      </div>
    </div>
  );
}
