import { useState, useMemo } from 'react';
import { CheckCircle2, AlertCircle, Loader2 } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { useListBindingTemplates } from '@/shared/api/bindingTemplates';
import { useAvailableDataSetFiles, useCreateDataSetBinding } from '@/shared/api/datasets';
import { parseSourceColumnNames } from '@/shared/api/datasetHelpers';
import { DATA_SET_FORMAT_LABELS, SCOPE_LABELS } from '@/shared/api/types';
import type { DataSetBindingTemplate, DataSetFile, DataSetSource, DocumentType } from '@/shared/api/types';

// ─── Column match preview ─────────────────────────────────────────────────────

function ColumnMatchPreview({
  template,
  source,
}: {
  template: DataSetBindingTemplate;
  source: DataSetSource;
}) {
  // Вычисляемые колонки (Transformation) не персистятся в cachedSchema — без их алиасов шаблон,
  // ссылающийся на такую колонку, ложно считался бы «не совпадающим» (issue #49).
  const availableColumns = useMemo<Set<string>>(
    () => {
      const computedAliases = (source.computedColumns ?? []).map(c => c.alias).filter(Boolean);
      return new Set([...parseSourceColumnNames(source.cachedSchema), ...computedAliases]);
    },
    [source.cachedSchema, source.computedColumns],
  );

  const entries = Object.entries(template.columnMappings).filter(([, col]) => col);
  if (entries.length === 0)
    return <p className="text-xs text-fg4">Маппинг не задан</p>;

  const matched = entries.filter(([, col]) => availableColumns.has(col)).length;

  return (
    <div className="space-y-1">
      <p className="text-xs mb-2 text-fg3">
        Совпадение колонок: {matched}/{entries.length}
        {matched < entries.length && (
          <span className="ml-1 text-danger">
            — часть полей не будет заполнена
          </span>
        )}
      </p>
      <div className="space-y-1 max-h-40 overflow-y-auto">
        {entries.map(([fieldKey, colName]) => {
          const found = availableColumns.has(colName);
          return (
            <div key={fieldKey} className="flex items-center gap-2 text-xs">
              {found
                ? <CheckCircle2 size={12} className="text-success shrink-0" />
                : <AlertCircle size={12} className="text-danger shrink-0" />
              }
              <span className="truncate text-fg3 shrink-0" style={{ width: '120px' }} title={fieldKey}>
                {fieldKey}
              </span>
              <span className={`font-mono ${found ? 'text-fg1' : 'text-fg4'}`}>
                {colName}
              </span>
              {!found && (
                <span className="text-[10px] text-danger">не найдено</span>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

// ─── Source selector ──────────────────────────────────────────────────────────

function SourceSelector({
  setId,
  value,
  onChange,
}: {
  setId: string;
  value: string;
  onChange: (sourceId: string, source: DataSetSource & { file: DataSetFile }) => void;
}) {
  const { data: files = [], isLoading } = useAvailableDataSetFiles(setId);
  const allSources = useMemo(
    () => files.flatMap(f => f.sources.map(s => ({ ...s, file: f }))),
    [files],
  );

  if (isLoading)
    return <p className="text-xs py-1 text-fg4">Загрузка источников...</p>;

  if (files.length === 0)
    return (
      <p className="text-xs py-1 text-fg4">
        Нет загруженных наборов данных
      </p>
    );

  return (
    <select
      value={value}
      onChange={e => {
        const src = allSources.find(s => s.id === e.target.value);
        if (src) onChange(e.target.value, src);
      }}
      className="w-full border border-stroke rounded-md px-2 py-1.5 text-sm bg-surface text-fg1"
    >
      <option value="">— выберите источник данных —</option>
      {files.map(f => (
        <optgroup key={f.id} label={`[${SCOPE_LABELS[f.scope]}] ${f.name} (${DATA_SET_FORMAT_LABELS[f.format]})`}>
          {f.sources.map(s => (
            <option key={s.id} value={s.id}>{s.name} · {s.cachedRowCount} строк</option>
          ))}
        </optgroup>
      ))}
    </select>
  );
}

// ─── Main dialog ──────────────────────────────────────────────────────────────

export function ApplyTemplateDialog({
  instanceId,
  setId,
  docType,
  onDone,
  onClose,
}: {
  instanceId: string;
  setId: string;
  docType: DocumentType | undefined;
  onDone: () => void;
  onClose: () => void;
}) {
  const { data: templates = [], isLoading: templatesLoading } = useListBindingTemplates(docType?.id);
  const create = useCreateDataSetBinding();

  const [selectedTemplateId, setSelectedTemplateId] = useState('');
  const [selectedSource, setSelectedSource] = useState<(DataSetSource & { file: DataSetFile }) | null>(null);
  const [error, setError] = useState('');

  const selectedTemplate = templates.find(t => t.id === selectedTemplateId) ?? null;

  async function handleApply() {
    if (!selectedTemplate || !selectedSource) {
      setError('Выберите шаблон и источник данных');
      return;
    }
    setError('');
    try {
      await create.mutateAsync({
        instanceId,
        sourceId: selectedSource.id,
        targetFieldKey: selectedTemplate.targetFieldKey,
        mapping: selectedTemplate.columnMappings,
      });
      onDone();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка создания привязки');
    }
  }

  return (
    <Modal
      open={true}
      onOpenChange={o => { if (!o) onClose(); }}
      title="Применить шаблон данных"
      footer={
        <div className="flex gap-2">
          <button
            onClick={handleApply}
            disabled={!selectedTemplate || !selectedSource || create.isPending}
            className="flex items-center gap-2 px-4 py-2 rounded-md text-sm font-medium text-white disabled:opacity-40 bg-brand"
          >
            {create.isPending && <Loader2 size={13} className="animate-spin" />}
            Создать привязку
          </button>
          <button
            onClick={onClose}
            className="px-4 py-2 rounded-md text-sm font-medium text-fg2 bg-muted"
          >
            Отмена
          </button>
        </div>
      }
    >
      <div className="space-y-5">
        {/* Выбор шаблона */}
        <div>
          <label className="block text-xs font-medium mb-2 text-fg3">
            Шаблон
          </label>
          {templatesLoading ? (
            <p className="text-xs text-fg4">Загрузка шаблонов...</p>
          ) : templates.length === 0 ? (
            <p className="text-xs text-fg4">
              Нет шаблонов для этого типа документа. Создайте шаблон в настройках типа документа.
            </p>
          ) : (
            <div className="space-y-2">
              {templates.map(t => {
                const selected = t.id === selectedTemplateId;
                const mappedCount = Object.keys(t.columnMappings).filter(k => t.columnMappings[k]).length;
                return (
                  <button
                    key={t.id}
                    onClick={() => { setSelectedTemplateId(t.id); setSelectedSource(null); }}
                    className={`w-full text-left px-3 py-2.5 rounded-lg border transition-all ${
                      selected ? 'border-brand bg-brand-subtle' : 'border-stroke bg-surface'
                    }`}
                  >
                    <div className="text-sm font-medium text-fg1">{t.name}</div>
                    <div className="text-xs mt-0.5 text-fg4">
                      {t.targetFieldKey ? `Табличный → ${t.targetFieldKey}` : 'Скалярный'}
                      {' · '}{mappedCount} {mappedCount === 1 ? 'поле' : 'полей'}
                    </div>
                  </button>
                );
              })}
            </div>
          )}
        </div>

        {/* Выбор источника */}
        {selectedTemplate && (
          <div>
            <label className="block text-xs font-medium mb-2 text-fg3">
              Источник данных
            </label>
            <SourceSelector
              setId={setId}
              value={selectedSource?.id ?? ''}
              onChange={(_, src) => setSelectedSource(src)}
            />
          </div>
        )}

        {/* Превью совпадения колонок */}
        {selectedTemplate && selectedSource && (
          <div className="rounded-lg p-3 border border-stroke bg-base">
            <p className="text-xs font-semibold mb-2 text-fg2">
              Проверка колонок
            </p>
            <ColumnMatchPreview template={selectedTemplate} source={selectedSource} />
          </div>
        )}

        {error && (
          <p className="text-xs text-danger">{error}</p>
        )}
      </div>
    </Modal>
  );
}
