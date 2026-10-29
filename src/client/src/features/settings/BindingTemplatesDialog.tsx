import { useState, useMemo } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Add, Check, Database, Delete, Edit } from '@/shared/ui/icons';
import { Modal } from '@/shared/ui/Modal';
import {
  useListBindingTemplates, useCreateBindingTemplate,
  useUpdateBindingTemplate, useDeleteBindingTemplate,
} from '@/shared/api/bindingTemplates';
import { resolveEffectiveFields, isScalarField, type SchemaField } from '@/shared/api/schema';
import type { DataSetBindingTemplate, DocumentType } from '@/shared/api/types';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Tooltip } from '@/shared/ui/Tooltip';

// Shared input styling.
const FIELD_CLS = 'border border-outline-variant rounded-md px-3 py-1.5 text-sm bg-surface text-on-surface';

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
        <label className="block text-xs font-medium mb-1 text-on-surface-variant">
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
      <Select label="Режим" value={targetFieldKey} onValueChange={handleTargetChange}
        emptyItem="Скалярный — первая строка → отдельные поля">
        {arrayFields.map(f => (
          <SelectItem key={f.key} value={f.key}>
            Табличный → {f.title} ({f.key})
          </SelectItem>
        ))}
      </Select>

      {/* Маппинг */}
      <div>
        <label className="block text-xs font-medium mb-2 text-on-surface-variant">
          Ожидаемые колонки в файле
          <span className="ml-1 font-normal text-on-surface-variant">
            (оставьте пустым, чтобы пропустить поле)
          </span>
        </label>
        {mappableFields.length === 0 ? (
          <p className="text-xs text-on-surface-variant">
            {targetFieldKey
              ? 'Нет полей для маппинга (возможно, тип не задан или не найден)'
              : 'Нет простых полей в схеме документа'}
          </p>
        ) : (
          <div className="space-y-1.5 max-h-60 overflow-y-auto pr-1">
            {mappableFields.map(f => (
              <div key={f.key} className="flex items-center gap-2">
                <div className="w-44 shrink-0">
                  <span className="text-xs font-medium truncate block text-on-surface-variant" title={f.title}>
                    {f.title}
                  </span>
                  <span className="text-caption font-mono text-on-surface-variant">{f.key}</span>
                </div>
                <input
                  value={columnMappings[f.key] ?? ''}
                  onChange={e => setMapping(f.key, e.target.value)}
                  placeholder="Название колонки в файле"
                  className="flex-1 border border-outline-variant rounded px-2 py-1 text-xs bg-surface text-on-surface"
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
          className="flex items-center gap-1.5 px-3 py-1.5 rounded-md text-sm font-medium text-on-primary disabled:opacity-40 bg-primary"
        >
          <Icon icon={Check} size={18} />
          {saving ? 'Сохранение...' : 'Сохранить'}
        </button>
        <button
          onClick={onCancel}
          className="px-3 py-1.5 rounded-md text-sm font-medium text-on-surface-variant bg-surface-container"
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
    <div className="border-b border-outline-variant last:border-0">
      {/* Заголовок */}
      <div className="flex items-center gap-3 px-4 py-3">
        <Icon icon={Database} size={18} className="text-primary shrink-0" />
        <div className="flex-1 min-w-0">
          <div className="text-sm font-medium text-on-surface">{template.name}</div>
          <div className="flex items-center gap-2 mt-0.5 flex-wrap">
            <span className="text-xs text-on-surface-variant">
              {template.targetFieldKey
                ? `Табличный → ${arrayField?.title ?? template.targetFieldKey}`
                : 'Скалярный'}
              {' · '}{mappedCount} {mappedCount === 1 ? 'поле' : 'полей'}
            </span>
          </div>
        </div>
        <Tooltip content="Редактировать" name>
          <button
            onClick={() => setEditing(e => !e)}
            className="p-1.5 rounded text-on-surface-variant"
          >
            <Icon icon={Edit} size={18} />
          </button>
        </Tooltip>
        {!confirming ? (
          <Tooltip content="Удалить" name>
            <button
              onClick={() => setConfirming(true)}
              className="p-1.5 rounded text-on-surface-variant hover:text-error transition-colors"
            >
              <Icon icon={Delete} size={18} />
            </button>
          </Tooltip>
        ) : (
          <div className="flex items-center gap-1.5 text-xs">
            <span className="text-on-surface-variant">Удалить?</span>
            <button
              onClick={() => del.mutateAsync({ documentTypeId: docType.id, id: template.id }).then(() => setConfirming(false))}
              disabled={del.isPending}
              className="px-2 py-0.5 rounded text-on-error bg-error"
              style={{ fontSize: '11px' }}
            >
              Да
            </button>
            <button
              onClick={() => setConfirming(false)}
              className="px-2 py-0.5 rounded bg-surface-container text-on-surface-variant"
              style={{ fontSize: '11px' }}
            >
              Нет
            </button>
          </div>
        )}
      </div>

      {/* Редактор */}
      {editing && (
        <div className="px-4 pb-4 bg-surface-container-low">
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
      <p className="text-xs mb-4 text-on-surface-variant">
        Шаблоны задают стандартный маппинг для этого типа документа (фильтрация/преобразование/
        сортировка — на уровне источника данных, см. «Наборы данных»).
        При добавлении источника можно применить шаблон — маппинг заполнится автоматически.
      </p>

      {/* Список */}
      <div className="rounded-xl overflow-hidden mb-4 border border-outline-variant bg-surface">
        {isLoading ? (
          <div className="p-6 text-center text-sm text-on-surface-variant">Загрузка...</div>
        ) : templates.length === 0 && !adding ? (
          <div className="p-6 text-center text-sm text-on-surface-variant">
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
        <div className="rounded-xl p-4 border border-outline-variant bg-surface-container-low">
          <p className="text-xs font-semibold mb-3 text-on-surface-variant">Новый шаблон</p>
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
          className="flex items-center gap-2 text-sm font-medium px-3 py-2 rounded-md text-on-primary-container bg-primary-container"
        >
          <Icon icon={Add} size={18} /> Добавить шаблон
        </button>
      )}
    </Modal>
  );
}
