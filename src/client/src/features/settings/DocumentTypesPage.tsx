import { useState } from 'react';
import {
  Plus, ChevronDown, ChevronUp, Trash2,
  Braces, Ban, RotateCcw, Layers, Code, Database, Cpu,
} from 'lucide-react';
import { BindingTemplatesDialog } from './BindingTemplatesDialog';
import { Modal } from '@/shared/ui/Modal';
import {
  useListDocumentTypes,
  useCreateDocumentType,
  useUpdateDocumentType,
  useUpdateDocumentTypeSchema,
  useDeleteDocumentType,
  useSetDocumentTypeAbstract,
  useSetDocumentTypeGroup,
} from '@/shared/api/documentTypes';
import { TypeGroupAccordion, GroupPicker } from './TypeGroupAccordion';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import type { DocumentType, DocumentTypeKind } from '@/shared/api/types';
import {
  parseSchemaFields,
  resolveEffectiveFields,
  type SchemaField,
  type SchemaDefinition,
  type FieldGroup,
  type TypstRender,
} from '@/shared/api/schema';
import { TypstRendersEditor } from './TypstRendersEditor';
import { schemaToJson, validateFields, TYPE_LABELS, toCamelKey } from './schemaConstants';
import { useTagRegistry, typeTags as typeTagDefs } from '@/shared/api/tags';
import { GroupEditor } from './GroupEditor';
import { JsonPreview, FieldBuilder, DefaultValueCell } from './FieldBuilder';

function InheritedFieldsPanel({
  parentEffectiveFields, excludedFields, fieldOverrides, compositeTypes,
  onExclude, onInclude, onOverrideRequired, onOverrideDefaultValue, onResetOverride,
}: {
  parentEffectiveFields: SchemaField[];
  excludedFields: string[];
  fieldOverrides: Record<string, { required?: boolean; defaultValue?: unknown }>;
  compositeTypes: DocumentType[];
  onExclude: (key: string) => void;
  onInclude: (key: string) => void;
  onOverrideRequired: (key: string, required: boolean) => void;
  onOverrideDefaultValue: (key: string, value: unknown) => void;
  onResetOverride: (key: string) => void;
}) {
  const excludedSet = new Set(excludedFields);

  if (parentEffectiveFields.length === 0) {
    return <p className="text-xs text-fg4 py-1">Родительский тип не содержит полей.</p>;
  }

  function fieldTypeLabel(f: SchemaField) {
    if (f.type === 'complex' || f.type === 'array') {
      const ct = compositeTypes.find(c => c.id === f.typeId);
      return ct ? ct.name : (f.type === 'array' ? 'Массив' : 'Составной');
    }
    return TYPE_LABELS[f.type] ?? f.type;
  }

  return (
    <div className="space-y-1.5">
      <div className="grid grid-cols-[1fr_1fr_100px_70px_80px_120px_56px] gap-2 px-2 pb-1">
        <span className="text-xs font-medium text-fg3">Ключ</span>
        <span className="text-xs font-medium text-fg3">Название</span>
        <span className="text-xs font-medium text-fg3">Тип</span>
        <span className="text-xs font-medium text-fg3">Обязат.</span>
        <span className="text-xs font-medium text-fg3">Переопр.</span>
        <span className="text-xs font-medium text-fg3">Дефолт</span>
        <span className="text-xs font-medium text-fg3">Искл.</span>
      </div>
      {parentEffectiveFields.map(field => {
        const isExcluded = excludedSet.has(field.key);
        const override = fieldOverrides[field.key];
        const effectiveRequired = override?.required !== undefined ? override.required : field.required;

        return (
          <div
            key={field.key}
            className={`grid grid-cols-[1fr_1fr_100px_70px_80px_120px_56px] gap-2 items-center rounded-md px-2 py-1.5 ${
              isExcluded ? 'bg-danger-subtle opacity-60' : 'bg-base'
            }`}
          >
            <span className={`text-sm font-mono ${isExcluded ? 'line-through text-fg4' : 'text-fg2'}`}>
              {field.key}
            </span>
            <span className={`text-sm ${isExcluded ? 'line-through text-fg4' : 'text-fg2'}`}>
              {field.title}
            </span>
            <span className="text-xs text-fg4 truncate">{fieldTypeLabel(field)}</span>
            <span className={`text-xs font-medium ${effectiveRequired ? 'text-danger' : 'text-fg4'}`}>
              {effectiveRequired ? 'обязат.' : 'опц.'}
            </span>
            {!isExcluded ? (
              <div className="flex items-center gap-1">
                {override?.required !== undefined ? (
                  <div className="flex items-center gap-1">
                    <span className="text-xs text-brand">{override.required ? 'обяз.✎' : 'опц.✎'}</span>
                    <button type="button" onClick={() => onResetOverride(field.key)}
                      className="p-0.5 text-fg4 hover:text-fg2" title="Сбросить">
                      <RotateCcw size={11} />
                    </button>
                  </div>
                ) : (
                  <button type="button"
                    onClick={() => onOverrideRequired(field.key, !field.required)}
                    className="text-xs text-fg4 hover:text-brand px-1 py-0.5 rounded hover:bg-brand-subtle">
                    → {field.required ? 'опц.' : 'обяз.'}
                  </button>
                )}
              </div>
            ) : <span />}
            {!isExcluded
              ? <DefaultValueCell field={field} override={override} onOverrideDefaultValue={onOverrideDefaultValue} />
              : <span />
            }
            {isExcluded ? (
              <button type="button" onClick={() => onInclude(field.key)}
                className="flex items-center gap-1 text-xs text-success hover:text-success px-1 py-0.5 rounded hover:bg-success-subtle">
                <RotateCcw size={11} /> Вкл.
              </button>
            ) : (
              <button type="button" onClick={() => onExclude(field.key)}
                className="flex items-center gap-1 text-xs text-danger hover:text-danger px-1 py-0.5 rounded hover:bg-danger-subtle">
                <Ban size={11} /> Искл.
              </button>
            )}
          </div>
        );
      })}
    </div>
  );
}

// ─── Properties editor ─────────────────────────────────────────────────────────

function getDescendantIds(id: string, allDocTypes: DocumentType[]): Set<string> {
  const result = new Set<string>();
  const stack = [id];
  while (stack.length > 0) {
    const curr = stack.pop()!;
    for (const dt of allDocTypes) {
      if (dt.parentId === curr && !result.has(dt.id)) {
        result.add(dt.id);
        stack.push(dt.id);
      }
    }
  }
  return result;
}

function PropertiesEditor({ docType, allDocTypes }: { docType: DocumentType; allDocTypes: DocumentType[] }) {
  const [name, setName] = useState(docType.name);
  const [code, setCode] = useState(docType.code);
  const [parentId, setParentId] = useState(docType.parentId ?? '');
  const [error, setError] = useState('');
  const [saved, setSaved] = useState(false);
  const mutation = useUpdateDocumentType();

  const descendantIds = getDescendantIds(docType.id, allDocTypes);
  const eligibleParents = allDocTypes.filter(
    dt => dt.kind === docType.kind && dt.id !== docType.id && !descendantIds.has(dt.id),
  );

  const dirty = name !== docType.name || code !== docType.code || parentId !== (docType.parentId ?? '');

  // См. FieldBuilder.updateTitle — тот же принцип: код перегенерируется, пока совпадает
  // с авто-значением текущего названия (или пуст); ручная правка кода отключает автогенерацию.
  function handleNameChange(v: string) {
    const isCodeAuto = !code.trim() || code === toCamelKey(name);
    setName(v);
    setSaved(false);
    if (isCodeAuto) setCode(toCamelKey(v));
  }

  async function handleSave(e: React.FormEvent) {
    e.preventDefault();
    if (!name.trim() || !code.trim()) { setError('Наименование и код обязательны'); return; }
    setError('');
    try {
      await mutation.mutateAsync({ id: docType.id, name: name.trim(), code: code.trim(), parentId: parentId || null });
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка сохранения');
    }
  }

  return (
    <form onSubmit={handleSave} className="space-y-3 pb-4 border-b border-stroke mb-4">
      <p className="text-xs font-medium text-fg3 uppercase tracking-wide">Параметры типа</p>
      <div className="grid grid-cols-2 gap-3">
        <div>
          <label className="block text-xs font-medium text-fg2 mb-1">Наименование</label>
          <input value={name} onChange={e => handleNameChange(e.target.value)} required
            className="w-full border border-stroke-strong rounded-md px-3 py-1.5 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface" />
        </div>
        <div>
          <label className="block text-xs font-medium text-fg2 mb-1">Код</label>
          <input value={code} onChange={e => { setCode(e.target.value); setSaved(false); }} required spellCheck={false}
            className="w-full border border-stroke-strong rounded-md px-3 py-1.5 text-sm font-mono focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface" />
        </div>
      </div>
      <div>
        <label className="block text-xs font-medium text-fg2 mb-1">Родительский тип</label>
        <select value={parentId} onChange={e => { setParentId(e.target.value); setSaved(false); }}
          className="w-full border border-stroke-strong rounded-md px-3 py-1.5 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface">
          <option value="">— без родителя —</option>
          {eligibleParents.map(dt => (
            <option key={dt.id} value={dt.id}>{dt.name} ({dt.code})</option>
          ))}
        </select>
      </div>
      {error && <p className="text-xs text-danger">{error}</p>}
      <div className="flex items-center gap-3">
        <button type="submit" disabled={!dirty || mutation.isPending}
          className="px-3 py-1.5 text-xs bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-40 transition-colors">
          {mutation.isPending ? 'Сохранение...' : 'Сохранить параметры'}
        </button>
        {saved && <span className="text-xs text-success">Сохранено</span>}
        {dirty && !saved && <span className="text-xs text-warning">Есть несохранённые изменения</span>}
      </div>
    </form>
  );
}

// ─── Create form ───────────────────────────────────────────────────────────────

function CreateForm({
  kind, onClose, allDocTypes,
}: {
  kind: DocumentTypeKind;
  onClose: () => void;
  allDocTypes: DocumentType[];
}) {
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const [name, setName] = useState('');
  const [code, setCode] = useState('');
  const [parentId, setParentId] = useState('');
  const [isAbstract, setIsAbstract] = useState(false);
  const [fields, setFields] = useState<SchemaField[]>([]);
  const [showJson, setShowJson] = useState(false);
  const [error, setError] = useState('');
  const mutation = useCreateDocumentType();

  function handleNameChange(v: string) {
    const isCodeAuto = !code.trim() || code === toCamelKey(name);
    setName(v);
    if (isCodeAuto) setCode(toCamelKey(v));
  }

  const sameKindTypes = allDocTypes.filter(dt => dt.kind === kind);
  const compositeTypes = allDocTypes.filter(dt => dt.kind === 'Composite');
  const parentType = sameKindTypes.find(dt => dt.id === parentId) ?? null;
  const parentEffectiveFields = parentType ? resolveEffectiveFields(parentType, allDocTypes) : [];
  const inheritedKeys = new Set(parentEffectiveFields.map(f => f.key));

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    const fieldError = validateFields(fields);
    if (fieldError) { setError(fieldError); return; }
    const conflict = fields.find(f => inheritedKeys.has(f.key.trim()));
    if (conflict) { setError(`Ключ "${conflict.key}" уже есть в родительском типе`); return; }
    try {
      await mutation.mutateAsync({
        name, code, kind,
        parentId: parentId || null,
        schema: schemaToJson(fields, [], {}),
        isAbstract: kind === 'Document' ? isAbstract : false,
      });
      onClose();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка сохранения');
    }
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col min-h-0 flex-1">
      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4 space-y-5">
      <div className="grid grid-cols-2 gap-4">
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Наименование</label>
          <input value={name} onChange={e => handleNameChange(e.target.value)} required
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface" />
        </div>
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Код</label>
          <input value={code} onChange={e => setCode(e.target.value)} required spellCheck={false}
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm font-mono focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface" />
        </div>
      </div>

      {kind === 'Document' && (
        <label className="flex items-center gap-2.5 cursor-pointer select-none">
          <input type="checkbox" checked={isAbstract} onChange={e => setIsAbstract(e.target.checked)}
            className="w-4 h-4 rounded border-stroke-strong text-brand" />
          <span className="text-sm font-medium text-fg2">Абстрактный тип</span>
          <span className="text-xs text-fg4">(нельзя добавить в комплект напрямую)</span>
        </label>
      )}

      {sameKindTypes.length > 0 && (
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">
            Родительский тип (наследование)
          </label>
          <select value={parentId} onChange={e => setParentId(e.target.value)}
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface">
            <option value="">— без родителя —</option>
            {sameKindTypes.map(dt => (
              <option key={dt.id} value={dt.id}>{dt.name} ({dt.code})</option>
            ))}
          </select>
        </div>
      )}

      {parentEffectiveFields.length > 0 && (
        <div>
          <p className="text-xs font-medium text-fg3 mb-2 uppercase tracking-wide">
            Наследуемые поля от «{parentType?.name}» ({parentEffectiveFields.length})
          </p>
          <div className="border border-stroke rounded-lg bg-base px-3 py-2 space-y-1">
            {parentEffectiveFields.map(f => (
              <div key={f.key} className="flex items-center gap-3 text-xs text-fg3">
                <span className="font-mono text-fg2 w-36 truncate">{f.key}</span>
                <span className="flex-1 truncate">{f.title}</span>
                <span className="text-fg4">
                  {f.type === 'complex'
                    ? (compositeTypes.find(c => c.id === f.typeId)?.name ?? 'Составной')
                    : (TYPE_LABELS[f.type] ?? f.type)}
                </span>
                <span className={f.required ? 'text-danger' : 'text-stroke-strong'}>
                  {f.required ? 'обязат.' : 'опц.'}
                </span>
              </div>
            ))}
          </div>
          <p className="text-xs text-fg4 mt-1">
            Управление унаследованными полями — после создания типа.
          </p>
        </div>
      )}

      <div>
        <div className="flex items-center justify-between mb-3">
          <label className="text-sm font-medium text-fg2">
            {parentEffectiveFields.length > 0 ? 'Собственные поля' : 'Поля'}
          </label>
          {fields.length > 0 && (
            <button type="button" onClick={() => setShowJson(v => !v)}
              className={`flex items-center gap-1.5 text-xs px-2 py-1 rounded ${
                showJson ? 'bg-fg1 text-muted' : 'text-fg3 hover:text-fg1 hover:bg-muted'
              }`}>
              <Braces size={12} /> JSON
            </button>
          )}
        </div>
        {showJson
          ? <JsonPreview fields={fields} groups={[]} excludedFields={[]} fieldOverrides={{}} />
          : <FieldBuilder fields={fields} onChange={setFields} disabledKeys={inheritedKeys} compositeTypes={compositeTypes} primitiveTypes={primitiveTypes} allDocTypes={allDocTypes} />}
      </div>

      {error && <p className="text-sm text-danger">{error}</p>}
      </div>
      <div className="shrink-0 px-6 py-3 border-t border-stroke flex justify-end gap-3">
        <button type="button" onClick={onClose}
          className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">
          Отмена
        </button>
        <button type="submit" disabled={mutation.isPending}
          className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
          {mutation.isPending ? 'Создание...' : 'Создать'}
        </button>
      </div>
    </form>
  );
}

// ─── Schema editor (inline) ────────────────────────────────────────────────────

function SchemaEditor({ docType, allDocTypes }: {
  docType: DocumentType;
  allDocTypes: DocumentType[];
}) {
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const schemaDef = docType.schema as unknown as SchemaDefinition;
  const [fields, setFields] = useState<SchemaField[]>(() => parseSchemaFields(docType.schema));
  const [groups, setGroups] = useState<FieldGroup[]>(() => schemaDef.groups ?? []);
  const [excludedFields, setExcludedFields] = useState<string[]>(() => schemaDef.excludedFields ?? []);
  const [fieldOverrides, setFieldOverrides] = useState<Record<string, { required?: boolean; defaultValue?: unknown }>>(
    () => schemaDef.fieldOverrides ?? {},
  );
  const [typstRenders, setTypstRenders] = useState<TypstRender[]>(() => schemaDef.typstRenders ?? []);
  const [docTypeTags, setDocTypeTags] = useState<string[]>(() => schemaDef.tags ?? []);
  const { data: tagRegistry } = useTagRegistry();
  const applicableTypeTags = typeTagDefs(tagRegistry, docType.kind);
  const [showJson, setShowJson] = useState(false);
  const [showGroups, setShowGroups] = useState(groups.length > 0);
  const [showTypstRenders, setShowTypstRenders] = useState(typstRenders.length > 0);
  const [error, setError] = useState('');
  const [saved, setSaved] = useState(false);
  const mutation = useUpdateDocumentTypeSchema();

  const compositeTypes = allDocTypes.filter(dt => dt.kind === 'Composite');
  const parentType = docType.parentId ? allDocTypes.find(dt => dt.id === docType.parentId) ?? null : null;
  const parentEffectiveFields = parentType ? resolveEffectiveFields(parentType, allDocTypes) : [];
  const inheritedKeys = new Set(parentEffectiveFields.map(f => f.key));
  const effectiveFields = resolveEffectiveFields(docType, allDocTypes);

  const handleExclude = (key: string) => {
    setExcludedFields(prev => [...prev.filter(k => k !== key), key]);
    setFieldOverrides(prev => { const n = { ...prev }; delete n[key]; return n; });
    setSaved(false);
  };
  const handleInclude = (key: string) => { setExcludedFields(prev => prev.filter(k => k !== key)); setSaved(false); };
  const handleOverrideRequired = (key: string, required: boolean) => {
    setFieldOverrides(prev => ({ ...prev, [key]: { ...prev[key], required } })); setSaved(false);
  };
  const handleOverrideDefaultValue = (key: string, value: unknown) => {
    setFieldOverrides(prev => {
      const cur = prev[key] ?? {};
      if (value === undefined) {
        const { defaultValue: _, ...rest } = cur as { required?: boolean; defaultValue?: unknown };
        return Object.keys(rest).length ? { ...prev, [key]: rest } : { ...prev, [key]: rest };
      }
      return { ...prev, [key]: { ...cur, defaultValue: value } };
    }); setSaved(false);
  };
  const handleResetOverride = (key: string) => {
    setFieldOverrides(prev => { const n = { ...prev }; delete n[key]; return n; }); setSaved(false);
  };

  async function handleSave() {
    setError(''); setSaved(false);
    const fieldError = validateFields(fields);
    if (fieldError) { setError(fieldError); return; }
    const conflict = fields.find(f => inheritedKeys.has(f.key.trim()));
    if (conflict) { setError(`Ключ "${conflict.key}" уже есть в родительском типе`); return; }

    // Проверка уникальности fnName Typst-блоков в рамках всей системы
    const definedFnNames = typstRenders.map(r => r.fnName.trim()).filter(Boolean);
    const localDup = definedFnNames.find((n, i) => definedFnNames.indexOf(n) !== i);
    if (localDup) { setError(`Имя функции "${localDup}" задано дважды`); return; }

    const foreignFnNames = new Set<string>();
    for (const dt of allDocTypes) {
      if (dt.id === docType.id) continue;
      const def = dt.schema as unknown as SchemaDefinition;
      for (const r of def.typstRenders ?? []) {
        if (r.fnName) foreignFnNames.add(r.fnName.trim());
      }
    }
    const crossDup = definedFnNames.find(n => foreignFnNames.has(n));
    if (crossDup) { setError(`Имя функции "${crossDup}" уже используется в другом типе`); return; }

    try {
      await mutation.mutateAsync({ id: docType.id, schema: schemaToJson(fields, excludedFields, fieldOverrides, groups, typstRenders, docTypeTags) });
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка сохранения');
    }
  }

  return (
    <div className="space-y-4">
      {parentType && (
        <div>
          <div className="flex items-center gap-2 mb-2">
            <p className="text-xs font-medium text-fg3 uppercase tracking-wide">
              Унаследовано от «{parentType.name}»
            </p>
            {excludedFields.length > 0 && (
              <span className="text-xs text-fg4">(исключено: {excludedFields.length})</span>
            )}
          </div>
          <InheritedFieldsPanel
            parentEffectiveFields={parentEffectiveFields}
            excludedFields={excludedFields}
            fieldOverrides={fieldOverrides}
            compositeTypes={compositeTypes}
            onExclude={handleExclude}
            onInclude={handleInclude}
            onOverrideRequired={handleOverrideRequired}
            onOverrideDefaultValue={handleOverrideDefaultValue}
            onResetOverride={handleResetOverride}
          />
        </div>
      )}

      <div>
        {parentType && (
          <p className="text-xs font-medium text-fg3 uppercase tracking-wide mb-2">
            Собственные поля
          </p>
        )}
        <div className="flex justify-end mb-2">
          {(fields.length > 0 || parentEffectiveFields.length > 0) && (
            <button type="button" onClick={() => setShowJson(v => !v)}
              className={`flex items-center gap-1.5 text-xs px-2 py-1 rounded ${
                showJson ? 'bg-fg1 text-muted' : 'text-fg3 hover:text-fg1 hover:bg-muted'
              }`}>
              <Braces size={12} /> JSON
            </button>
          )}
        </div>
        {showJson
          ? <JsonPreview fields={fields} groups={groups} excludedFields={excludedFields} fieldOverrides={fieldOverrides} />
          : <FieldBuilder fields={fields} onChange={f => { setFields(f); setSaved(false); }}
              disabledKeys={inheritedKeys} compositeTypes={compositeTypes} primitiveTypes={primitiveTypes} allDocTypes={allDocTypes} />}
      </div>

      {!showJson && effectiveFields.length > 0 && (
        <div className="border-t border-stroke pt-4">
          <button type="button"
            onClick={() => setShowGroups(v => !v)}
            className="flex items-center gap-2 text-xs font-medium text-fg3 hover:text-fg1 uppercase tracking-wide">
            <Layers size={12} />
            Группировка полей
            {groups.length > 0 && <span className="text-brand">({groups.length})</span>}
            {showGroups ? <ChevronUp size={12} /> : <ChevronDown size={12} />}
          </button>
          {showGroups && (
            <div className="mt-3">
              <GroupEditor
                groups={groups}
                effectiveFields={effectiveFields}
                onChange={g => { setGroups(g); setSaved(false); }}
              />
            </div>
          )}
        </div>
      )}

      {!showJson && applicableTypeTags.length > 0 && (
        <div className="border-t border-stroke pt-4">
          <div className="flex items-center gap-2 mb-2 text-xs font-medium text-fg3 uppercase tracking-wide">
            <Cpu size={12} /> Функциональные тэги типа
          </div>
          <div className="flex flex-wrap gap-1.5">
            {applicableTypeTags.map(t => {
              const on = docTypeTags.includes(t.code);
              return (
                <button
                  key={t.code}
                  type="button"
                  title={t.description}
                  onClick={() => { setDocTypeTags(prev => on ? prev.filter(c => c !== t.code) : [...prev, t.code]); setSaved(false); }}
                  className={`px-2.5 py-1 rounded-full text-xs border transition-colors ${
                    on ? 'bg-purple-500/15 border-purple-400 text-purple-700' : 'border-stroke text-fg4 hover:border-stroke-strong hover:text-fg2'
                  }`}
                >
                  {t.label}
                </button>
              );
            })}
          </div>
        </div>
      )}

      {!showJson && (docType.kind === 'Composite' || docType.kind === 'Document') && (
        <div className="border-t border-stroke pt-4">
          <button type="button"
            onClick={() => setShowTypstRenders(v => !v)}
            className="flex items-center gap-2 text-xs font-medium text-fg3 hover:text-fg1 uppercase tracking-wide">
            <Code size={12} />
            Typst-блоки (варианты отображения)
            {typstRenders.length > 0 && <span className="text-purple-600">({typstRenders.length})</span>}
            {showTypstRenders ? <ChevronUp size={12} /> : <ChevronDown size={12} />}
          </button>
          {showTypstRenders && (
            <div className="mt-3">
              <TypstRendersEditor
                renders={typstRenders}
                onChange={r => { setTypstRenders(r); setSaved(false); }}
                fields={effectiveFields}
                allDocTypes={allDocTypes}
              />
            </div>
          )}
        </div>
      )}

      {!showJson && (
        <>
          {error && <p className="text-xs text-danger">{error}</p>}
          <div className="flex items-center gap-3 pt-1">
            <button onClick={handleSave} disabled={mutation.isPending}
              className="px-3 py-1.5 text-xs bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
              {mutation.isPending ? 'Сохранение...' : 'Сохранить схему'}
            </button>
            {saved && <span className="text-xs text-success">Сохранено</span>}
          </div>
        </>
      )}
    </div>
  );
}

// ─── Type row ──────────────────────────────────────────────────────────────────

function TypeRow({ docType, allDocTypes, allGroups, expanded, onToggle }: {
  docType: DocumentType; allDocTypes: DocumentType[]; allGroups: string[];
  expanded: boolean; onToggle: () => void;
}) {
  const deleteMutation = useDeleteDocumentType();
  const abstractMutation = useSetDocumentTypeAbstract();
  const groupMutation = useSetDocumentTypeGroup();
  const [templatesOpen, setTemplatesOpen] = useState(false);

  const effectiveFields = resolveEffectiveFields(docType, allDocTypes);
  const ownFieldCount = parseSchemaFields(docType.schema).length;
  const parentType = docType.parentId ? allDocTypes.find(dt => dt.id === docType.parentId) : null;
  const hasChildren = allDocTypes.some(dt => dt.parentId === docType.id);
  const compositeTypes = allDocTypes.filter(dt => dt.kind === 'Composite');

  function getFieldTypeLabel(f: SchemaField) {
    if (f.type === 'complex') {
      const ct = compositeTypes.find(c => c.id === f.typeId);
      return ct ? `[${ct.name}]` : '[Составной]';
    }
    return TYPE_LABELS[f.type] ?? f.type;
  }

  function handleDelete(e: React.MouseEvent) {
    e.stopPropagation();
    if (hasChildren) { alert(`Тип «${docType.name}» является родительским — удаление невозможно.`); return; }
    if (!confirm(`Удалить тип «${docType.name}»? Это действие необратимо.`)) return;
    deleteMutation.mutate(docType.id);
  }

  const requiredCount = effectiveFields.filter(f => f.required).length;
  const complexFields = effectiveFields.filter(f => f.type === 'complex');

  return (
    <div className={`overflow-hidden group ${expanded ? 'bg-base' : ''}`}>
      <div className="flex items-center hover:bg-base transition-colors">
        <button onClick={onToggle}
          className="flex-1 min-w-0 flex items-center gap-2 px-4 py-2.5 text-left">
          {expanded
            ? <ChevronUp size={15} className="text-fg4 shrink-0" />
            : <ChevronDown size={15} className="text-fg4 shrink-0" />}
          <span className="text-sm font-medium text-fg1 shrink-0">{docType.name}</span>
          <span className="text-xs text-fg4 font-mono shrink-0">{docType.code}</span>
          {docType.isAbstract && (
            <span className="text-[11px] bg-orange-50 text-orange-600 border border-orange-200 px-1.5 py-0.5 rounded-full shrink-0">
              абстрактный
            </span>
          )}
          {parentType && (
            <span className="text-[11px] bg-brand-subtle text-brand px-1.5 py-0.5 rounded-full shrink-0 truncate max-w-[160px]">
              ↑ {parentType.name}
            </span>
          )}
          <span className="flex-1" />
          {effectiveFields.length > 0 && (
            <span className="text-xs text-fg4 shrink-0">
              {effectiveFields.length} {effectiveFields.length < 5 ? 'поля' : 'полей'}
              {parentType && ownFieldCount > 0 && ` (+${ownFieldCount})`}
            </span>
          )}
          {requiredCount > 0 && (
            <span className="text-xs text-danger shrink-0">{requiredCount} обяз.</span>
          )}
          {complexFields.length > 0 && (
            <span className="text-xs text-purple-500 shrink-0"
              title={complexFields.map(f => getFieldTypeLabel(f)).join(', ')}>
              {complexFields.length} сост.
            </span>
          )}
        </button>
        {docType.kind === 'Document' && (
          <>
            <button
              onClick={e => { e.stopPropagation(); setTemplatesOpen(true); }}
              className="px-2 py-3 opacity-0 group-hover:opacity-100 transition-all text-brand"
              title="Шаблоны данных"
            >
              <Database size={14} />
            </button>
            <button
              onClick={e => { e.stopPropagation(); abstractMutation.mutate({ id: docType.id, isAbstract: !docType.isAbstract }); }}
              disabled={abstractMutation.isPending}
              className={`px-2 py-3 text-xs font-medium opacity-0 group-hover:opacity-100 transition-all disabled:opacity-30 ${
                docType.isAbstract
                  ? 'text-warning hover:text-orange-700'
                  : 'text-stroke-strong hover:text-warning'
              }`}
              title={docType.isAbstract ? 'Снять абстрактность' : 'Сделать абстрактным'}>
              Абстр.
            </button>
          </>
        )}
        <span className="pr-1" onClick={e => e.stopPropagation()}>
          <GroupPicker groups={allGroups} value={docType.group}
            onChange={group => groupMutation.mutate({ id: docType.id, group })} />
        </span>
        <button onClick={handleDelete} disabled={deleteMutation.isPending}
          className="px-3 py-3 text-stroke-strong hover:text-danger opacity-0 group-hover:opacity-100 transition-all disabled:opacity-30"
          title={hasChildren ? 'Нельзя удалить: есть дочерние типы' : 'Удалить'}>
          <Trash2 size={14} />
        </button>
      </div>
      {expanded && (
        <div className="px-4 pb-5 pt-3 border-t border-stroke bg-base">
          <PropertiesEditor docType={docType} allDocTypes={allDocTypes} />
          <SchemaEditor docType={docType} allDocTypes={allDocTypes} />
        </div>
      )}
      {templatesOpen && (
        <BindingTemplatesDialog
          docType={docType}
          allDocTypes={allDocTypes}
          onClose={() => setTemplatesOpen(false)}
        />
      )}
    </div>
  );
}

// ─── Page (parameterised by kind) ──────────────────────────────────────────────

interface TypesPageProps {
  kind: DocumentTypeKind;
}

export function DocumentTypesPage({ kind }: TypesPageProps) {
  const [createOpen, setCreateOpen] = useState(false);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const { data: allDocTypes = [], isLoading } = useListDocumentTypes();

  const filtered = allDocTypes
    .filter(dt => dt.kind === kind)
    .sort((a, b) => a.name.localeCompare(b.name, 'ru'));
  const allGroups = [...new Set(filtered.map(dt => dt.group).filter((g): g is string => !!g))]
    .sort((a, b) => a.localeCompare(b, 'ru'));

  const title = kind === 'Document' ? 'Типы документов' : 'Составные типы';
  const addLabel = kind === 'Document' ? 'Добавить тип документа' : 'Добавить составной тип';

  function toggleExpanded(id: string) {
    setExpandedId(prev => prev === id ? null : id);
  }

  return (
    <div className="px-6 py-4">
      <div className="flex items-center justify-between mb-4">
        <div>
          <h1 className="text-xl font-semibold text-fg1">{title}</h1>
          {kind === 'Composite' && (
            <p className="text-xs text-fg3 mt-0.5">
              Переиспользуемые структуры полей для использования внутри типов документов
            </p>
          )}
        </div>
        <button onClick={() => setCreateOpen(true)}
          className="flex items-center gap-2 bg-brand hover:bg-brand-hover text-white text-sm font-medium px-4 py-2 rounded-md transition-colors">
          <Plus size={16} /> {addLabel}
        </button>
      </div>

      {isLoading ? (
        <div className="text-center text-fg4 text-sm py-10">Загрузка...</div>
      ) : filtered.length === 0 ? (
        <div className="text-center text-fg4 text-sm py-10">
          {kind === 'Document' ? 'Типов документов не создано' : 'Составных типов не создано'}
        </div>
      ) : (
        <TypeGroupAccordion items={filtered} getGroup={dt => dt.group} renderItem={dt => (
          <TypeRow key={dt.id} docType={dt} allDocTypes={allDocTypes} allGroups={allGroups}
            expanded={expandedId === dt.id} onToggle={() => toggleExpanded(dt.id)} />
        )} />
      )}

      <Modal open={createOpen} onOpenChange={setCreateOpen}
        title={kind === 'Document' ? 'Новый тип документа' : 'Новый составной тип'}
        wide flushBody>
        {createOpen && (
          <CreateForm kind={kind} onClose={() => setCreateOpen(false)} allDocTypes={allDocTypes} />
        )}
      </Modal>
    </div>
  );
}
