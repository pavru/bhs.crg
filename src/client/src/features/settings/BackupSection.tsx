import { useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  AlertTriangle, CheckCircle, ChevronDown, ChevronRight, Download, Info, Loader2, RotateCcw,
  Trash2, Upload, XCircle,
} from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { formatBytes } from '@/shared/api/attachments';
import { apiError } from '@/shared/utils/apiError';
import {
  downloadBackupFile, fetchBackupSize, uploadBackupFile,
  useBackupFiles, useBackupJob, useCreateBackup, useDeleteBackupFile, useRestoreFromFile,
  useSaveBackupSchedule,
} from '@/shared/api/backup';
import type {
  BackupFileInfo, BackupScheduleSettings, BackupScheduleStatus, BackupScope, RestoreReport,
} from '@/shared/api/types';
import { formatDate, useLocale } from '@/shared/hooks/useLocale';
import { CollapsibleSection } from './CollapsibleSection';

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
      <p className="text-xs text-fg3">
        <b>Настройка системы</b> — типы документов, шаблоны и их ассеты, справочники, общие данные,
        библиотека Typst, профили распознавания, шаблоны маппинга и рецепты обработки, алиасы
        сверки — входит в копию всегда, вместе с библиотекой документов качества и сканами.
        {' '}<b>Проектная работа</b> — стройки, разделы, комплекты, документы с выпущенными файлами,
        наборы данных с разобранными источниками, сверки и связки с материалами — входит в
        <b> полную</b> копию: ею переезжают на другой сервер и восстанавливаются после сбоя.
        Не входит ни в какую: учётные записи, ключи интеграций и результаты прогонов (сверок,
        проверок) — они пересчитываются.
      </p>
      <BackupSizeLine />
      <BackupFilesPanel />
    </CollapsibleSection>
  );
}

// ─── Вес копии против предела загрузки через браузер ──────────────────────────

/**
 * Сколько весит копия и на каком пороге её откажется принять ЗАГРУЗКА через браузер (issue #711).
 *
 * С появлением каталога на сервере (issue #831) это перестало быть приговором: копия сверх предела
 * снимается и восстанавливается по-прежнему — просто не через браузер. Поэтому и текст здесь
 * теперь называет путь, а не только беду.
 *
 * Запрос уходит только когда раздел раскрыт: CollapsibleSection не монтирует содержимое свёрнутого,
 * а оценка стоит построения манифеста и запроса размера на каждый файл.
 */
function BackupSizeLine() {
  const { data, isPending, isError } = useQuery({
    queryKey: ['backup', 'size'],
    queryFn: fetchBackupSize,
    staleTime: 5 * 60 * 1000,
    refetchOnWindowFocus: false,
    retry: false,
  });

  if (isPending) return <p className="text-xs text-fg4">Размер копии оценивается…</p>;
  // Молча ничего не показывать нельзя, но и мешать копированию нечем: кнопка работает и без оценки.
  if (isError || !data) return <p className="text-xs text-fg4">Размер копии оценить не удалось.</p>;

  return (
    <div className="space-y-1">
      <p className="text-xs text-fg3">
        Размер копии ≈ настройка <b>{formatBytes(data.configuration.totalBytes)}</b>
        {' · '}полная <b>{formatBytes(data.full.totalBytes)}</b>
        {data.full.blobCount > 0 && <> · файлов: {data.full.blobCount}</>}
      </p>
      {data.full.missingBlobCount > 0 && (
        <p className="text-xs text-warning flex items-start gap-1">
          <AlertTriangle size={13} className="shrink-0 mt-px" />
          <span>
            Файлов нет в хранилище: {data.full.missingBlobCount}. В копию они не попадут — ссылки на
            них останутся битыми и после восстановления.
          </span>
        </p>
      )}
    </div>
  );
}

// ─── Каталог копий ────────────────────────────────────────────────────────────

/**
 * Расписание планового копирования (issue #832).
 *
 * Включено по умолчанию, и это главное решение здесь: копия, которую забыли настроить, — самый
 * частый способ потерять данные, поэтому установка, где администратор не сделал ничего, всё равно
 * защищена. Выключатель на месте — но выключать приходится осознанно, а не включать.
 *
 * «Последняя плановая» показывается не потому, что данные есть, а потому, что без неё галка
 * «включено» неопровержима: она выглядит одинаково и когда копии снимаются, и когда они полгода
 * падают на нехватке места.
 */
function ScheduleForm({ status }: { status: BackupScheduleStatus }) {
  const [locale] = useLocale();
  const save = useSaveBackupSchedule();
  const [form, setForm] = useState<BackupScheduleSettings>({
    enabled: status.enabled, timeOfDay: status.timeOfDay, keepCount: status.keepCount,
  });
  const [error, setError] = useState('');
  const [saved, setSaved] = useState(false);

  // Ответ сервера — источник истины: расписание могли поменять из другой вкладки, и форма,
  // оставшаяся на своём, показывала бы не то, чем система живёт.
  useEffect(() => {
    setForm({ enabled: status.enabled, timeOfDay: status.timeOfDay, keepCount: status.keepCount });
  }, [status.enabled, status.timeOfDay, status.keepCount]);

  const dirty = form.enabled !== status.enabled
    || form.timeOfDay !== status.timeOfDay
    || form.keepCount !== status.keepCount;

  async function submit() {
    setError('');
    try {
      await save.mutateAsync(form);
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch (e) {
      setError(apiError(e, 'Не удалось сохранить расписание.'));
    }
  }

  return (
    <div className="rounded-md border border-muted p-3 space-y-2">
      <label className="flex items-start gap-2 text-sm text-fg2">
        <input type="checkbox" className="mt-0.5" checked={form.enabled}
          onChange={e => setForm({ ...form, enabled: e.target.checked })} />
        <span>
          Снимать копию автоматически
          <span className="block text-xs text-fg4">
            Раз в сутки. Пропущенный запуск (сервер был выключен) выполняется при следующем старте;
            за неделю простоя снимается одна копия, а не семь.
          </span>
        </span>
      </label>

      <div className="flex flex-wrap items-end gap-3">
        <label className="text-xs text-fg3">
          <span className="block mb-1">Время (часы сервера)</span>
          <input type="time" value={form.timeOfDay} disabled={!form.enabled}
            onChange={e => setForm({ ...form, timeOfDay: e.target.value })}
            className="h-8 px-2 rounded-md border border-stroke bg-surface text-sm text-fg1 disabled:opacity-50" />
        </label>
        <label className="text-xs text-fg3">
          <span className="block mb-1">Хранить плановых</span>
          <input type="number" min={1} max={1000} value={form.keepCount} disabled={!form.enabled}
            onChange={e => setForm({ ...form, keepCount: Number(e.target.value) })}
            className="h-8 w-20 px-2 rounded-md border border-stroke bg-surface text-sm text-fg1 disabled:opacity-50" />
        </label>
        <Button variant="outlined" onClick={submit} disabled={!dirty || save.isPending}>
          {save.isPending ? 'Сохранение…' : 'Сохранить'}
        </Button>
        {saved && <span className="text-xs text-success">Сохранено</span>}
      </div>

      <p className="text-xs text-fg4">
        Уборка старых копий касается <b>только плановых</b>: снятые вручную и принесённые с другого
        сервера остаются, пока их не удалят руками.
      </p>

      {error && (
        <p className="text-xs text-danger flex items-start gap-1">
          <XCircle size={13} className="shrink-0 mt-px" /> <span>{error}</span>
        </p>
      )}

      {status.lastError ? (
        <p className="text-xs text-danger flex items-start gap-1">
          <AlertTriangle size={13} className="shrink-0 mt-px" />
          <span>
            Последняя плановая копия не удалась
            {status.lastErrorAt && <> ({formatDate(status.lastErrorAt, locale, {
              day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit',
            })})</>}: {status.lastError}
          </span>
        </p>
      ) : status.lastSuccessAt ? (
        <p className="text-xs text-fg4">
          Последняя плановая копия: {formatDate(status.lastSuccessAt, locale, {
            day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit',
          })}
          {status.lastFileName && <> · {status.lastFileName}</>}
        </p>
      ) : (
        <p className="text-xs text-fg4">Плановых копий ещё не было.</p>
      )}
    </div>
  );
}

function BackupFilesPanel() {
  const [locale] = useLocale();
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
        <span className="text-fg3 text-xs">Что включать:</span>
        {([['Full', 'настройку и проектные данные'], ['Configuration', 'только настройку']] as const)
          .map(([value, label]) => (
            <label key={value} className="flex items-center gap-1.5 text-xs text-fg2">
              <input type="radio" name="backup-scope" checked={scope === value}
                onChange={() => setScope(value)} disabled={busy} />
              {label}
            </label>
          ))}
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <Button variant="filled" onClick={handleCreate} disabled={busy || create.isPending || full}>
          {busy ? <Loader2 size={14} className="animate-spin" /> : <Download size={14} />}
          {job
            ? (job.progress ? `Копирование: ${job.progress}` : 'Копирование…')
            : scheduledRunning ? 'Идёт плановое копирование…' : 'Создать копию'}
        </Button>
        <Button variant="outlined" onClick={() => fileInputRef.current?.click()} disabled={uploading}>
          {uploading ? <Loader2 size={14} className="animate-spin" /> : <Upload size={14} />}
          {uploading ? 'Загрузка…' : 'Загрузить копию'}
        </Button>
        <input ref={fileInputRef} type="file" accept=".zip" className="hidden" onChange={handleUpload} />
        {data && (
          <span className="text-xs text-fg4">
            {files.length} из {data.keepCount}
          </span>
        )}
      </div>

      {full && (
        <p className="text-xs text-warning flex items-start gap-1">
          <AlertTriangle size={13} className="shrink-0 mt-px" />
          <span>
            Каталог заполнен: новая копия не создастся, пока не удалить старые. Старые не убираются
            сами — иначе однажды исчезла бы та единственная, которая понадобится.
          </span>
        </p>
      )}

      {error && (
        <p className="text-xs text-danger flex items-start gap-1">
          <XCircle size={13} className="shrink-0 mt-px" /> <span>{error}</span>
        </p>
      )}

      {isPending && <p className="text-xs text-fg4">Список копий загружается…</p>}
      {isError && <p className="text-xs text-danger">Не удалось прочитать каталог копий.</p>}

      {!isPending && !isError && files.length === 0 && (
        <p className="text-xs text-fg4">Копий пока нет.</p>
      )}

      {files.length > 0 && (
        <ul className="divide-y divide-muted border-t border-b border-muted">
          {files.map(f => (
            <li key={f.fileName} className="py-1.5">
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => setExpanded(expanded === f.fileName ? null : f.fileName)}
                  className="p-0.5 text-fg4 hover:text-fg2"
                  aria-label={expanded === f.fileName ? 'Свернуть состав' : 'Показать состав'}
                >
                  {expanded === f.fileName ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
                </button>
                <div className="min-w-0 flex-1">
                  <p className="text-sm text-fg1 truncate">
                    {f.fileName}
                    <span className="ml-2 text-[11px] text-fg4 font-normal">
                      {f.includesProjectData ? 'полная' : 'настройка'}
                    </span>
                    {scheduled.has(f.fileName) && (
                      <span className="ml-2 text-[11px] text-fg4 font-normal" title="Снята расписанием — попадает под уборку старых">
                        плановая
                      </span>
                    )}
                  </p>
                  <p className="text-xs text-fg3">
                    {formatDate(f.createdAt, locale, {
                      day: '2-digit', month: '2-digit', year: 'numeric',
                      hour: '2-digit', minute: '2-digit',
                    })}
                    {' · '}{formatBytes(f.sizeBytes)}
                    {f.appVersion && <> · v{f.appVersion}</>}
                    {f.blobCount !== null && <> · файлов: {f.blobCount}</>}
                  </p>
                  {f.problem && (
                    <p className="text-xs text-warning flex items-start gap-1 mt-0.5">
                      <AlertTriangle size={12} className="shrink-0 mt-px" /> <span>{f.problem}</span>
                    </p>
                  )}
                  {/* Пропущенное при СНЯТИИ копии — здесь, а не только в журнале: узнать об этом
                      при восстановлении значит узнать после аварии. */}
                  {f.warnings?.map((w, i) => (
                    <p key={i} className="text-xs text-warning flex items-start gap-1 mt-0.5">
                      <AlertTriangle size={12} className="shrink-0 mt-px" /> <span>{w}</span>
                    </p>
                  ))}
                </div>
                <div className="flex items-center gap-1 shrink-0">
                  <IconAction title="Скачать" onClick={() => handleDownload(f)}
                    disabled={downloading !== null}>
                    {downloading === f.fileName
                      ? <Loader2 size={14} className="animate-spin" />
                      : <Download size={14} />}
                  </IconAction>
                  <IconAction title="Восстановить из этой копии" onClick={() => setToRestore(f)}>
                    <RotateCcw size={14} />
                  </IconAction>
                  <IconAction title="Удалить копию" danger onClick={() => setToDelete(f)}>
                    <Trash2 size={14} />
                  </IconAction>
                </div>
              </div>

              {expanded === f.fileName && (
                <div className="pl-7 pr-2 pb-1 pt-1">
                  {f.sections && f.sections.length > 0 ? (
                    <div className="grid grid-cols-2 gap-x-6 gap-y-0.5 text-xs sm:grid-cols-3">
                      {f.sections.map(s => (
                        <div key={s.label} className="flex justify-between gap-2">
                          <span className="text-fg3 truncate">{s.label}</span>
                          <span className="text-fg2 font-mono">{s.count}</span>
                        </div>
                      ))}
                    </div>
                  ) : (
                    <p className="text-xs text-fg4">Состав неизвестен: в архиве нет паспорта копии.</p>
                  )}
                </div>
              )}
            </li>
          ))}
        </ul>
      )}

      {data && (
        <p className="text-xs text-fg4">
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
      >
        {result && <RestoreResultModal report={result} onClose={() => setResult(null)} />}
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
    <button
      type="button"
      title={title}
      aria-label={title}
      onClick={onClick}
      disabled={disabled}
      className={`p-1.5 rounded-md transition-colors disabled:opacity-50 ${
        danger ? 'text-fg3 hover:text-danger hover:bg-danger-subtle' : 'text-fg3 hover:text-fg1 hover:bg-base'
      }`}
    >
      {children}
    </button>
  );
}

// ─── Итог восстановления ──────────────────────────────────────────────────────

function RestoreResultModal({ report, onClose }: { report: RestoreReport; onClose: () => void }) {
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
          report.success ? 'bg-success-subtle text-success' : 'bg-danger-subtle text-danger'
        }`}>
          {report.success
            ? <CheckCircle size={20} className="text-success shrink-0" />
            : <XCircle size={20} className="text-danger shrink-0" />}
          <span className="text-sm font-medium">
            {report.success
              ? `Восстановление выполнено успешно (${total} записей)`
              : 'Восстановление завершилось с ошибкой'}
          </span>
        </div>

        {report.conversionNotice && (
          <div className="flex gap-2 rounded-lg border border-brand-subtle bg-brand-subtle p-3 text-sm text-brand-pressed">
            <Info size={16} className="shrink-0 mt-0.5" />
            <span>{report.conversionNotice}</span>
          </div>
        )}

        {report.success && (
          <div className="rounded-lg border border-stroke overflow-hidden">
            <table className="w-full text-sm">
              <thead>
                <tr className="bg-base border-b border-stroke">
                  <th className="px-4 py-2 text-left font-medium text-fg2">Категория</th>
                  <th className="px-3 py-2 text-center font-medium text-success">Добавлено</th>
                  <th className="px-3 py-2 text-center font-medium text-brand-hover">Обновлено</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-muted">
                <StatRow label="Типы полей"
                  created={report.primitiveTypesCreated ?? 0} updated={report.primitiveTypesUpdated ?? 0} />
                <StatRow label="Перечисления"
                  created={report.enumTypesCreated ?? 0} updated={report.enumTypesUpdated ?? 0} />
                <StatRow label="Типы документов"
                  created={report.documentTypesCreated} updated={report.documentTypesUpdated} />
                <StatRow label="Шаблоны"
                  created={report.templatesCreated} updated={report.templatesUpdated} />
                <StatRow label="Ассеты шаблонов"
                  created={report.templateAssetsCreated ?? 0} updated={report.templateAssetsUpdated ?? 0} />
                <StatRow label="Профили распознавания"
                  created={report.recognitionProfilesCreated ?? 0} updated={report.recognitionProfilesUpdated ?? 0} />
                <StatRow label="Записи каталога"
                  created={report.catalogEntitiesCreated} updated={report.catalogEntitiesUpdated} />
                <StatRow label="Общие данные"
                  created={report.commonDataEntriesCreated} updated={report.commonDataEntriesUpdated} />
                <StatRow label="Шаблоны маппинга"
                  created={report.dataSetBindingTemplatesCreated ?? 0} updated={report.dataSetBindingTemplatesUpdated ?? 0} />
                <StatRow label="Алиасы сверки"
                  created={report.reconciliationAliasesCreated ?? 0} updated={report.reconciliationAliasesUpdated ?? 0} />
                <StatRow label="Шаблоны обработки"
                  created={report.dataSetProcessingTemplatesCreated ?? 0} updated={report.dataSetProcessingTemplatesUpdated ?? 0} />
                <StatRow label="Документы качества"
                  created={report.qualityDocumentsCreated ?? 0} updated={report.qualityDocumentsUpdated ?? 0} />
                {/* Проектные секции приходят списком (issue #833): их много, и заводить пару полей
                    на каждую значило бы править тип, таблицу и сервер ради каждой новой. */}
                {report.projectSections?.map(sec => (
                  <StatRow key={sec.label} label={sec.label} created={sec.created} updated={sec.updated} />
                ))}
              </tbody>
            </table>
            {report.typstUserLibRestored && (
              <p className="px-4 py-2 text-xs text-fg3 border-t border-stroke">Библиотека Typst (userlib) восстановлена.</p>
            )}
            {/*
              Семантика восстановления нигде не была объявлена, а она не очевидна: копия ДОБАВЛЯЕТ и
              ОБНОВЛЯЕТ, но не удаляет. Тип документа, удалённый уже после снятия копии, к удалённому
              состоянию не вернётся — он останется. Поведение выбрано намеренно (безопаснее не
              удалять), но оператор вправе знать о нём до того, как удивится.
            */}
            <p className="px-4 py-2 text-xs text-fg3 border-t border-stroke">
              Восстановление добавляет и обновляет записи, но <b>не удаляет</b> то, чего в копии нет:
              созданное после снятия копии останется на месте. Кроме библиотеки Typst — её дерево
              файлов замещается целиком.
            </p>
          </div>
        )}

        {report.warnings.length > 0 && (
          <div className="rounded-lg border border-warning-border bg-warning-subtle p-3 space-y-1">
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
      <div className="shrink-0 px-6 py-3 border-t border-stroke flex justify-end">
        <Button variant="filled" onClick={onClose}>Закрыть</Button>
      </div>
    </div>
  );
}

function StatRow({ label, created, updated }: { label: string; created: number; updated: number }) {
  return (
    <tr className="text-fg2">
      <td className="px-4 py-2">{label}</td>
      <td className="px-3 py-2 text-center font-mono text-success">
        {created > 0 ? `+${created}` : <span className="text-stroke-strong">—</span>}
      </td>
      <td className="px-3 py-2 text-center font-mono text-brand-hover">
        {updated > 0 ? `~${updated}` : <span className="text-stroke-strong">—</span>}
      </td>
    </tr>
  );
}
