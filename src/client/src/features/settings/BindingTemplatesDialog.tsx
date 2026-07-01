import { useState, useMemo } from 'react';
import { Plus, Trash2, Pencil, Check, Database } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import {
  useListBindingTemplates, useCreateBindingTemplate,
  useUpdateBindingTemplate, useDeleteBindingTemplate,
} from '@/shared/api/bindingTemplates';
import { resolveEffectiveFields, isScalarField, type SchemaField } from '@/shared/api/schema';
import type { DataSetBindingTemplate, DocumentType } from '@/shared/api/types';

// Shared input styling.
const FIELD_CLS = 'border border-stroke rounded-md px-3 py-1.5 text-sm bg-surface text-fg1';

// ─── Template form ────────────────────────────────────────────────────────────

interface TemplateFormState {
  name: string;
  targetFieldKey: string;   // '' = scalar
  columnMappings: Record<string, string>;
}

function TemplateForm({
  docType,
  allDocTypes,
  initial,
  onSave,
  onCancel,
  saving,
}: {
  docType: DocumentType;
  allDocTypes: DocumentType[];
  initial?: DataSetBindingTemplate;
  onSave: (state: TemplateFormState) => void;
  onCancel: () => void;
  saving: boolean;
}) {
  const allFields = useMemo(() => resolveEffectiveFields(docType, allDocTypes), [docType, allDocTypes]);
  const arrayFields = allFields.filter(f => f.type === 'array');
  const scalarFields = allFields.filter(isScalarField);

  const [name, setName] = useState(initial?.name ?? '');
  const [targetFieldKey, setTargetFieldKey] = useState(initial?.targetFieldKey ?? '');
  const [columnMappings, setColumnMappings] = useState<Record<string, string>>(
    initial?.columnMappings ?? {},
  );

  const mappableFields = useMemo<SchemaField[]>(() => {
    if (!targetFieldKey) return scalarFields;
    const arrayField = arrayFields.find(f => f.key === targetFieldKey);
    if (!arrayField?.typeId) return [];
    const compositeType = allDocTypes.find(dt => dt.id === arrayField.typeId);
    if (!compositeType) return [];
    return resolveEffectiveFields(compositeType, allDocTypes).filter(isScalarField);
  }, [targetFieldKey, arrayFields, allDocTypes, scalarFields]);

  function setMapping(fieldKey: string, colName: string) {
    setColumnMappings(prev => {
      const next = { ...prev };
      if (colName.trim()) next[fieldKey] = colName.trim();
      else delete next[fieldKey];
      return next;
    });
  }

  function handleTargetChange(val: string) {
    setTargetFieldKey(val);
    setColumnMappings({});
  }

  return (
    <div className="space-y-4">
      {/* Название */}
      <div>
        <label className="block text-xs font-medium mb-1 text-fg3">
          Название шаблона
        </label>
        <input
          value={name}
          onChange={e => setName(e.target.value)}
          placeholder="Напр.: Список материалов"
          className={`w-full ${FIELD_CLS}`}
        />
      </div>

      {/* Режим */}
      <div>
        <label className="block text-xs font-medium mb-1 text-fg3">
          Режим
        </label>
        <select
          value={targetFieldKey}
          onChange={e => handleTargetChange(e.target.value)}
          className="w-full border border-stroke rounded-md px-2 py-1.5 text-sm bg-surface text-fg1"
        >
          <option value="">Скалярный — первая строка → отдельные поля</option>
          {arrayFields.map(f => (
            <option key={f.key} value={f.key}>
              Табличный → {f.title} ({f.key})
            </option>
          ))}
        </select>
      </div>

      {/* Маппинг */}
      <div>
        <label className="block text-xs font-medium mb-2 text-fg3">
          Ожидаемые колонки в файле
          <span className="ml-1 font-normal text-fg4">
            (оставьте пустым, чтобы пропустить поле)
          </span>
        </label>
        {mappableFields.length === 0 ? (
          <p className="text-xs text-fg4">
            {targetFieldKey
              ? 'Нет полей для маппинга (возможно, тип не задан или не найден)'
              : 'Нет простых полей в схеме документа'}
          </p>
        ) : (
          <div className="space-y-1.5 max-h-60 overflow-y-auto pr-1">
            {mappableFields.map(f => (
              <div key={f.key} className="flex items-center gap-2">
                <div className="w-44 shrink-0">
                  <span className="text-xs font-medium truncate block text-fg2" title={f.title}>
                    {f.title}
                  </span>
                  <span className="text-[10px] font-mono text-fg4">{f.key}</span>
                </div>
                <input
                  value={columnMappings[f.key] ?? ''}
                  onChange={e => setMapping(f.key, e.target.value)}
                  placeholder="Название колонки в файле"
                  className="flex-1 border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1"
                />
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Кнопки */}
      <div className="flex gap-2 pt-1">
        <button
          onClick={() => onSave({ name, targetFieldKey, columnMappings })}
          disabled={saving || !name.trim()}
          className="flex items-center gap-1.5 px-3 py-1.5 rounded-md text-sm font-medium text-white disabled:opacity-40 bg-brand"
        >
          <Check size={13} />
          {saving ? 'Сохранение...' : 'Сохранить'}
        </button>
        <button
          onClick={onCancel}
          className="px-3 py-1.5 rounded-md text-sm font-medium text-fg2 bg-muted"
        >
          Отмена
        </button>
      </div>
    </div>
  );
}

// ─── Template row ─────────────────────────────────────────────────────────────

function TemplateRow({
  template,
  docType,
  allDocTypes,
}: {
  template: DataSetBindingTemplate;
  docType: DocumentType;
  allDocTypes: DocumentType[];
}) {
  const [editing, setEditing] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const update = useUpdateBindingTemplate();
  const del = useDeleteBindingTemplate();

  const allFields = useMemo(() => resolveEffectiveFields(docType, allDocTypes), [docType, allDocTypes]);
  const arrayField = template.targetFieldKey
    ? allFields.find(f => f.key === template.targetFieldKey)
    : null;

  const mappedCount = Object.keys(template.columnMappings).filter(k => template.columnMappings[k]).length;

  async function handleSave(state: TemplateFormState) {
    await update.mutateAsync({
      documentTypeId: docType.id,
      id: template.id,
      name: state.name,
      targetFieldKey: state.targetFieldKey || null,
      columnMappings: state.columnMappings,
    });
    setEditing(false);
  }

  return (
    <div className="border-b border-stroke last:border-0">
      {/* Заголовок */}
      <div className="flex items-center gap-3 px-4 py-3">
        <Database size={13} className="text-brand shrink-0" />
        <div className="flex-1 min-w-0">
          <div className="text-sm font-medium text-fg1">{template.name}</div>
          <div className="flex items-center gap-2 mt-0.5 flex-wrap">
            <span className="text-xs text-fg4">
              {template.targetFieldKey
                ? `Табличный → ${arrayField?.title ?? template.targetFieldKey}`
                : 'Скалярный'}
              {' · '}{mappedCount} {mappedCount === 1 ? 'поле' : 'полей'}
            </span>
          </div>
        </div>
        <button
          onClick={() => setEditing(e => !e)}
          className="p-1.5 rounded text-fg3"
          title="Редактировать"
        >
          <Pencil size={13} />
        </button>
        {!confirming ? (
          <button
            onClick={() => setConfirming(true)}
            className="p-1.5 rounded text-fg4 hover:text-danger transition-colors"
            title="Удалить"
          >
            <Trash2 size={13} />
          </button>
        ) : (
          <div className="flex items-center gap-1.5 text-xs">
            <span className="text-fg3">Удалить?</span>
            <button
              onClick={() => del.mutateAsync({ documentTypeId: docType.id, id: template.id }).then(() => setConfirming(false))}
              disabled={del.isPending}
              className="px-2 py-0.5 rounded text-white bg-danger"
              style={{ fontSize: '11px' }}
            >
              Да
            </button>
            <button
              onClick={() => setConfirming(false)}
              className="px-2 py-0.5 rounded bg-muted text-fg2"
              style={{ fontSize: '11px' }}
            >
              Нет
            </button>
          </div>
        )}
      </div>

      {/* Редактор */}
      {editing && (
        <div className="px-4 pb-4 bg-base">
          <TemplateForm
            docType={docType}
            allDocTypes={allDocTypes}
            initial={template}
            onSave={handleSave}
            onCancel={() => setEditing(false)}
            saving={update.isPending}
          />
        </div>
      )}
    </div>
  );
}

// ─── Main dialog ──────────────────────────────────────────────────────────────

export function BindingTemplatesDialog({
  docType,
  allDocTypes,
  onClose,
}: {
  docType: DocumentType;
  allDocTypes: DocumentType[];
  onClose: () => void;
}) {
  const { data: templates = [], isLoading } = useListBindingTemplates(docType.id);
  const create = useCreateBindingTemplate();
  const [adding, setAdding] = useState(false);

  async function handleCreate(state: TemplateFormState) {
    await create.mutateAsync({
      documentTypeId: docType.id,
      name: state.name,
      targetFieldKey: state.targetFieldKey || null,
      columnMappings: state.columnMappings,
    });
    setAdding(false);
  }

  return (
    <Modal
      open={true}
      onOpenChange={o => { if (!o) onClose(); }}
      title={`Шаблоны данных — ${docType.name}`}
      wide
    >
      <p className="text-xs mb-4 text-fg4">
        Шаблоны задают стандартный маппинг для этого типа документа (фильтрация/преобразование/
        сортировка — на уровне источника данных, см. «Наборы данных»).
        При добавлении источника можно применить шаблон — маппинг заполнится автоматически.
      </p>

      {/* Список */}
      <div className="rounded-xl overflow-hidden mb-4 border border-stroke bg-surface">
        {isLoading ? (
          <div className="p-6 text-center text-sm text-fg4">Загрузка...</div>
        ) : templates.length === 0 && !adding ? (
          <div className="p-6 text-center text-sm text-fg4">
            Нет шаблонов. Создайте первый шаблон для ускорения настройки маппинга.
          </div>
        ) : (
          templates.map(t => (
            <TemplateRow key={t.id} template={t} docType={docType} allDocTypes={allDocTypes} />
          ))
        )}
      </div>

      {/* Форма добавления */}
      {adding ? (
        <div className="rounded-xl p-4 border border-stroke bg-base">
          <p className="text-xs font-semibold mb-3 text-fg3">Новый шаблон</p>
          <TemplateForm
            docType={docType}
            allDocTypes={allDocTypes}
            onSave={handleCreate}
            onCancel={() => setAdding(false)}
            saving={create.isPending}
          />
        </div>
      ) : (
        <button
          onClick={() => setAdding(true)}
          className="flex items-center gap-2 text-sm font-medium px-3 py-2 rounded-md text-brand bg-brand-subtle"
        >
          <Plus size={14} /> Добавить шаблон
        </button>
      )}
    </Modal>
  );
}
