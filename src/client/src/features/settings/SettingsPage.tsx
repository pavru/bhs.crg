import { useRef, useState } from 'react';
import { Download, Upload, CheckCircle, XCircle, AlertTriangle, Info } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { downloadBackup, restoreBackup } from '@/shared/api/backup';
import type { RestoreReport } from '@/shared/api/types';
import {
  useLocale, LOCALE_OPTIONS, SYSTEM_LOCALE, resolveLocale, formatDate, formatNumber,
} from '@/shared/hooks/useLocale';
import { IntegrationSettingsSection } from './IntegrationSettingsSection';
import { CollapsibleSection } from './CollapsibleSection';

// ─── Settings hook (re-exported for use by other pages) ───────────────────────

export const MAX_VERSIONS_KEY = 'crg.maxTemplateVersions';
export const DEFAULT_MAX_VERSIONS = 5;

export function useMaxTemplateVersions(): [number, (v: number) => void] {
  const [value, setValue] = useState(() => {
    const stored = localStorage.getItem(MAX_VERSIONS_KEY);
    const parsed = stored ? Number(stored) : NaN;
    return Number.isFinite(parsed) && parsed >= 2 ? parsed : DEFAULT_MAX_VERSIONS;
  });
  function set(v: number) {
    localStorage.setItem(MAX_VERSIONS_KEY, String(v));
    setValue(v);
  }
  return [value, set];
}

// ─── Restore confirmation modal ───────────────────────────────────────────────

function RestoreConfirmModal({ file, onConfirm, onCancel }: {
  file: File;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const sizeMb = (file.size / 1024 / 1024).toFixed(2);
  return (
    <div className="flex flex-col min-h-0 flex-1">
      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4 space-y-4">
      <div className="rounded-lg border border-stroke bg-base p-4 text-sm space-y-1">
        <div className="flex gap-2">
          <span className="text-fg3 w-32 shrink-0">Файл:</span>
          <span className="text-fg1 font-medium truncate">{file.name}</span>
        </div>
        <div className="flex gap-2">
          <span className="text-fg3 w-32 shrink-0">Размер:</span>
          <span className="text-fg1">{sizeMb} МБ</span>
        </div>
      </div>
      <div className="rounded-lg border border-warning-border bg-warning-subtle p-3 flex gap-2 text-sm text-warning">
        <AlertTriangle size={16} className="shrink-0 mt-0.5" />
        <span>
          Существующие записи с совпадающими ID будут обновлены. Новые записи из резервной
          копии будут добавлены. Прикреплённые файлы и изображения будут восстановлены в хранилище.
          Данные операционной работы (стройки, комплекты, документы) не затрагиваются.
        </span>
      </div>
      </div>
      <div className="shrink-0 px-6 py-3 border-t border-stroke flex justify-end gap-3">
        <button type="button" onClick={onCancel}
          className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md transition-colors">
          Отмена
        </button>
        <button type="button" onClick={onConfirm}
          className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md transition-colors">
          Восстановить
        </button>
      </div>
    </div>
  );
}

// ─── Restore result modal ──────────────────────────────────────────────────────

function RestoreResultModal({
  report,
  onClose,
}: {
  report: RestoreReport;
  onClose: () => void;
}) {
  const total =
    (report.primitiveTypesCreated ?? 0) + (report.primitiveTypesUpdated ?? 0) +
    report.documentTypesCreated + report.documentTypesUpdated +
    report.templatesCreated + report.templatesUpdated +
    report.catalogEntitiesCreated + report.catalogEntitiesUpdated +
    report.commonDataEntriesCreated + report.commonDataEntriesUpdated;

  return (
    <div className="flex flex-col min-h-0 flex-1">
      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4 space-y-4">
      {/* Status banner */}
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

      {/* Conversion notice */}
      {report.conversionNotice && (
        <div className="flex gap-2 rounded-lg border border-brand-subtle bg-brand-subtle p-3 text-sm text-brand-pressed">
          <Info size={16} className="shrink-0 mt-0.5" />
          <span>{report.conversionNotice}</span>
        </div>
      )}

      {/* Stats table */}
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
              <StatRow label="Типы документов"
                created={report.documentTypesCreated} updated={report.documentTypesUpdated} />
              <StatRow label="Шаблоны"
                created={report.templatesCreated} updated={report.templatesUpdated} />
              <StatRow label="Записи каталога"
                created={report.catalogEntitiesCreated} updated={report.catalogEntitiesUpdated} />
              <StatRow label="Общие данные"
                created={report.commonDataEntriesCreated} updated={report.commonDataEntriesUpdated} />
            </tbody>
          </table>
        </div>
      )}

      {/* Warnings */}
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
        <button type="button" onClick={onClose}
          className="px-4 py-2 text-sm bg-fg1 hover:bg-fg2 text-white rounded-md transition-colors">
          Закрыть
        </button>
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

// ─── Locale settings section ───────────────────────────────────────────────────

const PREVIEW_DATE = new Date(2025, 11, 31, 14, 5, 0); // 31 дек 2025 14:05
const PREVIEW_NUMBER = 1234567.89;

function LocaleSection() {
  const [locale, setLocale] = useLocale();
  const [localeSaved, setLocaleSaved] = useState(false);

  const resolved = resolveLocale(locale);

  function handleSelect(value: string) {
    setLocale(value);
    setLocaleSaved(true);
    setTimeout(() => setLocaleSaved(false), 2000);
  }

  return (
    <CollapsibleSection title="Региональные настройки" storageKey="locale" defaultOpen={false}>
      <p className="text-xs text-fg3">
        Определяет формат дат и чисел в интерфейсе. Сохраняется в браузере.
      </p>

      <div className="space-y-1">
        {LOCALE_OPTIONS.map(opt => {
          const isSelected = locale === opt.value;
          return (
            <button
              key={opt.value}
              type="button"
              onClick={() => handleSelect(opt.value)}
              className={`w-full flex items-center gap-3 px-3 py-2 rounded-md text-sm text-left transition-colors ${
                isSelected
                  ? 'bg-brand-subtle border border-brand-subtle text-brand-pressed'
                  : 'border border-transparent text-fg2 hover:bg-base'
              }`}
            >
              <span className={`w-3.5 h-3.5 rounded-full border-2 shrink-0 flex items-center justify-center ${
                isSelected ? 'border-brand' : 'border-stroke-strong'
              }`}>
                {isSelected && <span className="w-2 h-2 rounded-full bg-brand block" />}
              </span>
              <span className="flex-1">{opt.label}</span>
              {opt.value === SYSTEM_LOCALE && (
                <span className="text-xs text-fg4 font-mono">{navigator.language}</span>
              )}
            </button>
          );
        })}
      </div>

      {/* Preview */}
      <div className="rounded-lg bg-base border border-stroke p-3 space-y-1.5">
        <p className="text-xs font-medium text-fg3 mb-2">Предпросмотр ({resolved})</p>
        <div className="flex gap-3 text-sm">
          <span className="text-fg3 w-20 shrink-0">Дата:</span>
          <span className="text-fg1 font-mono">
            {formatDate(PREVIEW_DATE, locale)}
          </span>
        </div>
        <div className="flex gap-3 text-sm">
          <span className="text-fg3 w-20 shrink-0">Дата и время:</span>
          <span className="text-fg1 font-mono">
            {formatDate(PREVIEW_DATE, locale, {
              day: '2-digit', month: '2-digit', year: 'numeric',
              hour: '2-digit', minute: '2-digit',
            })}
          </span>
        </div>
        <div className="flex gap-3 text-sm">
          <span className="text-fg3 w-20 shrink-0">Число:</span>
          <span className="text-fg1 font-mono">
            {formatNumber(PREVIEW_NUMBER, locale)}
          </span>
        </div>
        <div className="flex gap-3 text-sm">
          <span className="text-fg3 w-20 shrink-0">Валюта:</span>
          <span className="text-fg1 font-mono">
            {formatNumber(PREVIEW_NUMBER, locale, { style: 'currency', currency: 'RUB' })}
          </span>
        </div>
      </div>

      {localeSaved && (
        <p className="text-sm text-success">Сохранено</p>
      )}
    </CollapsibleSection>
  );
}

// ─── Main settings page ────────────────────────────────────────────────────────

export function SettingsPage() {
  // Template version limit setting
  const [maxVersions, setMaxVersions] = useMaxTemplateVersions();
  const [input, setInput] = useState(String(maxVersions));
  const [saved, setSaved] = useState(false);

  // Backup state
  const [backupLoading, setBackupLoading] = useState(false);
  const [backupError, setBackupError] = useState('');

  // Restore state
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [pendingFile, setPendingFile] = useState<File | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [restoreLoading, setRestoreLoading] = useState(false);
  const [restoreResult, setRestoreResult] = useState<RestoreReport | null>(null);
  const [restoreError, setRestoreError] = useState('');

  function handleSave(e: React.FormEvent) {
    e.preventDefault();
    const v = Number(input);
    if (!Number.isFinite(v) || v < 2) return;
    setMaxVersions(v);
    setSaved(true);
    setTimeout(() => setSaved(false), 2000);
  }

  async function handleBackup() {
    setBackupLoading(true);
    setBackupError('');
    try {
      await downloadBackup();
    } catch {
      setBackupError('Не удалось создать резервную копию. Проверьте соединение с сервером.');
    } finally {
      setBackupLoading(false);
    }
  }

  async function handleFileSelected(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (!e.target) return;
    e.target.value = '';
    if (!file) return;
    if (!file.name.endsWith('.zip')) {
      setRestoreError('Выберите файл резервной копии (.zip).');
      return;
    }
    setRestoreError('');
    setPendingFile(file);
    setConfirmOpen(true);
  }

  async function handleConfirmRestore() {
    if (!pendingFile) return;
    setConfirmOpen(false);
    setRestoreLoading(true);
    setRestoreError('');
    try {
      const report = await restoreBackup(pendingFile);
      setRestoreResult(report);
    } catch (err) {
      setRestoreError(err instanceof Error ? err.message : 'Не удалось выполнить восстановление.');
    } finally {
      setRestoreLoading(false);
      setPendingFile(null);
    }
  }

  return (
    <div className="px-6 py-4 max-w-2xl space-y-5">
      <h1 className="text-xl font-semibold text-fg1">Настройки</h1>

      {/* ── Template versioning ────────────────────────────────────────────── */}
      <form onSubmit={handleSave}>
        <CollapsibleSection title="Шаблоны" storageKey="templates">
          <div>
            <label className="block text-sm font-medium text-fg2 mb-1">
              Максимум версий шаблона
            </label>
            <p className="text-xs text-fg3 mb-2">
              При превышении система предложит удалить старые версии. Минимум — 2.
            </p>
            <input
              type="number" min={2} max={100} value={input}
              onChange={e => { setInput(e.target.value); setSaved(false); }}
              className="w-28 border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface"
            />
          </div>
          <div className="flex items-center gap-3">
            <button type="submit"
              className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md transition-colors">
              Сохранить
            </button>
            {saved && <span className="text-sm text-success">Сохранено</span>}
          </div>
        </CollapsibleSection>
      </form>

      {/* ── Locale / regional settings ─────────────────────────────────────── */}
      <LocaleSection />

      {/* ── Поиск и распознавание (интеграции) ─────────────────────────────── */}
      <IntegrationSettingsSection />

      {/* ── Backup & Restore ───────────────────────────────────────────────── */}
      <CollapsibleSection title="Резервное копирование" storageKey="backup" defaultOpen={false}>
        <p className="text-xs text-fg3">
          Резервная копия включает: типы документов, шаблоны, каталог сущностей и общие данные.
          Включает типы документов, шаблоны, общие данные, прикреплённые файлы и изображения. Стройки, комплекты и документы не включаются.
        </p>

        {/* Backup */}
        <div className="flex items-start gap-4">
          <div className="flex-1">
            <p className="text-sm font-medium text-fg2">Создать резервную копию</p>
            <p className="text-xs text-fg3 mt-0.5">Скачать файл .json с текущей конфигурацией системы</p>
          </div>
          <button
            type="button"
            onClick={handleBackup}
            disabled={backupLoading}
            className="flex items-center gap-2 px-3 py-2 text-sm bg-fg1 hover:bg-fg2 disabled:opacity-50 text-white rounded-md transition-colors shrink-0"
          >
            <Download size={14} />
            {backupLoading ? 'Создание...' : 'Скачать'}
          </button>
        </div>
        {backupError && (
          <p className="text-xs text-danger flex items-center gap-1">
            <XCircle size={13} /> {backupError}
          </p>
        )}

        <div className="border-t border-muted" />

        {/* Restore */}
        <div className="flex items-start gap-4">
          <div className="flex-1">
            <p className="text-sm font-medium text-fg2">Восстановить из резервной копии</p>
            <p className="text-xs text-fg3 mt-0.5">
              Загрузить файл .json. Существующие записи будут обновлены, новые — добавлены.
            </p>
          </div>
          <button
            type="button"
            onClick={() => fileInputRef.current?.click()}
            disabled={restoreLoading}
            className="flex items-center gap-2 px-3 py-2 text-sm border border-stroke-strong hover:bg-base disabled:opacity-50 text-fg2 rounded-md transition-colors shrink-0"
          >
            <Upload size={14} />
            {restoreLoading ? 'Восстановление...' : 'Загрузить файл'}
          </button>
          <input
            ref={fileInputRef}
            accept=".zip"
            type="file"
            className="hidden"
            onChange={handleFileSelected}
          />
        </div>
        {restoreError && (
          <p className="text-xs text-danger flex items-center gap-1">
            <XCircle size={13} /> {restoreError}
          </p>
        )}
      </CollapsibleSection>

      {/* Restore confirmation modal */}
      <Modal
        open={confirmOpen}
        onOpenChange={(o) => { if (!o) { setConfirmOpen(false); setPendingFile(null); } }}
        title="Подтвердите восстановление"
        flushBody
      >
        {pendingFile && (
          <RestoreConfirmModal
            file={pendingFile}
            onConfirm={handleConfirmRestore}
            onCancel={() => { setConfirmOpen(false); setPendingFile(null); }}
          />
        )}
      </Modal>

      {/* Restore result modal */}
      <Modal
        open={restoreResult !== null}
        onOpenChange={(o) => { if (!o) setRestoreResult(null); }}
        title={restoreResult?.success ? 'Восстановление завершено' : 'Ошибка восстановления'}
        flushBody
      >
        {restoreResult && (
          <RestoreResultModal
            report={restoreResult}
            onClose={() => setRestoreResult(null)}
          />
        )}
      </Modal>
    </div>
  );
}
