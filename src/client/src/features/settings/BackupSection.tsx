import { useEffect, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Icon } from '@/shared/ui/Icon';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { Cancel, CheckCircle, ChevronRight, Delete, Download, Info, KeyboardArrowDown, RestartAlt, Upload, Warning } from '@/shared/ui/icons';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { formatBytes } from '@/shared/api/attachments';
import { apiError } from '@/shared/utils/apiError';
import {
  downloadBackupFile, uploadBackupFile,
  useBackupFiles, useBackupJob, useCreateBackup, useDeleteBackupFile, useRestoreFromFile,
} from '@/shared/api/backup';
import type {
  BackupFileInfo, BackupScope, RestoreReport,
} from '@/shared/api/types';
import { formatDateTime } from '@/shared/format/format';
import { ScheduleForm } from './BackupScheduleForm';
import { CollapsibleSection } from './CollapsibleSection';
import { BackupSizeLine } from './BackupSizeLine';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Tooltip } from '@/shared/ui/Tooltip';

/**
 * Резервное копирование: каталог копий на сервере (issue #831).
 *
 * Копия перестала быть загрузкой в браузер и обратно. Причина не в удобстве: предел
 * `BACKUP_MAX_ARCHIVE_MB` — свойство транспорта (nginx → Kestrel → форма), и с ростом библиотеки
 * качества система исправно снимала копии, которые сама же отказывалась принять обратно. Теперь
 * копия ложится в каталог на хосте, восстановление читает её оттуда, и по сети идёт одно имя файла.
 * Загрузка через браузер осталась удобством для небольших файлов — с прежним пределом.
 */
export function BackupSection() {
  return (
    <CollapsibleSection title="Резервное копирование" storageKey="backup" defaultOpen={false}>
      <p className="text-xs text-on-surface-variant">
        <b>Настройка системы</b> — типы документов, шаблоны и их ассеты, справочники, общие данные,
        библиотека Typst, профили распознавания, шаблоны маппинга и рецепты обработки, алиасы
        сверки — входит в копию всегда, вместе с библиотекой документов качества и сканами.
        {' '}<b>Проектная работа</b> — стройки, разделы, комплекты, документы с выпущенными файлами,
        наборы данных с разобранными источниками, сверки и связки с материалами — входит в
        <b> полную</b> копию: ею переезжают на другой сервер и восстанавливаются после сбоя.
        Не входит ни в какую: учётные записи, ключи интеграций и результаты прогонов (сверок,
        проверок) — они пересчитываются.
      </p>
      <BackupFilesPanel />
    </CollapsibleSection>
  );
}

// ─── Каталог копий ────────────────────────────────────────────────────────────

function BackupFilesPanel() {
  const qc = useQueryClient();
  const { data, isPending, isError } = useBackupFiles();
  const job = useBackupJob();
  const create = useCreateBackup();
  const remove = useDeleteBackupFile();
  const restore = useRestoreFromFile();

  const fileInputRef = useRef<HTMLInputElement>(null);
  const [error, setError] = useState('');
  const [uploading, setUploading] = useState(false);
  const [downloading, setDownloading] = useState<string | null>(null);
  // Умолчание — полная копия (issue #833): за копией приходят ради восстановления и переезда, а
  // не ради страховки одной настройки. Кто хочет лёгкую — выбирает её осознанно.
  const [scope, setScope] = useState<BackupScope>('Full');
  const [expanded, setExpanded] = useState<string | null>(null);
  const [toDelete, setToDelete] = useState<BackupFileInfo | null>(null);
  const [toRestore, setToRestore] = useState<BackupFileInfo | null>(null);
  const [restoring, setRestoring] = useState(false);
  const [result, setResult] = useState<RestoreReport | null>(null);

  // Задача закончилась — в каталоге появился файл. Без этого список оставался бы вчерашним до
  // перезагрузки страницы, и человек, дождавшийся конца копирования, не увидел бы копии.
  const wasRunning = useRef(false);
  useEffect(() => {
    if (job) wasRunning.current = true;
    else if (wasRunning.current) {
      wasRunning.current = false;
      qc.invalidateQueries({ queryKey: ['backup', 'files'] });
      qc.invalidateQueries({ queryKey: ['backup', 'size'] });
    }
  }, [job, qc]);

  async function handleCreate() {
    setError('');
    try { await create.mutateAsync(scope); }
    catch (e) { setError(apiError(e, 'Не удалось поставить копирование в очередь.')); }
  }

  async function handleUpload(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    if (!file.name.toLowerCase().endsWith('.zip')) {
      setError('Выберите файл резервной копии (.zip).');
      return;
    }
    setError('');
    setUploading(true);
    try {
      await uploadBackupFile(file);
      qc.invalidateQueries({ queryKey: ['backup', 'files'] });
    } catch (err) {
      setError(apiError(err, 'Не удалось загрузить файл.'));
    } finally {
      setUploading(false);
    }
  }

  /**
   * Скачивание — единственное действие, у которого отказ некому показать: кнопка не открывает
   * диалога. Молчащая кнопка при этом худший исход из возможных — копию удалили из другой вкладки
   * или унесли с диска, а выглядит это как «не нажалось».
   */
  async function handleDownload(file: BackupFileInfo) {
    setError('');
    setDownloading(file.fileName);
    try {
      await downloadBackupFile(file.fileName);
    } catch (e) {
      setError(apiError(e, `Не удалось скачать «${file.fileName}».`));
      qc.invalidateQueries({ queryKey: ['backup', 'files'] });
    } finally {
      setDownloading(null);
    }
  }

  async function handleRestore(file: BackupFileInfo) {
    setRestoring(true);
    try {
      setResult(await restore.mutateAsync(file.fileName));
      setToRestore(null);
    } finally {
      setRestoring(false);
    }
  }

  const files = data?.files ?? [];
  const full = data ? files.length >= data.keepCount : false;
  // Копию может снимать и расписание — своей задачи у пилюли в шапке при этом нет (она показывает
  // только задачи пользователя, а у плановой владельца нет). Без этого признака кнопка оставалась
  // бы включённой, а нажатие возвращало отказ «копия уже снимается» — про то, чего на экране не
  // видно.
  const scheduledRunning = data?.schedule.running === true && !job;
  const busy = !!job || scheduledRunning;

  const scheduled = new Set(data?.scheduledFiles ?? []);

  return (
    <div className="space-y-3">
      {data && <ScheduleForm status={data.schedule} />}

      <div className="flex flex-wrap items-center gap-3 text-sm">
        <span className="text-on-surface-variant text-xs">Что включать:</span>
        {([['Full', 'настройку и проектные данные'], ['Configuration', 'только настройку']] as const)
          .map(([value, label]) => (
            <label key={value} className="flex items-center gap-1.5 text-xs text-on-surface-variant">
              <input type="radio" name="backup-scope" checked={scope === value}
                onChange={() => setScope(value)} disabled={busy} />
              {label}
            </label>
          ))}
      </div>

      <BackupSizeLine scope={scope} />

      <div className="flex flex-wrap items-center gap-2">
        <Button variant="filled" onClick={handleCreate} disabled={busy || create.isPending || full}>
          {busy ? <CircularProgress size={18} /> : <Icon icon={Download} size={18} />}
          {job
            ? (job.progress ? `Копирование: ${job.progress}` : 'Копирование…')
            : scheduledRunning ? 'Идёт плановое копирование…' : 'Создать копию'}
        </Button>
        <Button variant="outlined" onClick={() => fileInputRef.current?.click()} disabled={uploading}>
          {uploading ? <CircularProgress size={18} /> : <Icon icon={Upload} size={18} />}
          {uploading ? 'Загрузка…' : 'Загрузить копию'}
        </Button>
        <input ref={fileInputRef} type="file" accept=".zip" className="hidden" onChange={handleUpload} />
        {data && (
          <span className="text-xs text-on-surface-variant">
            {files.length} из {data.keepCount}
          </span>
        )}
      </div>

      {full && (
        <p className="text-xs text-warning flex items-start gap-1">
          <Icon icon={Warning} size={18} className="shrink-0 mt-px" />
          <span>
            Каталог заполнен: новая копия не создастся, пока не удалить старые. Старые не убираются
            сами — иначе однажды исчезла бы та единственная, которая понадобится.
          </span>
        </p>
      )}

      {error && (
        <p className="text-xs text-error flex items-start gap-1">
          <Icon icon={Cancel} size={18} className="shrink-0 mt-px" /> <span>{error}</span>
        </p>
      )}

      {isPending && <p className="text-xs text-on-surface-variant">Список копий загружается…</p>}
      {isError && <p className="text-xs text-error">Не удалось прочитать каталог копий.</p>}

      {!isPending && !isError && files.length === 0 && (
        <p className="text-xs text-on-surface-variant">Копий пока нет.</p>
      )}

      {files.length > 0 && (
        <ul className="divide-y divide-surface-container border-t border-b border-surface-container">
          {files.map(f => (
            <li key={f.fileName} className="py-1.5">
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => setExpanded(expanded === f.fileName ? null : f.fileName)}
                  className="p-0.5 text-on-surface-variant hover:text-on-surface-variant"
                  aria-label={expanded === f.fileName ? 'Свернуть состав' : 'Показать состав'}
                >
                  {expanded === f.fileName ? <Icon icon={KeyboardArrowDown} size={18} /> : <Icon icon={ChevronRight} size={18} />}
                </button>
                <div className="min-w-0 flex-1">
                  <p className="text-sm text-on-surface truncate">
                    {f.fileName}
                    <span className="ml-2 text-caption text-on-surface-variant font-normal">
                      {f.includesProjectData ? 'полная' : 'настройка'}
                    </span>
                    {scheduled.has(f.fileName) && (
                      <Tooltip content="Снята расписанием — попадает под уборку старых">
                        <span className="ml-2 text-caption text-on-surface-variant font-normal">
                          плановая
                        </span>
                      </Tooltip>
                    )}
                  </p>
                  <p className="text-xs text-on-surface-variant">
                    {formatDateTime(f.createdAt)}
                    {' · '}{formatBytes(f.sizeBytes)}
                    {f.appVersion && <> · v{f.appVersion}</>}
                    {f.blobCount !== null && <> · файлов: {f.blobCount}</>}
                  </p>
                  {f.problem && (
                    <p className="text-xs text-warning flex items-start gap-1 mt-0.5">
                      <Icon icon={Warning} size={18} className="shrink-0 mt-px" /> <span>{f.problem}</span>
                    </p>
                  )}
                  {/* Пропущенное при СНЯТИИ копии — здесь, а не только в журнале: узнать об этом
                      при восстановлении значит узнать после аварии. */}
                  {f.warnings?.map((w, i) => (
                    <p key={i} className="text-xs text-warning flex items-start gap-1 mt-0.5">
                      <Icon icon={Warning} size={18} className="shrink-0 mt-px" /> <span>{w}</span>
                    </p>
                  ))}
                </div>
                <div className="flex items-center gap-1 shrink-0">
                  <IconAction title="Скачать" onClick={() => handleDownload(f)}
                    disabled={downloading !== null}>
                    {downloading === f.fileName
                      ? <CircularProgress size={18} />
                      : <Icon icon={Download} size={18} />}
                  </IconAction>
                  <IconAction title="Восстановить из этой копии" onClick={() => setToRestore(f)}>
                    <Icon icon={RestartAlt} size={18} />
                  </IconAction>
                  <IconAction title="Удалить копию" danger onClick={() => setToDelete(f)}>
                    <Icon icon={Delete} size={18} />
                  </IconAction>
                </div>
              </div>

              {expanded === f.fileName && (
                <div className="pl-7 pr-2 pb-1 pt-1">
                  {f.sections && f.sections.length > 0 ? (
                    <div className="grid grid-cols-2 gap-x-6 gap-y-0.5 text-xs sm:grid-cols-3">
                      {f.sections.map(s => (
                        <div key={s.label} className="flex justify-between gap-2">
                          <span className="text-on-surface-variant truncate">{s.label}</span>
                          <span className="text-on-surface-variant font-mono">{s.count}</span>
                        </div>
                      ))}
                    </div>
                  ) : (
                    <p className="text-xs text-on-surface-variant">Состав неизвестен: в архиве нет паспорта копии.</p>
                  )}
                </div>
              )}
            </li>
          ))}
        </ul>
      )}

      {data && (
        <p className="text-xs text-on-surface-variant">
          Каталог на сервере: <span className="font-mono">{data.directory}</span>. Копию в несколько
          гигабайт забирают и приносят файлами прямо туда — браузер такую не потянет ни на
          скачивании, ни на загрузке.
        </p>
      )}

      <ConfirmDialog
        open={toDelete !== null}
        onOpenChange={o => { if (!o) setToDelete(null); }}
        title="Удалить резервную копию?"
        description={
          <>
            Файл <b>{toDelete?.fileName}</b> ({toDelete && formatBytes(toDelete.sizeBytes)}) будет
            удалён с сервера безвозвратно. Если это единственная копия, восстанавливать систему
            будет не из чего.
          </>
        }
        confirmLabel="Удалить копию"
        onConfirm={async () => { if (toDelete) await remove.mutateAsync(toDelete.fileName); }}
      />

      <ConfirmDialog
        open={toRestore !== null}
        onOpenChange={o => { if (!o && !restoring) setToRestore(null); }}
        title="Восстановить из резервной копии?"
        errorTitle="Восстановление не выполнено"
        confirmLabel="Восстановить"
        requireCheckbox="Понимаю: записи будут перезаписаны данными из копии"
        description={
          <div className="space-y-2">
            <p>
              <b>{toRestore?.fileName}</b>
              {toRestore?.appVersion && <> · снята версией v{toRestore.appVersion}</>}
              {toRestore && <> · {formatBytes(toRestore.sizeBytes)}</>}
              {toRestore && <> · {toRestore.includesProjectData
                ? 'настройка и проектные данные' : 'только настройка'}</>}
            </p>
            <p>
              Записи с совпадающими идентификаторами будут обновлены, отсутствующие — добавлены,
              файлы и изображения восстановлены в хранилище. Проектная работа (стройки, комплекты,
              документы) не затрагивается.
            </p>
            <p>
              Восстановление <b>ничего не удаляет</b>: созданное после снятия копии останется — к
              состоянию на момент копии система не вернётся. Исключение — библиотека Typst: её
              дерево файлов замещается целиком.
            </p>
          </div>
        }
        onConfirm={() => { if (toRestore) return handleRestore(toRestore); }}
      />

      <Modal
        open={result !== null}
        onOpenChange={o => { if (!o) setResult(null); }}
        title={result?.success ? 'Восстановление завершено' : 'Ошибка восстановления'}
        flushBody
        // Итог читают (issue #1237): промах и Esc его не закрывают, выход один — и рисует его окно.
        acknowledge="Закрыть"
      >
        {result && <RestoreResultModal report={result} />}
      </Modal>
    </div>
  );
}

function IconAction({ title, onClick, danger, disabled, children }: {
  title: string;
  onClick: () => void;
  danger?: boolean;
  disabled?: boolean;
  children: React.ReactNode;
}) {
  return (
    <Tooltip content={title}>
      <button
        type="button"
        aria-label={title}
        onClick={onClick}
        disabled={disabled}
        className={`p-1.5 rounded-md disabled-content ${
          danger ? 'text-on-surface-variant hover:text-on-error-container hover:bg-error-container' : 'text-on-surface-variant hover:text-on-surface state-layer'
        }`}
      >
        {children}
      </button>
    </Tooltip>
  );
}

// ─── Итог восстановления ──────────────────────────────────────────────────────

function RestoreResultModal({ report }: { report: RestoreReport }) {
  const total =
    (report.primitiveTypesCreated ?? 0) + (report.primitiveTypesUpdated ?? 0) +
    (report.enumTypesCreated ?? 0) + (report.enumTypesUpdated ?? 0) +
    report.documentTypesCreated + report.documentTypesUpdated +
    report.templatesCreated + report.templatesUpdated +
    (report.templateAssetsCreated ?? 0) + (report.templateAssetsUpdated ?? 0) +
    (report.recognitionProfilesCreated ?? 0) + (report.recognitionProfilesUpdated ?? 0) +
    report.catalogEntitiesCreated + report.catalogEntitiesUpdated +
    report.commonDataEntriesCreated + report.commonDataEntriesUpdated +
    (report.dataSetBindingTemplatesCreated ?? 0) + (report.dataSetBindingTemplatesUpdated ?? 0) +
    (report.reconciliationAliasesCreated ?? 0) + (report.reconciliationAliasesUpdated ?? 0) +
    (report.dataSetProcessingTemplatesCreated ?? 0) + (report.dataSetProcessingTemplatesUpdated ?? 0) +
    (report.qualityDocumentsCreated ?? 0) + (report.qualityDocumentsUpdated ?? 0);

  return (
    <div className="flex flex-col min-h-0 flex-1">
      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4 space-y-4">
        <div className={`flex items-center gap-3 rounded-lg p-3 ${
          report.success ? 'bg-success-container text-on-success-container' : 'bg-error-container text-on-error-container'
        }`}>
          {report.success
            ? <Icon icon={CheckCircle} size={20} className="text-success shrink-0" />
            : <Icon icon={Cancel} size={20} className="text-error shrink-0" />}
          <span className="text-sm font-medium">
            {report.success
              ? `Восстановление выполнено успешно (${total} записей)`
              : 'Восстановление завершилось с ошибкой'}
          </span>
        </div>

        {report.conversionNotice && (
          <div className="flex gap-2 rounded-lg border border-primary-container bg-primary-container p-3 text-sm text-on-primary-container">
            <Icon icon={Info} size={18} className="shrink-0 mt-0.5" />
            <span>{report.conversionNotice}</span>
          </div>
        )}

        {report.success && (
          <div className="rounded-lg border border-outline-variant overflow-hidden">
            <DataGrid framed={false} columns={STAT_COLUMNS} rowKey={r => r.label} rows={[
              { label: 'Типы полей', created: report.primitiveTypesCreated ?? 0, updated: report.primitiveTypesUpdated ?? 0 },
              { label: 'Перечисления', created: report.enumTypesCreated ?? 0, updated: report.enumTypesUpdated ?? 0 },
              { label: 'Типы документов', created: report.documentTypesCreated, updated: report.documentTypesUpdated },
              { label: 'Шаблоны', created: report.templatesCreated, updated: report.templatesUpdated },
              { label: 'Ассеты шаблонов', created: report.templateAssetsCreated ?? 0, updated: report.templateAssetsUpdated ?? 0 },
              { label: 'Профили распознавания', created: report.recognitionProfilesCreated ?? 0, updated: report.recognitionProfilesUpdated ?? 0 },
              { label: 'Записи каталога', created: report.catalogEntitiesCreated, updated: report.catalogEntitiesUpdated },
              { label: 'Общие данные', created: report.commonDataEntriesCreated, updated: report.commonDataEntriesUpdated },
              { label: 'Шаблоны маппинга', created: report.dataSetBindingTemplatesCreated ?? 0, updated: report.dataSetBindingTemplatesUpdated ?? 0 },
              { label: 'Алиасы сверки', created: report.reconciliationAliasesCreated ?? 0, updated: report.reconciliationAliasesUpdated ?? 0 },
              { label: 'Шаблоны обработки', created: report.dataSetProcessingTemplatesCreated ?? 0, updated: report.dataSetProcessingTemplatesUpdated ?? 0 },
              { label: 'Документы качества', created: report.qualityDocumentsCreated ?? 0, updated: report.qualityDocumentsUpdated ?? 0 },
              // Проектные секции приходят списком (issue #833): их много, и заводить пару полей
              // на каждую значило бы править тип, таблицу и сервер ради каждой новой.
              ...(report.projectSections ?? []),
            ]} />
            {report.typstUserLibRestored && (
              <p className="px-4 py-2 text-xs text-on-surface-variant">Библиотека Typst (userlib) восстановлена.</p>
            )}
            {/*
              Семантика восстановления нигде не была объявлена, а она не очевидна: копия ДОБАВЛЯЕТ и
              ОБНОВЛЯЕТ, но не удаляет. Тип документа, удалённый уже после снятия копии, к удалённому
              состоянию не вернётся — он останется. Поведение выбрано намеренно (безопаснее не
              удалять), но оператор вправе знать о нём до того, как удивится.
            */}
            <p className={`px-4 py-2 text-xs text-on-surface-variant ${report.typstUserLibRestored ? 'border-t border-outline-variant' : ''}`}>
              Восстановление добавляет и обновляет записи, но <b>не удаляет</b> то, чего в копии нет:
              созданное после снятия копии останется на месте. Кроме библиотеки Typst — её дерево
              файлов замещается целиком.
            </p>
          </div>
        )}

        {report.warnings.length > 0 && (
          <div className="rounded-lg border border-outline-variant bg-warning-container p-3 space-y-1">
            <p className="text-xs font-semibold text-warning uppercase tracking-wide mb-2">
              Предупреждения ({report.warnings.length})
            </p>
            {report.warnings.map((w, i) => (
              <div key={i} className="flex gap-2 text-sm text-warning">
                <span className="shrink-0">·</span>
                <span>{w}</span>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

interface Stat { label: string; created: number; updated: number }

/** Число в отчёте: прочерк вместо нуля — иначе столбец нулей прятал бы немногие ненулевые. */
const statCell = (n: number, sign: string, tone: string) => n > 0
  ? <span className={`font-mono ${tone}`}>{sign}{n}</span>
  : <span className="font-mono text-on-surface-variant">—</span>;

const STAT_COLUMNS: DataGridColumn<Stat>[] = [
  { key: 'label', label: 'Категория' },
  { key: 'created', label: 'Добавлено', align: 'right', width: 'w-28', cell: r => statCell(r.created, '+', 'text-success') },
  { key: 'updated', label: 'Обновлено', align: 'right', width: 'w-28', cell: r => statCell(r.updated, '~', 'text-primary') },
];
