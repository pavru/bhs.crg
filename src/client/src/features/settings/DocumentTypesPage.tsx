import { useState } from 'react';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import {
  Plus, ChevronDown, ChevronUp, Trash2, Search, Folder, FileText, EyeOff, Check,
  Braces, RotateCcw, Layers, Code, Database, Cpu,
} from 'lucide-react';
import { Switch } from '@/shared/ui/Switch';
import { BindingTemplatesDialog } from './BindingTemplatesDialog';
import { Modal } from '@/shared/ui/Modal';
import { Button, IconButton } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { apiError } from '@/shared/utils/apiError';
import {
  useListDocumentTypes,
  useCreateDocumentType,
  useUpdateDocumentType,
  useUpdateDocumentTypeSchema,
  useDeleteDocumentType,
  useSetDocumentTypeAbstract,
  useSetDocumentTypeAllowsProxy,
  useSetDocumentTypeGroup,
} from '@/shared/api/documentTypes';
import { GroupPicker } from './TypeGroupAccordion';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import type { DocumentType, DocumentTypeKind, EnumTypeDef } from '@/shared/api/types';
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

/** Sentinel для «— без родителя —» — Radix Select запрещает пустую строку как value. */
const NO_PARENT = '__none__';

/** Свёрнутая MD3-карточка-секция (issue #197 Фаза C): заголовок с иконкой/счётчиком/chevron +
 *  раскрывающееся тело. Единый вид для «Группировка», «Тэги типа», «Typst-блоки». */
function SectionCard({ icon, title, count, countClass, open, onToggle, children }: {
  icon: React.ReactNode;
  title: string;
  count?: number;
  countClass?: string;
  open: boolean;
  onToggle: () => void;
  children: React.ReactNode;
}) {
  return (
    <div className="border border-stroke rounded-lg bg-surface overflow-hidden">
      <button type="button" onClick={onToggle}
        className="w-full flex items-center gap-2 px-3 py-2.5 hover:bg-muted/40 transition-colors">
        <span className="text-fg4 shrink-0">{icon}</span>
        <span className="text-sm font-medium text-fg2">{title}</span>
        {count != null && count > 0 && <span className={`text-xs ${countClass ?? 'text-brand'}`}>({count})</span>}
        <span className="flex-1" />
        {open ? <ChevronUp size={16} className="text-fg4 shrink-0" /> : <ChevronDown size={16} className="text-fg4 shrink-0" />}
      </button>
      {open && <div className="px-3 pb-3 pt-1 border-t border-stroke">{children}</div>}
    </div>
  );
}

function InheritedFieldsPanel({
  parentEffectiveFields, excludedFields, fieldOverrides, compositeTypes, enumTypes,
  onExclude, onInclude, onOverrideRequired, onOverrideDefaultValue, onResetOverride,
}: {
  parentEffectiveFields: SchemaField[];
  excludedFields: string[];
  fieldOverrides: Record<string, { required?: boolean; defaultValue?: unknown }>;
  compositeTypes: DocumentType[];
  enumTypes: EnumTypeDef[];
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

  const cols = 'grid grid-cols-[1fr_1fr_110px_160px_120px_64px] gap-2 items-center';
  return (
    <div className="space-y-0.5">
      <div className={`${cols} px-2 pb-1`}>
        <span className="text-xs font-medium text-fg3">Ключ</span>
        <span className="text-xs font-medium text-fg3">Название</span>
        <span className="text-xs font-medium text-fg3">Тип</span>
        <span className="text-xs font-medium text-fg3">Обязательность</span>
        <span className="text-xs font-medium text-fg3">Дефолт</span>
        <span className="text-xs font-medium text-fg3 text-center">Вкл.</span>
      </div>
      {parentEffectiveFields.map(field => {
        const isExcluded = excludedSet.has(field.key);
        const override = fieldOverrides[field.key];
        return (
          <div key={field.key} className={`${cols} rounded-md px-2 py-2 hover:bg-muted/50 transition-colors ${isExcluded ? 'opacity-55' : ''}`}>
            <span className="flex items-center gap-1.5 min-w-0">
              {isExcluded && <EyeOff size={14} className="text-fg4 shrink-0" />}
              <span className={`text-sm font-mono truncate ${isExcluded ? 'line-through text-fg4' : 'text-fg2'}`}>{field.key}</span>
            </span>
            <span className="text-sm text-fg2 truncate">{field.title}</span>
            <span className="text-xs text-fg4 truncate">{fieldTypeLabel(field)}</span>
            {isExcluded
              ? <span className="text-xs text-fg4">—</span>
              : <RequiredChip field={field} override={override}
                  onOverride={r => onOverrideRequired(field.key, r)} onReset={() => onResetOverride(field.key)} />}
            {isExcluded
              ? <span />
              : <DefaultValueCell field={field} override={override} enumTypes={enumTypes} onOverrideDefaultValue={onOverrideDefaultValue} />}
            <div className="flex justify-center">
              <Switch size="sm" checked={!isExcluded}
                onChange={on => on ? onInclude(field.key) : onExclude(field.key)}
                title={isExcluded ? 'Включить поле' : 'Исключить поле'} label={`Поле ${field.key}: включено`} />
            </div>
          </div>
        );
      })}
    </div>
  );
}

/** Интерактивный chip обязательности унаследованного поля (issue #197): меню как-у-родителя/обяз/опц/сброс. */
function RequiredChip({ field, override, onOverride, onReset }: {
  field: SchemaField;
  override?: { required?: boolean };
  onOverride: (required: boolean) => void;
  onReset: () => void;
}) {
  const overridden = override?.required !== undefined;
  const effective = overridden ? override!.required! : field.required;
  const parentLabel = field.required ? 'обяз.' : 'опц.';
  return (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger asChild>
        <button type="button"
          className={`inline-flex items-center gap-1.5 text-xs px-2 py-1 rounded-full transition-colors ${
            overridden ? 'bg-brand-subtle text-brand font-medium' : 'text-fg3 hover:bg-muted'}`}>
          {overridden && <span className="w-1.5 h-1.5 rounded-full bg-brand shrink-0" />}
          {overridden ? `${parentLabel} → ${effective ? 'обяз.' : 'опц.'}` : (effective ? 'обяз.' : 'опц.')}
        </button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="start" sideOffset={4}
          className="z-50 min-w-[210px] rounded-xl border border-stroke bg-surface p-1 text-sm text-fg1"
          style={{ boxShadow: 'var(--f-shadow16)' }}>
          <ReqItem onSelect={onReset} active={!overridden}>Как у родителя ({parentLabel})</ReqItem>
          <ReqItem onSelect={() => onOverride(true)} active={overridden && effective}>Обязательное</ReqItem>
          <ReqItem onSelect={() => onOverride(false)} active={overridden && !effective}>Опциональное</ReqItem>
          {overridden && (
            <>
              <DropdownMenu.Separator className="my-1 h-px bg-stroke" />
              <ReqItem onSelect={onReset}><RotateCcw size={13} className="text-fg4" /> Сбросить переопределение</ReqItem>
            </>
          )}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}

function ReqItem({ children, onSelect, active }: { children: React.ReactNode; onSelect: () => void; active?: boolean }) {
  return (
    <DropdownMenu.Item onSelect={onSelect}
      className="flex items-center gap-2 px-2.5 py-1.5 rounded-lg cursor-pointer outline-none data-[highlighted]:bg-muted">
      <Check size={14} className={active ? 'text-brand' : 'invisible'} />
      <span className="flex-1">{children}</span>
    </DropdownMenu.Item>
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
  const abstractMutation = useSetDocumentTypeAbstract();
  const proxyMutation = useSetDocumentTypeAllowsProxy();

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
        <TextField label="Наименование" value={name} onChange={e => handleNameChange(e.target.value)} required />
        <TextField label="Код" value={code} onChange={e => { setCode(e.target.value); setSaved(false); }}
          required spellCheck={false} className="font-mono" />
      </div>
      <div>
        <label className="block text-xs font-medium text-fg2 mb-1">Родительский тип</label>
        <Select value={parentId || NO_PARENT} aria-label="Родительский тип"
          onValueChange={v => { setParentId(v === NO_PARENT ? '' : v); setSaved(false); }}>
          <SelectItem value={NO_PARENT}>— без родителя —</SelectItem>
          {eligibleParents.map(dt => (
            <SelectItem key={dt.id} value={dt.id}>{dt.name} ({dt.code})</SelectItem>
          ))}
        </Select>
      </div>
      {/* Прокси/абстрактность — отдельные мгновенные переключатели (не часть формы «Сохранить
          параметры»): каждый — своя мутация, применяется сразу по щелчку (issue #197 Фаза C). */}
      <div className="flex flex-col gap-2 pt-1">
        <label className="flex items-center gap-2.5 select-none">
          <Switch checked={docType.allowsProxy} size="sm" label="Роль/прокси"
            disabled={proxyMutation.isPending}
            onChange={v => proxyMutation.mutate({ id: docType.id, allowsProxy: v })} />
          <span className="text-sm text-fg2">Роль/прокси</span>
          <span className="text-xs text-fg4">— тип может подменять другой при генерации</span>
        </label>
        {docType.kind === 'Document' && (
          <label className="flex items-center gap-2.5 select-none">
            <Switch checked={docType.isAbstract} size="sm" label="Абстрактный"
              disabled={abstractMutation.isPending}
              onChange={v => abstractMutation.mutate({ id: docType.id, isAbstract: v })} />
            <span className="text-sm text-fg2">Абстрактный</span>
            <span className="text-xs text-fg4">— нельзя добавить в комплект напрямую</span>
          </label>
        )}
      </div>
      {error && <p className="text-xs text-danger">{error}</p>}
      <div className="flex items-center gap-3">
        <Button type="submit" variant="filled" size="sm" disabled={!dirty} loading={mutation.isPending}>
          {mutation.isPending ? 'Сохранение…' : 'Сохранить параметры'}
        </Button>
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
  const { data: enumTypes = [] } = useListEnumTypes();
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
        <TextField label="Наименование" value={name} onChange={e => handleNameChange(e.target.value)} required />
        <TextField label="Код" value={code} onChange={e => setCode(e.target.value)}
          required spellCheck={false} className="font-mono" />
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
          <Select value={parentId || NO_PARENT} aria-label="Родительский тип"
            onValueChange={v => setParentId(v === NO_PARENT ? '' : v)}>
            <SelectItem value={NO_PARENT}>— без родителя —</SelectItem>
            {sameKindTypes.map(dt => (
              <SelectItem key={dt.id} value={dt.id}>{dt.name} ({dt.code})</SelectItem>
            ))}
          </Select>
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
          : <FieldBuilder fields={fields} onChange={setFields} disabledKeys={inheritedKeys} compositeTypes={compositeTypes} primitiveTypes={primitiveTypes} enumTypes={enumTypes} allDocTypes={allDocTypes} />}
      </div>

      {error && <p className="text-sm text-danger">{error}</p>}
      </div>
      <div className="shrink-0 px-6 py-3 border-t border-stroke flex justify-end gap-3">
        <Button type="button" variant="text" onClick={onClose}>Отмена</Button>
        <Button type="submit" variant="filled" loading={mutation.isPending}>
          {mutation.isPending ? 'Создание…' : 'Создать'}
        </Button>
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
  const { data: enumTypes = [] } = useListEnumTypes();
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
  const [showTypeTags, setShowTypeTags] = useState(false);
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
            enumTypes={enumTypes}
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
              disabledKeys={inheritedKeys} compositeTypes={compositeTypes} primitiveTypes={primitiveTypes} enumTypes={enumTypes} allDocTypes={allDocTypes} />}
      </div>

      {!showJson && effectiveFields.length > 0 && (
        <SectionCard icon={<Layers size={15} />} title="Группировка полей" count={groups.length}
          open={showGroups} onToggle={() => setShowGroups(v => !v)}>
          <GroupEditor
            groups={groups}
            effectiveFields={effectiveFields}
            onChange={g => { setGroups(g); setSaved(false); }}
          />
        </SectionCard>
      )}

      {!showJson && applicableTypeTags.length > 0 && (
        <SectionCard icon={<Cpu size={15} />} title="Функциональные тэги типа"
          count={docTypeTags.length} countClass="text-purple-600"
          open={showTypeTags} onToggle={() => setShowTypeTags(v => !v)}>
          <div className="flex flex-wrap gap-1.5 pt-2">
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
        </SectionCard>
      )}

      {!showJson && (docType.kind === 'Composite' || docType.kind === 'Document') && (
        <SectionCard icon={<Code size={15} />} title="Typst-блоки (варианты отображения)"
          count={typstRenders.length} countClass="text-purple-600"
          open={showTypstRenders} onToggle={() => setShowTypstRenders(v => !v)}>
          <div className="pt-2">
            <TypstRendersEditor
              renders={typstRenders}
              onChange={r => { setTypstRenders(r); setSaved(false); }}
              fields={effectiveFields}
              allDocTypes={allDocTypes}
            />
          </div>
        </SectionCard>
      )}

      {!showJson && (
        <>
          {error && <p className="text-xs text-danger">{error}</p>}
          <div className="flex items-center gap-3 pt-1">
            <Button variant="filled" size="sm" onClick={handleSave} loading={mutation.isPending}>
              {mutation.isPending ? 'Сохранение…' : 'Сохранить схему'}
            </Button>
            {saved && <span className="text-xs text-success">Сохранено</span>}
          </div>
        </>
      )}
    </div>
  );
}

// ─── Type row ──────────────────────────────────────────────────────────────────

/** Число эффективных полей типа — для счётчика в списке-пилюле (issue #197). */
function fieldCount(docType: DocumentType, allDocTypes: DocumentType[]): number {
  return resolveEffectiveFields(docType, allDocTypes).length;
}

/** Правая панель list-detail (issue #197 Фаза A): шапка типа (метрики+действия) + редактор как есть. */
function TypeDetail({ docType, allDocTypes, allGroups, onDeleted }: {
  docType: DocumentType; allDocTypes: DocumentType[]; allGroups: string[]; onDeleted: () => void;
}) {
  const deleteMutation = useDeleteDocumentType();
  const groupMutation = useSetDocumentTypeGroup();
  const [templatesOpen, setTemplatesOpen] = useState(false);
  const [deleteConfirmOpen, setDeleteConfirmOpen] = useState(false);

  const effectiveFields = resolveEffectiveFields(docType, allDocTypes);
  const ownFieldCount = parseSchemaFields(docType.schema).length;
  const parentType = docType.parentId ? allDocTypes.find(dt => dt.id === docType.parentId) : null;
  const hasChildren = allDocTypes.some(dt => dt.parentId === docType.id);
  const compositeTypes = allDocTypes.filter(dt => dt.kind === 'Composite');
  const requiredCount = effectiveFields.filter(f => f.required).length;
  const complexFields = effectiveFields.filter(f => f.type === 'complex');

  function getFieldTypeLabel(f: SchemaField) {
    if (f.type === 'complex') {
      const ct = compositeTypes.find(c => c.id === f.typeId);
      return ct ? `[${ct.name}]` : '[Составной]';
    }
    return TYPE_LABELS[f.type] ?? f.type;
  }

  const badge = 'text-xs px-2 py-0.5 rounded-full font-medium';
  return (
    <div className="flex flex-col min-h-0 flex-1">
      {/* Шапка типа */}
      <div className="shrink-0 px-6 py-4 border-b border-stroke bg-surface">
        <div className="flex items-start gap-3">
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2 flex-wrap">
              <h2 className="text-xl font-normal text-fg1 truncate">{docType.name}</h2>
              <span className="text-xs text-fg4 font-mono">{docType.code}</span>
              {parentType && (
                <span className={`${badge} bg-brand-subtle text-brand truncate max-w-[200px]`}>↑ {parentType.name}</span>
              )}
              {docType.isAbstract && <span className={`${badge} bg-warning-subtle text-warning`}>абстрактный</span>}
              {docType.allowsProxy && <span className={`${badge} bg-brand-subtle text-brand`}>роль/прокси</span>}
            </div>
            <div className="flex items-center gap-2 mt-2 flex-wrap">
              {effectiveFields.length > 0 && (
                <span className={`${badge} bg-muted text-fg3`}>
                  {effectiveFields.length} полей{parentType && ownFieldCount > 0 ? ` · ${ownFieldCount} своих` : ''}
                </span>
              )}
              {requiredCount > 0 && <span className={`${badge} bg-muted text-fg3`}>{requiredCount} обязательных</span>}
              {complexFields.length > 0 && (
                <span className={`${badge} bg-muted text-fg3`} title={complexFields.map(getFieldTypeLabel).join(', ')}>
                  {complexFields.length} составных
                </span>
              )}
            </div>
          </div>
          {/* Действия типа (прокси/абстрактность перенесены в «Параметры типа» как switch'и — #197 Фаза C) */}
          <div className="flex items-center gap-1 shrink-0">
            {docType.kind === 'Document' && (
              <IconButton label="Шаблоны данных" size="sm" onClick={() => setTemplatesOpen(true)} title="Шаблоны данных">
                <Database size={15} />
              </IconButton>
            )}
            <GroupPicker groups={allGroups} value={docType.group}
              onChange={group => groupMutation.mutate({ id: docType.id, group })} />
            <IconButton label="Удалить тип" size="sm" danger
              onClick={() => { if (!hasChildren) setDeleteConfirmOpen(true); }}
              disabled={deleteMutation.isPending || hasChildren}
              title={hasChildren ? 'Нельзя удалить: есть дочерние типы' : 'Удалить тип'}>
              <Trash2 size={15} />
            </IconButton>
          </div>
        </div>
      </div>
      {/* Тело редактора (существующие редакторы как есть — Фаза A) */}
      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-5">
        <div className="mx-auto max-w-4xl">
          <PropertiesEditor docType={docType} allDocTypes={allDocTypes} />
          <SchemaEditor docType={docType} allDocTypes={allDocTypes} />
        </div>
      </div>
      {templatesOpen && (
        <BindingTemplatesDialog docType={docType} allDocTypes={allDocTypes} onClose={() => setTemplatesOpen(false)} />
      )}
      <ConfirmDialog
        open={deleteConfirmOpen}
        onOpenChange={setDeleteConfirmOpen}
        title={`Удалить тип «${docType.name}»?`}
        description={<p>Это повлияет на все документы и шаблоны, использующие этот тип. Действие необратимо.</p>}
        confirmLabel={`Удалить тип «${docType.name}»`}
        requireCheckbox="Понимаю, что это необратимо"
        onConfirm={() => deleteMutation.mutate(docType.id, {
          onSuccess: onDeleted,
          onError: err => alert(apiError(err, 'Не удалось удалить тип.')),
        })}
      />
    </div>
  );
}

// ─── Page (parameterised by kind) ──────────────────────────────────────────────

interface TypesPageProps {
  kind: DocumentTypeKind;
}

export function DocumentTypesPage({ kind }: TypesPageProps) {
  const [createOpen, setCreateOpen] = useState(false);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const { data: allDocTypes = [], isLoading } = useListDocumentTypes();

  const filtered = allDocTypes
    .filter(dt => dt.kind === kind)
    .sort((a, b) => a.name.localeCompare(b.name, 'ru'));
  const allGroups = [...new Set(filtered.map(dt => dt.group).filter((g): g is string => !!g))]
    .sort((a, b) => a.localeCompare(b, 'ru'));

  const title = kind === 'Document' ? 'Типы документов' : 'Составные типы';
  const addLabel = kind === 'Document' ? 'Добавить тип документа' : 'Добавить составной тип';

  // Поиск по левому списку + группировка (пустая группа — первой).
  const q = query.trim().toLowerCase();
  const listed = q ? filtered.filter(t => `${t.name} ${t.code}`.toLowerCase().includes(q)) : filtered;
  const groupOrder: string[] = [];
  const byGroup = new Map<string, DocumentType[]>();
  for (const t of listed) {
    const g = t.group ?? '';
    if (!byGroup.has(g)) { byGroup.set(g, []); groupOrder.push(g); }
    byGroup.get(g)!.push(t);
  }
  groupOrder.sort((a, b) => a === '' ? -1 : b === '' ? 1 : a.localeCompare(b, 'ru'));

  // Выбранный тип: из выбора (если ещё в отфильтрованных) иначе первый.
  const selected = filtered.find(t => t.id === selectedId) ?? filtered[0];

  return (
    <div className="h-full flex flex-col">
      <div className="flex items-center justify-between gap-3 px-6 py-3 shrink-0 border-b border-stroke">
        <div className="min-w-0">
          <h1 className="text-xl font-semibold text-fg1">{title}</h1>
          {kind === 'Composite' && (
            <p className="text-xs text-fg3 mt-0.5">
              Переиспользуемые структуры полей для использования внутри типов документов
            </p>
          )}
        </div>
        <Button variant="filled" icon={<Plus size={16} />} onClick={() => setCreateOpen(true)}>
          {addLabel}
        </Button>
      </div>

      {isLoading ? (
        <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Загрузка...</div>
      ) : filtered.length === 0 ? (
        <div className="flex-1 flex items-center justify-center text-fg4 text-sm">
          {kind === 'Document' ? 'Типов документов не создано' : 'Составных типов не создано'}
        </div>
      ) : (
        <div className="flex-1 min-h-0 flex">
          <TypeListPanel
            groupOrder={groupOrder} byGroup={byGroup} allDocTypes={allDocTypes}
            selectedId={selected?.id ?? null} onSelect={setSelectedId}
            query={query} onQuery={setQuery} />
          {selected ? (
            <TypeDetail key={selected.id} docType={selected} allDocTypes={allDocTypes}
              allGroups={allGroups} onDeleted={() => setSelectedId(null)} />
          ) : (
            <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Ничего не найдено</div>
          )}
        </div>
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

/** Левая панель list-detail (issue #197): поиск + группы + пилюли-типы со счётчиком полей. */
function TypeListPanel({ groupOrder, byGroup, allDocTypes, selectedId, onSelect, query, onQuery }: {
  groupOrder: string[];
  byGroup: Map<string, DocumentType[]>;
  allDocTypes: DocumentType[];
  selectedId: string | null;
  onSelect: (id: string) => void;
  query: string;
  onQuery: (q: string) => void;
}) {
  return (
    <nav aria-label="Типы" className="w-80 shrink-0 border-r border-stroke flex flex-col bg-base">
      <div className="p-3 shrink-0">
        <div className="relative">
          <Search size={15} className="absolute left-3 top-1/2 -translate-y-1/2 text-fg4 pointer-events-none" />
          <input value={query} onChange={e => onQuery(e.target.value)} placeholder="Поиск типа…" aria-label="Поиск типа"
            className="w-full h-10 pl-9 pr-3 rounded-full text-sm bg-surface border border-stroke-strong text-fg1 outline-none focus-visible:ring-2 focus-visible:ring-brand placeholder:text-fg4" />
        </div>
      </div>
      <div className="flex-1 overflow-y-auto px-2 pb-3">
        {groupOrder.length === 0 && <p className="px-3 py-6 text-center text-sm text-fg4">Ничего не найдено</p>}
        {groupOrder.map(g => (
          <div key={g || '__ungrouped__'}>
            <div className="flex items-center gap-1.5 px-3 pt-3 pb-1 text-[11px] font-semibold uppercase tracking-wide text-fg4">
              <Folder size={12} className="shrink-0" />
              <span className="truncate flex-1">{g || 'Без группы'}</span>
              <span className="opacity-70">{byGroup.get(g)!.length}</span>
            </div>
            {byGroup.get(g)!.map(t => {
              const active = t.id === selectedId;
              return (
                <button key={t.id} type="button" onClick={() => onSelect(t.id)}
                  aria-current={active ? 'true' : undefined}
                  className={`w-full flex items-center gap-2.5 px-3 h-11 rounded-full text-left transition-colors ${
                    active ? 'bg-brand-subtle text-brand-hover font-medium' : 'text-fg2 hover:bg-muted'}`}>
                  <FileText size={17} className="shrink-0" />
                  <span className="flex-1 truncate text-sm">{t.name}</span>
                  <span className="text-xs text-fg4 shrink-0">{fieldCount(t, allDocTypes)}</span>
                </button>
              );
            })}
          </div>
        ))}
      </div>
    </nav>
  );
}
