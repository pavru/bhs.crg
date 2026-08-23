import { useState } from 'react';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import {
  useLocale, LOCALE_OPTIONS, SYSTEM_LOCALE, resolveLocale, formatDate, formatNumber,
} from '@/shared/hooks/useLocale';
import { IntegrationSettingsSection } from './IntegrationSettingsSection';
import { EmailSettingsSection } from './EmailSettingsSection';
import { CollapsibleSection } from './CollapsibleSection';
import { UpdateSettingsSection } from './UpdateSettingsSection';
import { ImageMaintenanceSection } from './ImageMaintenanceSection';
import { OrphanObjectsSection } from './OrphanObjectsSection';
import { OrphanBlobsSection } from './OrphanBlobsSection';
import { MaterialLabelSection } from './MaterialLabelSection';
import { BackupSection } from './BackupSection';
import { GithubSettingsSection } from './GithubSettingsSection';

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

  function handleSave(e: React.FormEvent) {
    e.preventDefault();
    const v = Number(input);
    if (!Number.isFinite(v) || v < 2) return;
    setMaxVersions(v);
    setSaved(true);
    setTimeout(() => setSaved(false), 2000);
  }

  return (
    <div className="px-6 py-4 max-w-3xl space-y-5">
      <h1 className="text-xl font-semibold text-fg1">Настройки</h1>

      {/* ── Template versioning ────────────────────────────────────────────── */}
      <form onSubmit={handleSave}>
        <CollapsibleSection title="Шаблоны" storageKey="templates">
          <div>
            <p className="text-xs text-fg3 mb-2">
              При превышении система предложит удалить старые версии. Минимум — 2.
            </p>
            <TextField containerClassName="w-40" label="Максимум версий шаблона"
              type="number" min={2} max={100} value={input}
              onChange={e => { setInput(e.target.value); setSaved(false); }} />
          </div>
          <div className="flex items-center gap-3">
            <Button type="submit" variant="filled">Сохранить</Button>
            {saved && <span className="text-sm text-success">Сохранено</span>}
          </div>
        </CollapsibleSection>
      </form>

      {/* ── Locale / regional settings ─────────────────────────────────────── */}
      <LocaleSection />

      {/* ── Поиск и распознавание (интеграции) ─────────────────────────────── */}
      <IntegrationSettingsSection />

      {/* ── Почта (SMTP) ───────────────────────────────────────────────────── */}
      <EmailSettingsSection />

      {/* ── Обновления системы (issue #813) ────────────────────────────────── */}
      <UpdateSettingsSection />

      <GithubSettingsSection />

      <ImageMaintenanceSection />

      <MaterialLabelSection />

      <OrphanObjectsSection />

      <OrphanBlobsSection />

      {/* ── Резервное копирование (issue #831: каталог копий на сервере) ────── */}
      <BackupSection />
    </div>
  );
}
