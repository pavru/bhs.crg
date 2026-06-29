import { useEffect, useState } from 'react';
import {
  useIntegrationSettings,
  useSaveIntegrationSettings,
  useIntegrationModels,
  type EngineDto,
  type EngineUpdate,
  type IntegrationSettingsUpdate,
} from '../../shared/api/integrationSettings';
import { CollapsibleSection } from './CollapsibleSection';

// ─── Метаданные движков (порядок отображения, подписи, какие поля показывать) ────

interface EngineMeta {
  key: string;
  label: string;
  hint: string;
  keyless?: boolean;      // не использует API-ключ (Ollama)
  modelLabel?: string;    // подпись поля модели
  showBaseUrl?: boolean;  // Ollama
  showFolderId?: boolean; // Yandex
  showHost?: boolean;     // Yandex
}

const RECOGNIZERS: Record<string, EngineMeta> = {
  Gemini: { key: 'Gemini', label: 'Google Gemini', hint: 'Бесплатный лимит. Vision-распознавание.', modelLabel: 'Модель' },
  Anthropic: { key: 'Anthropic', label: 'Anthropic Claude', hint: 'Платный. Высокое качество распознавания.', modelLabel: 'Модель' },
  Ollama: { key: 'Ollama', label: 'Ollama (локально)', hint: 'Локальная модель, без ключа. Только изображения.', keyless: true, modelLabel: 'Модель', showBaseUrl: true },
};

const WEB_ENGINES: Record<string, EngineMeta> = {
  Serper: { key: 'Serper', label: 'Serper (Google)', hint: 'Веб-поиск через google.serper.dev.' },
  Yandex: { key: 'Yandex', label: 'Яндекс XML', hint: 'Yandex Cloud Search API.', showFolderId: true, showHost: true },
};

// Локальная редактируемая форма движка (apiKey — то, что пользователь печатает поверх маски)
interface EngineForm {
  enabled: boolean;
  hasKey: boolean;
  apiKey: string;        // '' = не менять
  model: string;
  baseUrl: string;
  folderId: string;
  host: string;
}

function toForm(dto: EngineDto | undefined): EngineForm {
  return {
    enabled: dto?.enabled ?? false,
    hasKey: dto?.hasKey ?? false,
    apiKey: '',
    model: dto?.model ?? '',
    baseUrl: dto?.baseUrl ?? '',
    folderId: dto?.folderId ?? '',
    host: dto?.host ?? '',
  };
}

function toUpdate(meta: EngineMeta, f: EngineForm): EngineUpdate {
  const u: EngineUpdate = { enabled: f.enabled };
  if (!meta.keyless && f.apiKey.trim()) u.apiKey = f.apiKey.trim();
  if (meta.modelLabel) u.model = f.model.trim() || null;
  if (meta.showBaseUrl) u.baseUrl = f.baseUrl.trim() || null;
  if (meta.showFolderId) u.folderId = f.folderId.trim() || null;
  if (meta.showHost) u.host = f.host.trim() || null;
  return u;
}

// ─── UI-примитивы ────────────────────────────────────────────────────────────

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="block text-xs font-medium text-fg3 mb-1">{label}</span>
      {children}
    </label>
  );
}

const inputCls =
  'w-full px-2.5 py-1.5 text-sm rounded-md border border-stroke bg-surface text-fg1 ' +
  'focus:outline-none focus:border-brand placeholder:text-fg4';

function EngineCard({
  meta, form, onChange, reorder, modelOptions,
}: {
  meta: EngineMeta;
  form: EngineForm;
  onChange: (next: EngineForm) => void;
  reorder?: { onUp?: () => void; onDown?: () => void };
  modelOptions?: string[];
}) {
  return (
    <div className={`rounded-lg border p-3 space-y-3 ${form.enabled ? 'border-stroke bg-surface' : 'border-stroke bg-base'}`}>
      <div className="flex items-start gap-2">
        <label className="flex items-center gap-2 flex-1 cursor-pointer">
          <input
            type="checkbox"
            checked={form.enabled}
            onChange={e => onChange({ ...form, enabled: e.target.checked })}
            className="accent-brand"
          />
          <span className="text-sm font-medium text-fg1">{meta.label}</span>
        </label>
        {reorder && (
          <div className="flex gap-1">
            <button type="button" onClick={reorder.onUp} disabled={!reorder.onUp}
              className="px-1.5 text-fg3 hover:text-fg1 disabled:opacity-30 disabled:hover:text-fg3" title="Выше">↑</button>
            <button type="button" onClick={reorder.onDown} disabled={!reorder.onDown}
              className="px-1.5 text-fg3 hover:text-fg1 disabled:opacity-30 disabled:hover:text-fg3" title="Ниже">↓</button>
          </div>
        )}
      </div>
      <p className="text-xs text-fg4">{meta.hint}</p>

      {!meta.keyless && (
        <Field label="API-ключ">
          <input
            type="password"
            autoComplete="off"
            value={form.apiKey}
            onChange={e => onChange({ ...form, apiKey: e.target.value })}
            placeholder={form.hasKey ? '•••••••• (ключ задан — оставьте пустым, чтобы не менять)' : 'ключ не задан'}
            className={inputCls}
          />
        </Field>
      )}

      {meta.modelLabel && (() => {
        // в список добавляем текущее значение, чтобы кастомная модель из конфига не потерялась
        const opts = Array.from(new Set([...(modelOptions ?? []), form.model].filter(Boolean)));
        return (
          <Field label={meta.modelLabel}>
            {opts.length === 0 ? (
              <div className="text-xs text-fg4 px-2.5 py-2 rounded-md border border-dashed border-stroke">
                {meta.key === 'Ollama'
                  ? <>Нет скачанных моделей. Выполните <code className="font-mono text-fg3">ollama pull qwen2.5vl:7b</code></>
                  : 'Список моделей недоступен'}
              </div>
            ) : (
              <select
                value={form.model}
                onChange={e => onChange({ ...form, model: e.target.value })}
                className={inputCls}
              >
                {!form.model && <option value="">— выберите —</option>}
                {opts.map(o => <option key={o} value={o}>{o}</option>)}
              </select>
            )}
          </Field>
        );
      })()}

      {meta.showBaseUrl && (
        <Field label="Базовый URL">
          <input type="text" value={form.baseUrl} placeholder="http://localhost:11434"
            onChange={e => onChange({ ...form, baseUrl: e.target.value })}
            className={inputCls} />
        </Field>
      )}

      {meta.showFolderId && (
        <Field label="Folder ID">
          <input type="text" value={form.folderId}
            onChange={e => onChange({ ...form, folderId: e.target.value })}
            className={inputCls} />
        </Field>
      )}

      {meta.showHost && (
        <Field label="Host (необязательно)">
          <input type="text" value={form.host} placeholder="https://yandex.ru/search/xml"
            onChange={e => onChange({ ...form, host: e.target.value })}
            className={inputCls} />
        </Field>
      )}
    </div>
  );
}

function DomainList({ label, hint, value, onChange }: {
  label: string; hint: string; value: string[]; onChange: (v: string[]) => void;
}) {
  // Храним «сырой» текст, чтобы можно было добавлять переводы строк и пробелы;
  // наружу отдаём очищенный список. Синхронизируемся с внешним значением (загрузка),
  // но не сбрасываем при собственном вводе.
  const [text, setText] = useState(value.join('\n'));
  useEffect(() => {
    const parsedLocal = text.split('\n').map(s => s.trim()).filter(Boolean).join('\n');
    if (value.join('\n') !== parsedLocal) setText(value.join('\n'));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value]);

  return (
    <Field label={label}>
      <p className="text-xs text-fg4 mb-1">{hint}</p>
      <textarea
        value={text}
        onChange={e => { setText(e.target.value); onChange(e.target.value.split('\n').map(s => s.trim()).filter(Boolean)); }}
        rows={Math.min(Math.max(text.split('\n').length + 1, 3), 12)}
        className={`${inputCls} font-mono resize-y`}
        spellCheck={false}
      />
    </Field>
  );
}

// ─── Секция ──────────────────────────────────────────────────────────────────

export function IntegrationSettingsSection() {
  const { data, isLoading, error } = useIntegrationSettings();
  const { data: models } = useIntegrationModels();
  const save = useSaveIntegrationSettings();

  const modelOptionsFor = (key: string): string[] => {
    switch (key) {
      case 'Gemini': return models?.gemini ?? [];
      case 'Anthropic': return models?.anthropic ?? [];
      case 'Ollama': return models?.ollama ?? [];
      default: return [];
    }
  };

  const [order, setOrder] = useState<string[]>([]);
  const [recog, setRecog] = useState<Record<string, EngineForm>>({});
  const [web, setWeb] = useState<Record<string, EngineForm>>({});
  const [fgis, setFgis] = useState<string[]>([]);
  const [manufacturers, setManufacturers] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);

  // Инициализация формы из загруженных данных
  useEffect(() => {
    if (!data) return;
    const recKeys = Object.keys(RECOGNIZERS);
    const ord = [
      ...data.recognitionOrder.filter(k => recKeys.includes(k)),
      ...recKeys.filter(k => !data.recognitionOrder.includes(k)),
    ];
    setOrder(ord);
    setRecog(Object.fromEntries(recKeys.map(k => [k, toForm(data.recognition[k])])));
    setWeb(Object.fromEntries(Object.keys(WEB_ENGINES).map(k => [k, toForm(data.webSearch[k])])));
    setFgis(data.fgisDomains);
    setManufacturers(data.manufacturerDomains);
  }, [data]);

  function move(idx: number, dir: -1 | 1) {
    setOrder(prev => {
      const next = [...prev];
      const j = idx + dir;
      if (j < 0 || j >= next.length) return prev;
      [next[idx], next[j]] = [next[j], next[idx]];
      return next;
    });
  }

  async function handleSave() {
    const update: IntegrationSettingsUpdate = {
      recognitionOrder: order,
      recognition: Object.fromEntries(order.map(k => [k, toUpdate(RECOGNIZERS[k], recog[k])])),
      webSearch: Object.fromEntries(Object.keys(WEB_ENGINES).map(k => [k, toUpdate(WEB_ENGINES[k], web[k])])),
      fgisDomains: fgis,
      manufacturerDomains: manufacturers,
    };
    await save.mutateAsync(update);
    setSaved(true);
    setTimeout(() => setSaved(false), 2500);
  }

  return (
    <CollapsibleSection title="Поиск и распознавание" storageKey="integrations" defaultOpen={false}>
      <p className="text-xs text-fg3">
        Движки распознавания документов качества (vision-LLM) и веб-поиска. Ключи хранятся на сервере
        и не отображаются — оставьте поле пустым, чтобы сохранить текущий.
      </p>

      {isLoading && <p className="text-sm text-fg3">Загрузка…</p>}
      {error && <p className="text-sm text-danger">Не удалось загрузить настройки.</p>}

      {data && (
        <>
          {/* Распознавание + приоритет */}
          <div className="space-y-2">
            <h3 className="text-xs font-semibold text-fg2">Распознавание реквизитов (по приоритету)</h3>
            <p className="text-xs text-fg4">
              Движки опрашиваются сверху вниз: первый включённый с заданным ключом обрабатывает документ.
            </p>
            <div className="space-y-2">
              {order.map((k, i) => recog[k] && (
                <EngineCard
                  key={k}
                  meta={RECOGNIZERS[k]}
                  form={recog[k]}
                  onChange={f => setRecog(prev => ({ ...prev, [k]: f }))}
                  modelOptions={modelOptionsFor(k)}
                  reorder={{
                    onUp: i > 0 ? () => move(i, -1) : undefined,
                    onDown: i < order.length - 1 ? () => move(i, 1) : undefined,
                  }}
                />
              ))}
            </div>
          </div>

          {/* Веб-поиск */}
          <div className="space-y-2">
            <h3 className="text-xs font-semibold text-fg2">Веб-поиск документов</h3>
            <p className="text-xs text-fg4">Выдача всех включённых движков объединяется.</p>
            <div className="space-y-2">
              {Object.keys(WEB_ENGINES).map(k => web[k] && (
                <EngineCard
                  key={k}
                  meta={WEB_ENGINES[k]}
                  form={web[k]}
                  onChange={f => setWeb(prev => ({ ...prev, [k]: f }))}
                />
              ))}
            </div>
          </div>

          {/* Домены */}
          <div className="space-y-3">
            <h3 className="text-xs font-semibold text-fg2">Домены для тиров поиска</h3>
            <DomainList
              label="ФГИС"
              hint="Реестры сертификатов/деклараций (site:-фильтр первого тира)."
              value={fgis}
              onChange={setFgis}
            />
            <DomainList
              label="Производители"
              hint="Сайты производителей (site:-фильтр второго тира), по одному на строку."
              value={manufacturers}
              onChange={setManufacturers}
            />
          </div>

          <div className="flex items-center gap-3 pt-1">
            <button
              type="button"
              onClick={handleSave}
              disabled={save.isPending}
              className="px-4 py-2 text-sm font-medium rounded-md bg-brand text-white hover:bg-brand-hover disabled:opacity-50"
            >
              {save.isPending ? 'Сохранение…' : 'Сохранить'}
            </button>
            {saved && <span className="text-sm text-success">Сохранено</span>}
            {save.isError && <span className="text-sm text-danger">Ошибка сохранения</span>}
          </div>
        </>
      )}
    </CollapsibleSection>
  );
}
