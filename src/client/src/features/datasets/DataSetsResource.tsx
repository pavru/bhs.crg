import { useMemo, useRef, useState, type ReactNode } from 'react';
import { useSearchParams } from 'react-router';
import { useRememberedSelection } from '@/shared/hooks/useRememberedSelection';
import { Upload, Trash2, Database, RefreshCw, Download, FileText, LayoutGrid, ChevronRight, Loader2 } from 'lucide-react';
import { Button, IconButton } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import {
  useCreateSystemDataSetFile, useListDataSetFiles, useSystemDataCandidates, useUploadDataSetFile,
} from '@/shared/api/datasets';
import { useRecognitionJobs, useCancelJob, type ActiveJob } from '@/shared/api/jobs';
import type { CatalogScope, DataSetFile } from '@/shared/api/types';
import { DATA_SET_FORMAT_LABELS, SCOPE_LABELS } from '@/shared/api/types';
import { ruCount } from '@/shared/utils/pluralize';
import { SourcesPanel, FileRecognizeActions } from './SourcesExpander';
import { useDataSetFileActions } from './useDataSetFileActions';

const ACCEPT = '.csv,.txt,.xlsx,.xls,.xml,.json,.zip,.gsfx,.pdf';
const ALL = 'all';
/** Порядок унаследованных наборов в рейле — от ближнего уровня к дальнему (issue #721). */
const INHERIT_ORDER: CatalogScope[] = ['Set', 'Section', 'Construction', 'System'];

const sourcesLabel = (n: number) => `${ruCount(n, 'источник', 'источника', 'источников')}`;

/** Пункт рейла набора (issue #210, рейл-по-файлу): иконка формата ИЛИ спиннер (идёт распознавание) +
 *  имя (2 строки: имя + «формат · N источников»), amber-точка при устаревших источниках, подсветка как
 *  у каталога. Статус в рейле — спокойный: только спиннер/точка, без текста (один факт — один раз). */
function FileNavItem({ icon, label, secondary, count, active, recognizing, stale, level, onClick }: {
  icon: ReactNode; label: string; secondary?: string; count?: number; active?: boolean;
  recognizing?: boolean; stale?: boolean; level?: ReactNode; onClick: () => void;
}) {
  return (
    <button type="button" onClick={onClick} aria-current={active ? 'true' : undefined}
      className={`w-full flex items-center gap-2.5 px-2.5 py-1.5 rounded-lg text-left transition-colors ${
        active ? 'bg-brand-subtle text-brand-hover' : 'text-fg2 hover:bg-muted'}`}>
      <span className={`shrink-0 ${active ? 'text-brand-hover' : 'text-fg4'}`}>
        {recognizing ? <Loader2 size={15} className="animate-spin text-brand" /> : icon}
      </span>
      <span className="flex-1 min-w-0">
        <span className={`block truncate text-sm ${active ? 'font-medium' : ''}`} title={label}>{label}</span>
        {/* Чип уровня — в начале подписи, а не в хвосте строки (issue #721): в хвосте он отнимал
            ширину у имени, а трёх «Данные системы» подряд без уровня не различить вовсе. */}
        {(secondary || level) && (
          <span className="flex items-center gap-1.5 text-[11px] text-fg4">
            {level}
            {secondary && <span className="truncate">{secondary}</span>}
          </span>
        )}
      </span>
      {stale && <span title="Есть устаревшие источники — распознайте набор заново" className="w-1.5 h-1.5 rounded-full bg-warning shrink-0" />}
      {count != null && <span className="text-xs text-fg4 tabular-nums shrink-0">{count}</span>}
    </button>
  );
}

/**
 * Уровень-владелец набора словами — теми же, что в селекторе источника у привязки (issue #721):
 * два экрана про одно и то же обязаны называть уровни одинаково.
 */
function LevelChip({ file }: { file: DataSetFile }) {
  return (
    <span title={`Набор принадлежит уровню «${SCOPE_LABELS[file.scope]}» — здесь он доступен, потому что этот уровень выше`}
      className="shrink-0 text-[10px] font-medium px-1.5 py-0.5 rounded bg-muted text-fg3 leading-tight">
      {SCOPE_LABELS[file.scope]}
    </span>
  );
}

/** Detail выбранного набора: шапка (имя + формат + дата + скачать/обновить/удалить) + баннер статуса
 *  распознавания (если идёт) + панель источников. */
function FileDetail({ file, inherited, job }: {
  file: DataSetFile; inherited: boolean; job?: ActiveJob;
}) {
  // Уровень для мутаций берём У ФАЙЛА, а не у экрана (issue #721): унаследованный набор правится
  // отсюда, но принадлежит своему уровню, и замена/удаление должны уйти от его имени.
  const {
    update, confirming, setConfirming, downloading,
    updateInputRef, handleReplace, handleDownload, handleDelete,
  } = useDataSetFileActions(file, file.scope, file.scopeId ?? undefined);
  const cancel = useCancelJob();
  const isSystem = file.format === 'System';

  return (
    <div className="rounded-lg border border-stroke bg-surface overflow-hidden">
      <div className="flex items-start justify-between gap-3 px-4 py-3 border-b border-stroke bg-base">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="text-sm font-semibold text-fg1 truncate" title={file.name}>{file.name}</span>
            <span className="shrink-0 text-[10px] font-medium px-1.5 py-0.5 rounded bg-muted text-fg3">{DATA_SET_FORMAT_LABELS[file.format]}</span>
            {inherited && <LevelChip file={file} />}
          </div>
          <div className="text-xs text-fg4 mt-0.5">
            {sourcesLabel(file.sources.length)} · {isSystem
              ? 'данные собираются системой при каждом обращении'
              : `загружен ${new Date(file.createdAt).toLocaleDateString('ru-RU')}`}
          </div>
        </div>
        <div className="flex items-center gap-1 shrink-0">
          {/* У системного набора файла нет: скачивать, заменять и распознавать нечего. */}
          {!isSystem && (
            <>
              <input ref={updateInputRef} type="file" accept={ACCEPT} className="hidden" onChange={handleReplace} />
              <FileRecognizeActions file={file} />
              <IconButton label="Скачать оригинальный файл" size="sm" onClick={handleDownload} disabled={downloading}>
                <Download size={15} className={downloading ? 'opacity-50' : ''} />
              </IconButton>
              <IconButton label="Обновить файл (привязки сохранятся)" size="sm"
                onClick={() => updateInputRef.current?.click()} disabled={update.isPending}>
                <RefreshCw size={15} className={update.isPending ? 'animate-spin' : ''} />
              </IconButton>
            </>
          )}
          <IconButton label="Удалить набор" size="sm" danger onClick={() => setConfirming(true)}>
            <Trash2 size={15} />
          </IconButton>
        </div>
      </div>
      {job && (
        <div className="flex items-center gap-2 px-4 py-2 text-xs bg-brand-subtle/50 border-b border-stroke text-fg2">
          <Loader2 size={13} className="animate-spin text-brand shrink-0" />
          <span className="flex-1 min-w-0 truncate">{job.progress ?? job.title ?? 'Распознаётся…'}</span>
          {job.status === 'Queued' && (
            <button onClick={() => cancel.mutate(job.id)} disabled={cancel.isPending}
              className="text-brand hover:text-brand-hover shrink-0 disabled:opacity-50">Отменить</button>
          )}
        </div>
      )}
      <div className="px-4 py-3">
        <SourcesPanel file={file} />
      </div>
      <ConfirmDialog open={confirming} onOpenChange={o => { if (!o) setConfirming(false); }}
        title={`Удалить набор «${file.name}»?`}
        description={<>
          {/* Унаследованный набор общий (issue #721): удаление отсюда — не «убрать у себя», а
              убрать у всех, кто им пользуется. Прав хватает, поэтому предупреждает текст. */}
          {inherited && (
            <p className="mb-2 text-warning">
              Набор принадлежит уровню «{SCOPE_LABELS[file.scope]}» и доступен всем, кто ниже, —
              он исчезнет не только здесь.
            </p>
          )}
          <p>
            {isSystem ? 'Набор' : 'Файл'} и все его источники будут удалены. Привязки, ссылающиеся на него, перестанут работать.
            {isSystem && ' Сами данные системы (документы, общие данные) при этом не тронутся.'}
          </p>
        </>}
        confirmLabel="Удалить набор" onConfirm={() => { void handleDelete(); }} />
    </div>
  );
}

/** Обзор «Все наборы»: карточки-сводки БЕЗ раскрытия источников (иначе «простыня» вернётся). */
function AllFilesOverview({ files, inheritedCount, onOpen }: {
  files: DataSetFile[]; inheritedCount: number; onOpen: (id: string) => void;
}) {
  // Своих нет, но что-то доступно сверху (issue #721) — сказать об этом, а не показывать пустоту:
  // обзор считает только свои, и без пояснения ноль читался бы как «данных нет вообще».
  if (files.length === 0) {
    return (
      <p className="text-sm text-fg4 py-6 text-center">
        На этом уровне своих наборов нет.
        {inheritedCount > 0 && ' Доступные с верхних уровней перечислены в списке слева.'}
      </p>
    );
  }
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
const SELECTION_KEYS = ['file'] as const;

export function DataSetsResource({ scope, scopeId }: { scope: CatalogScope; scopeId?: string }) {
  const { data: allFiles = [], isLoading } = useListDataSetFiles(scope, scopeId, true);
  const upload = useUploadDataSetFile();
  const createSystem = useCreateSystemDataSetFile();
  // Нечего консолидировать на этом уровне — системный набор здесь не предлагаем (issue #606).
  const { data: systemCandidates = [] } = useSystemDataCandidates(scope, scopeId);
  const recogJobs = useRecognitionJobs();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [uploading, setUploading] = useState(false);
  const [uploadError, setUploadError] = useState('');
  const [dragOver, setDragOver] = useState(false);

  const [searchParams, setSearchParams] = useSearchParams();
  // Выбор в URL + память последнего открытого (issue #787, общий хелпер). Ключ памяти — на уровень:
  // один компонент обслуживает систему, стройку, раздел и комплект, и набор одной стройки не должен
  // всплывать на другой. Раскрытость группы унаследованных (`?inherited=`) не запоминаем — она
  // выводится из выбора и живёт только в адресе.
  // Регистр уровня приводим так же, как в `isOwn` ниже: иначе адрес с другим регистром завёл бы
  // вторую, отдельную память для того же уровня.
  const { values, remember } = useRememberedSelection(
    `datasets-last:${scope}:${scopeId?.toLowerCase() ?? ''}`, SELECTION_KEYS);
  const setSelected = (v: string | null) => remember({ file: v ?? '' });

  // Свернув группу, снимаем выбор с унаследованного набора: иначе detail показывал бы набор,
  // строки которого в рейле уже нет, и подсвечивать было бы нечего.
  const setInheritedOpen = (open: boolean, dropSelection = false) => {
    const flag = (params: URLSearchParams) => params.set('inherited', open ? '1' : '0');
    // Снятие выбора и флаг группы — одним обновлением: два setSearchParams в одном такте не
    // накапливаются, и второй вернул бы удалённый `file` обратно.
    if (!open && dropSelection) remember({ file: '' }, flag);
    else setSearchParams(prev => { const next = new URLSearchParams(prev); flag(next); return next; }, { replace: true });
  };

  // Свои — наборы этого уровня; унаследованные — всё остальное из цепочки родителей.
  // Всё, что говорит о владении (счётчик, пустое состояние, гейт «Данные системы», обзор),
  // считает СВОИ: иначе набор раздела спрячет кнопку создания набора комплекта.
  // Идентификатор уровня приходит из адреса, а сравнивается со строкой из ответа API: регистр
  // может разойтись, и тогда СВОИ наборы поголовно считались бы унаследованными — со счётчиком 0,
  // предупреждением при удалении своего же файла и повторным предложением создать «Данные системы».
  const isOwn = useMemo(() => {
    const here = scopeId?.toLowerCase();
    return (f: DataSetFile) => f.scope === scope && (f.scopeId?.toLowerCase() ?? undefined) === here;
  }, [scope, scopeId]);

  const files = useMemo(() => allFiles.filter(isOwn), [allFiles, isOwn]);
  const inheritedFiles = useMemo(
    () => allFiles.filter(f => !isOwn(f))
      // Ближний уровень выше: комплект → раздел → стройка → система.
      .sort((a, b) => INHERIT_ORDER.indexOf(a.scope) - INHERIT_ORDER.indexOf(b.scope) || a.name.localeCompare(b.name, 'ru')),
    [allFiles, isOwn]);

  // Восстановленный набор мог быть удалён или относиться к другому уровню — тогда память молча
  // пропускается; иначе экран открывался бы на «Набор не найден».
  const remembered = values.file || null;
  const known = remembered === ALL || (remembered && allFiles.some(f => f.id === remembered))
    ? remembered : null;
  // Дефолт по числу файлов: без явного выбора — 1 файл открывается сразу, 2+ → «Все наборы».
  // Пока список едет, он пуст, и дефолтом оказывается «Все наборы» — рейл в это время всё равно
  // закрыт индикатором загрузки.
  const selected = known ?? (files.length === 1 ? files[0].id : ALL);
  const isAll = selected === ALL;
  const selectedFile = !isAll ? allFiles.find(f => f.id === selected) : undefined;
  const selectedInherited = !!selectedFile && !files.some(f => f.id === selectedFile.id);
  const hasSystemFile = files.some(f => f.format === 'System');
  const offerSystemFile = !hasSystemFile && systemCandidates.length > 0;

  // Группа унаследованных свёрнута по умолчанию (issue #721): экран уровня остаётся про свои наборы,
  // а строка со счётчиком не даёт остальным быть невидимыми. Явный выбор живёт в URL рядом с ?file=;
  // без него группа раскрывается, когда открыт унаследованный набор, — иначе ссылка на такой набор
  // показывала бы detail, которого нет в рейле.
  const inheritedParam = searchParams.get('inherited');
  const inheritedOpen = inheritedParam !== null ? inheritedParam === '1' : selectedInherited;

  // Статус набора: активная задача распознавания (targetId = file.id для ГОСТ, иначе id источника).
  const jobForFile = (f: DataSetFile) =>
    recogJobs.find(j => j.targetId === f.id || f.sources.some(s => s.id === j.targetId));
  const fileStale = (f: DataSetFile) => f.sources.some(s => s.recognitionStale);

  async function uploadFile(file: File) {
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
  function handleFileInput(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (file) void uploadFile(file);
  }

  // Drag-drop загрузка — вся панель drop-target с dashed-оверлеем при dragover.
  function onDragOver(e: React.DragEvent) {
    if ([...e.dataTransfer.types].includes('Files')) { e.preventDefault(); setDragOver(true); }
  }
  function onDragLeave(e: React.DragEvent) {
    if (!e.currentTarget.contains(e.relatedTarget as Node)) setDragOver(false);
  }
  function onDrop(e: React.DragEvent) {
    e.preventDefault(); setDragOver(false);
    const file = e.dataTransfer.files?.[0];
    if (file) void uploadFile(file);
  }

  async function addSystemFile() {
    setUploadError('');
    try {
      const created = await createSystem.mutateAsync({ scope, scopeId });
      if (created?.id) setSelected(created.id);
    } catch (err: unknown) {
      setUploadError(err instanceof Error ? err.message : 'Не удалось создать набор');
    }
  }

  const uploadBtn = (
    <>
      <input ref={fileInputRef} type="file" accept={ACCEPT} className="hidden" onChange={handleFileInput} />
      <Button variant="tonal" size="sm" onClick={() => fileInputRef.current?.click()}
        loading={uploading} icon={<Upload size={14} />}>
        {uploading ? 'Загрузка…' : 'Загрузить файл'}
      </Button>
      {/* Набор без файла: сырьё — данные самой системы. На уровень нужен один, поэтому кнопка
          прячется, как только он появился, и не показывается там, где консолидаций нет. */}
      {offerSystemFile && (
        <Button variant="text" size="sm" onClick={() => { void addSystemFile(); }}
          loading={createSystem.isPending} icon={<Database size={14} />}>
          Данные системы
        </Button>
      )}
    </>
  );

  const overlay = dragOver && (
    <div className="absolute inset-0 z-10 rounded-lg border-2 border-dashed border-brand bg-brand-subtle/60 flex items-center justify-center pointer-events-none">
      <span className="text-sm font-medium text-brand-hover">Отпустите файл — загрузим в набор</span>
    </div>
  );

  return (
    <div className="relative" onDragOver={onDragOver} onDragLeave={onDragLeave} onDrop={onDrop}>
      {overlay}
      {isLoading ? (
        <div className="p-8 text-center text-sm text-fg4">Загрузка...</div>
      ) : allFiles.length === 0 ? (
        // Пусто — когда нет ни своих, ни унаследованных (issue #721): при одних унаследованных
        // экран обязан остаться рейлом, иначе к ним нет входа вовсе.
        <>
          <input ref={fileInputRef} type="file" accept={ACCEPT} className="hidden" onChange={handleFileInput} />
          <EmptyState icon={<Database size={30} />} title="Пока нет наборов данных"
            description={'Загрузите файл (CSV, XLSX, XML, JSON, ZIP, PDF) или перетащите его сюда — из него можно собирать источники для колонок и таблиц документов.'
              + (offerSystemFile ? ' Либо возьмите данные самой системы: например, перечень документов комплекта.' : '')}
            action={
              <div className="flex items-center gap-2">
                <Button variant="filled" size="sm" onClick={() => fileInputRef.current?.click()}
                  loading={uploading} icon={<Upload size={14} />}>Загрузить файл</Button>
                {offerSystemFile && (
                  <Button variant="outlined" size="sm" onClick={() => { void addSystemFile(); }}
                    loading={createSystem.isPending} icon={<Database size={14} />}>Данные системы</Button>
                )}
              </div>
            } />
          {uploadError && <p className="text-xs text-danger text-center mt-2">{uploadError}</p>}
        </>
      ) : (
        <div className="flex gap-5 items-start">
          {/* Рейл файлов */}
          <aside className="w-56 shrink-0 sticky top-0 self-start space-y-0.5">
            <FileNavItem icon={<LayoutGrid size={15} />} label="Все наборы" count={files.length}
              active={isAll} onClick={() => setSelected(ALL)} />
            {files.map(f => (
              <FileNavItem key={f.id} icon={<FileText size={15} />} label={f.name}
                secondary={`${DATA_SET_FORMAT_LABELS[f.format]} · ${sourcesLabel(f.sources.length)}`}
                active={selected === f.id} recognizing={!!jobForFile(f)} stale={fileStale(f)}
                onClick={() => setSelected(f.id)} />
            ))}

            {/* Наборы верхних уровней (issue #721): раньше их тут не было вовсе, хотя привязка их
                предлагает. Свёрнуты, потому что экран — про наборы этого уровня; строка со
                счётчиком сообщает, что доступно ещё, и не даёт им остаться невидимыми. */}
            {inheritedFiles.length > 0 && (
              <>
                <button type="button" onClick={() => setInheritedOpen(!inheritedOpen, selectedInherited)}
                  aria-expanded={inheritedOpen}
                  className="w-full flex items-center gap-1.5 mt-2 pt-2 px-1.5 border-t border-stroke text-left text-xs text-fg3 hover:text-brand">
                  <ChevronRight size={13} className={`shrink-0 transition-transform ${inheritedOpen ? 'rotate-90' : ''}`} />
                  <span className="flex-1 min-w-0">
                    Доступны здесь: {inheritedOpen ? '' : 'ещё '}
                    <span className="text-brand font-medium">{ruCount(inheritedFiles.length, 'набор', 'набора', 'наборов')}</span>
                    {!inheritedOpen && ' с верхних уровней'}
                  </span>
                </button>
                {inheritedOpen && inheritedFiles.map(f => (
                  <FileNavItem key={f.id} icon={<FileText size={15} />} label={f.name}
                    secondary={`${DATA_SET_FORMAT_LABELS[f.format]} · ${sourcesLabel(f.sources.length)}`}
                    active={selected === f.id} recognizing={!!jobForFile(f)} stale={fileStale(f)}
                    level={<LevelChip file={f} />}
                    onClick={() => setSelected(f.id)} />
                ))}
              </>
            )}

            <div className="pt-2">{uploadBtn}</div>
            {uploadError && <p className="text-[11px] text-danger px-1 pt-1">{uploadError}</p>}
          </aside>

          {/* Detail */}
          <div className="flex-1 min-w-0">
            {isAll ? (
              <AllFilesOverview files={files} inheritedCount={inheritedFiles.length} onOpen={setSelected} />
            ) : selectedFile ? (
              <FileDetail file={selectedFile} inherited={selectedInherited} job={jobForFile(selectedFile)} />
            ) : (
              <div className="text-center py-10 text-fg4 text-sm">
                Набор не найден. <button className="text-brand hover:text-brand-hover" onClick={() => setSelected(ALL)}>Ко всем наборам</button>
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
