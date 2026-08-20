import { useEffect, useState } from 'react';
import { AlertTriangle, AlertCircle, Eye } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { Select, SelectItem } from '@/shared/ui/Select';
import {
  useIntegrationSettings,
  useSaveIntegrationSettings,
  useIntegrationModels,
  useCheckVision,
  type EngineDto,
  type EngineUpdate,
  type IntegrationSettingsUpdate,
  type UnavailableModel,
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

function EngineCard({
  meta, form, onChange, reorder, modelOptions, missing, modelIssue, blindIssue, unavailable, modelsChecked,
  visionCheckable,
}: {
  meta: EngineMeta;
  form: EngineForm;
  onChange: (next: EngineForm) => void;
  reorder?: { onUp?: () => void; onDown?: () => void };
  modelOptions?: string[];
  /** Чего не хватает движку по СОХРАНЁННЫМ настройкам (с сервера); null — настроен. */
  missing?: string | null;
  /** Беда с выбранной моделью (её у поставщика нет); null — либо всё хорошо, либо не проверяли. */
  modelIssue?: string | null;
  /**
   * Модель не принимает изображения (issue #801); null — либо не уличена, либо не проверяли.
   * Отдельно от modelIssue, потому что это претензия иного веса: ненастроенный движок цепочка
   * пропускает, движок с несуществующей моделью получает отказ, а слепой — ОТВЕЧАЕТ, и ответ его
   * выдуман целиком. Единственная из трёх, которая портит данные.
   */
  blindIssue?: string | null;
  /** Показывать ли кнопку проверки зрения (спрашиваем только локальную Ollama). */
  visionCheckable?: boolean;
  /** Пункты списка, которые поставщик точно не принимает. */
  unavailable?: UnavailableModel[];
  /** Удалось ли вообще спросить поставщика про модели (для честной подсказки при пустом списке). */
  modelsChecked?: boolean;
}) {
  // Бейдж говорит о том, что происходит СЕЙЧАС, — потому и считается по сохранённому состоянию, а
  // не по набранному в форме: пока изменения не сохранены, движок в самом деле не участвует.
  // Галку «включён» берём из формы: сняв её, пользователь уже не обещает участия — и упрёк ни к чему.
  const notParticipating = form.enabled && !!missing;
  const blind = form.enabled && !!blindIssue;
  // Проверка зрения по кнопке — её результат живёт до перезагрузки списка моделей: сервер кэширует
  // вердикт сам, здесь только то, что человек видит сразу после нажатия.
  const checkVision = useCheckVision();
  const checked = checkVision.data;
  return (
    <div className={`rounded-lg border p-3 space-y-3 ${
      blind ? 'border-danger bg-surface' : form.enabled ? 'border-stroke bg-surface' : 'border-stroke bg-base'}`}>
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
        {/* Один сигнал, не три: слепота > «модель не обслуживается» > «не участвует». Слепой движок
            не «не участвует» — он участвует и портит данные, и назвать это мягче значит соврать. */}
        {blind && (
          <span
            className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-medium bg-danger-subtle text-danger border border-danger-border shrink-0"
            title={blindIssue ?? undefined}
          >
            <AlertCircle size={11} /> не работает: модель без зрения
          </span>
        )}
        {!blind && notParticipating && (
          <span
            className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-medium bg-warning-subtle text-warning border border-warning-border shrink-0"
            title={`Движок включён, но в работе не участвует: ${missing}. Распознавание идёт следующим по списку.`}
          >
            <AlertTriangle size={11} /> не участвует: {missing}
          </span>
        )}
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

      {/* Беда с моделью — отдельной строкой, а не бейджем в шапке: там она не поместится, а совет
          поставщика («перейдите на такую-то») — самое полезное в этом сообщении и прятать его в
          подсказку по наведению незачем. Отдельно от «не участвует» ещё и по смыслу: ненастроенный
          движок цепочка пропускает, а этот — берёт и получает отказ. */}
      {blind && (
        <p className="flex items-start gap-1.5 rounded px-2 py-1.5 text-xs bg-danger-subtle text-danger border border-danger-border">
          <AlertCircle size={13} className="shrink-0 mt-px" />
          <span>{blindIssue}</span>
        </p>
      )}

      {form.enabled && !blind && modelIssue && (
        <p className="flex items-start gap-1.5 rounded px-2 py-1.5 text-xs bg-warning-subtle text-warning border border-warning-border">
          <AlertTriangle size={13} className="shrink-0 mt-px" />
          <span>{modelIssue}</span>
        </p>
      )}

      {!meta.keyless && (
        <TextField label="API-ключ" type="password" autoComplete="off" value={form.apiKey}
          onChange={e => onChange({ ...form, apiKey: e.target.value })}
          hint={form.hasKey ? '•••••••• (ключ задан — оставьте пустым, чтобы не менять)' : 'ключ не задан'} />
      )}

      {meta.modelLabel && (() => {
        // в список добавляем текущее значение, чтобы кастомная модель из конфига не потерялась
        const opts = Array.from(new Set([...(modelOptions ?? []), form.model].filter(Boolean)));
        const gone = (o: string) => unavailable?.find(u => u.model === o);
        return (
          opts.length === 0 ? (
            <Field label={meta.modelLabel}>
              <div className="text-xs text-fg4 px-2.5 py-2 rounded-md border border-dashed border-stroke">
                {meta.key !== 'Ollama'
                  ? 'Список моделей недоступен'
                  : modelsChecked
                    // Спросили и получили ноль — моделей действительно нет.
                    ? <>Нет скачанных моделей. Выполните <code className="font-mono text-fg3">ollama pull qwen2.5vl:7b</code></>
                    // Спросить не вышло. Советовать «скачайте модель» тут нельзя: скачано может быть
                    // всё что угодно, просто Ollama не отвечает.
                    : <>Ollama не отвечает — список скачанных моделей узнать не удалось.</>}
              </div>
            </Field>
          ) : (
            <Select label={meta.modelLabel} value={form.model || undefined} placeholder="— выберите —"
              onValueChange={m => onChange({ ...form, model: m })}>
              {opts.map(o => {
                // Пометка приходит с сервера (он же и сравнивал имена) — здесь только показываем.
                const u = gone(o);
                return (
                  <SelectItem key={o} value={o}>
                    {u ? `${o} — недоступна` : o}
                  </SelectItem>
                );
              })}
            </Select>
          )
        );
      })()}

      {/* Проверка зрения — в строке модели, а не у движка: проверяется пара (движок, модель), и
          сменив модель, человек получает НЕПРОВЕРЕННУЮ пару, а не прежний вердикт. Автоматически
          при открытии секции не запускаем: она общая, сюда заходят и за доменами ФГИС, а канарейка
          на холодной модели ждёт минуты. */}
      {visionCheckable && form.enabled && !missing && form.model && (
        <div className="flex items-center gap-2 flex-wrap">
          <Button type="button" variant="text" onClick={() => checkVision.mutate(meta.key)}
            disabled={checkVision.isPending}>
            <Eye size={13} className="mr-1" /> Проверить зрение
          </Button>
          <span className="text-xs text-fg4">
            {checkVision.isPending
              ? 'Показываю модели картинку и спрашиваю цвета…'
              : checked?.state === 'sighted'
                ? <span className="text-success">✓ зрение проверено</span>
                : checked?.state === 'unknown'
                  // «Не проверили» — не приговор модели: остановленная Ollama выглядела бы слепой.
                  ? (checked.error ?? 'проверить не удалось')
                  // Про слепоту уже сказано полосой выше — второй раз не повторяем.
                  : checked?.state === 'blind' ? '' : 'зрение не проверялось'}
          </span>
        </div>
      )}

      {meta.showBaseUrl && (
        <TextField label="Базовый URL" value={form.baseUrl} hint="http://localhost:11434"
          onChange={e => onChange({ ...form, baseUrl: e.target.value })} />
      )}

      {meta.showFolderId && (
        <TextField label="Folder ID" value={form.folderId}
          onChange={e => onChange({ ...form, folderId: e.target.value })} />
      )}

      {meta.showHost && (
        <TextField label="Host (необязательно)" value={form.host} hint="https://yandex.ru/search/xml"
          onChange={e => onChange({ ...form, host: e.target.value })} />
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
    <TextAreaField
      label={label} hint={hint} value={text}
      onChange={e => { setText(e.target.value); onChange(e.target.value.split('\n').map(s => s.trim()).filter(Boolean)); }}
      rows={Math.min(Math.max(text.split('\n').length + 1, 3), 12)}
      className="font-mono resize-y"
      spellCheck={false}
    />
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
                  missing={data.recognition[k]?.missing}
                  modelIssue={models?.issues?.[k]}
                  blindIssue={models?.blind?.[k]}
                  visionCheckable={k === 'Ollama'}
                  unavailable={models?.unavailable?.[k]}
                  modelsChecked={k === 'Ollama' ? models?.ollamaChecked : undefined}
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
                  missing={data.webSearch[k]?.missing}
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
            <Button variant="filled" onClick={handleSave} loading={save.isPending}>
              {save.isPending ? 'Сохранение…' : 'Сохранить'}
            </Button>
            {saved && <span className="text-sm text-success">Сохранено</span>}
            {save.isError && <span className="text-sm text-danger">Ошибка сохранения</span>}
          </div>
        </>
      )}
    </CollapsibleSection>
  );
}
