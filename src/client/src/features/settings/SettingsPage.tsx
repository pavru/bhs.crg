import { useState } from 'react';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
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
import { ProxySettingsSection } from './ProxySettingsSection';
import { useMaxTemplateVersions } from './useMaxTemplateVersions';

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

      {/* ── Прокси для внешних сервисов (issue #936) ─────────────────────────── */}
      <ProxySettingsSection />

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
