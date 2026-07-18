import { useState } from 'react';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import {
  Plus, ChevronRight, Trash2, Copy, Folder, FileText, Boxes, EyeOff, Check,
  Braces, RotateCcw, Code, Database, Cpu,
} from 'lucide-react';
import { Switch } from '@/shared/ui/Switch';
import { BindingTemplatesDialog } from './BindingTemplatesDialog';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
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
import { GroupedFieldsEditor } from './GroupedFieldsEditor';
import { JsonPreview, FieldBuilder, DefaultValueCell, type FieldRegistries } from './FieldBuilder';
import {
  TypeEditorProvider, useRegisterEditor, useTypeEditorRegistry, LeaveGuardDialog, SectionCard,
} from './typeEditorShell';
import { ListDetailShell, NavSearchInput, DetailHeader, useDirtyGuard } from '@/shared/ui/ListDetailShell';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { uniqueCode } from './PrimitiveTypesPage';

/** Единственное членство (issue #197 Фаза C): каждый ключ поля остаётся только в первой группе,
 *  где встречается. Легаси-схемы могли класть поле в несколько групп — нормализуем при загрузке. */
function normalizeGroupMembership(gs: FieldGroup[]): FieldGroup[] {
  const seen = new Set<string>();
  return gs.map(g => ({
    ...g,
    fieldKeys: g.fieldKeys.filter(k => (seen.has(k) ? false : (seen.add(k), true))),
  }));
}

/** Sentinel для «— без родителя —» — Radix Select запрещает пустую строку как value. */
const NO_PARENT = '__none__';

// Реестр редакторов / диалог-гард / карточка-секция — общие для list-detail страниц (см. typeEditorShell).

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
    if (isCodeAuto) setCode(toCamelKey(v));
  }

  // Сохранение параметров: бросает при ошибке — чтобы общий «Сохранить»/гард прервались (issue #197).
  async function save() {
    if (!name.trim() || !code.trim()) { setError('Наименование и код обязательны'); throw new Error('validation'); }
    setError('');
    try {
      await mutation.mutateAsync({ id: docType.id, name: name.trim(), code: code.trim(), parentId: parentId || null });
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка сохранения');
      throw err;
    }
  }
  useRegisterEditor('props', dirty, save,
    () => { setName(docType.name); setCode(docType.code); setParentId(docType.parentId ?? ''); setError(''); });

  return (
    <form onSubmit={e => { e.preventDefault(); save().catch(() => { /* ошибка показана в форме */ }); }}
      className="space-y-3 pb-4 border-b border-stroke mb-4">
      <p className="text-xs font-medium text-fg3 uppercase tracking-wide">Параметры типа</p>
      <div className="grid grid-cols-2 gap-3">
        <TextField label="Наименование" value={name} onChange={e => handleNameChange(e.target.value)} required />
        <TextField label="Код" value={code} onChange={e => setCode(e.target.value)}
          required spellCheck={false} className="font-mono" />
      </div>
      <div>
        <label className="block text-xs font-medium text-fg2 mb-1">Родительский тип</label>
        <Select value={parentId || NO_PARENT} aria-label="Родительский тип"
          onValueChange={v => setParentId(v === NO_PARENT ? '' : v)}>
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
  const [groups, setGroups] = useState<FieldGroup[]>(() => normalizeGroupMembership(schemaDef.groups ?? []));
  const [excludedFields, setExcludedFields] = useState<string[]>(() => schemaDef.excludedFields ?? []);
  const [fieldOverrides, setFieldOverrides] = useState<Record<string, { required?: boolean; defaultValue?: unknown }>>(
    () => schemaDef.fieldOverrides ?? {},
  );
  const [typstRenders, setTypstRenders] = useState<TypstRender[]>(() => schemaDef.typstRenders ?? []);
  const [docTypeTags, setDocTypeTags] = useState<string[]>(() => schemaDef.tags ?? []);
  const [ungroupedOrder, setUngroupedOrder] = useState<string[]>(() => schemaDef.ungroupedOrder ?? []);
  const { data: tagRegistry } = useTagRegistry();
  const applicableTypeTags = typeTagDefs(tagRegistry, docType.kind);
  const [showJson, setShowJson] = useState(false);
  const [showTypstRenders, setShowTypstRenders] = useState(typstRenders.length > 0);
  const [showTypeTags, setShowTypeTags] = useState(false);
  const [error, setError] = useState('');
  const [dirty, setDirty] = useState(false);
  const mutation = useUpdateDocumentTypeSchema();

  const compositeTypes = allDocTypes.filter(dt => dt.kind === 'Composite');
  const parentType = docType.parentId ? allDocTypes.find(dt => dt.id === docType.parentId) ?? null : null;
  const parentEffectiveFields = parentType ? resolveEffectiveFields(parentType, allDocTypes) : [];
  const inheritedKeys = new Set(parentEffectiveFields.map(f => f.key));
  const effectiveFields = resolveEffectiveFields(docType, allDocTypes);
  const reg: FieldRegistries = { compositeTypes, primitiveTypes, enumTypes, allDocTypes, tagRegistry };
  // Унаследованные поля для группировки — активные (исключённые не показываем в раскладке).
  const activeInheritedFields = parentEffectiveFields.filter(f => !excludedFields.includes(f.key));

  const handleExclude = (key: string) => {
    setExcludedFields(prev => [...prev.filter(k => k !== key), key]);
    setFieldOverrides(prev => { const n = { ...prev }; delete n[key]; return n; });
    setDirty(true);
  };
  const handleInclude = (key: string) => { setExcludedFields(prev => prev.filter(k => k !== key)); setDirty(true); };
  const handleOverrideRequired = (key: string, required: boolean) => {
    setFieldOverrides(prev => ({ ...prev, [key]: { ...prev[key], required } })); setDirty(true);
  };
  const handleOverrideDefaultValue = (key: string, value: unknown) => {
    setFieldOverrides(prev => {
      const cur = prev[key] ?? {};
      if (value === undefined) {
        const { defaultValue: _, ...rest } = cur as { required?: boolean; defaultValue?: unknown };
        return Object.keys(rest).length ? { ...prev, [key]: rest } : { ...prev, [key]: rest };
      }
      return { ...prev, [key]: { ...cur, defaultValue: value } };
    }); setDirty(true);
  };
  const handleResetOverride = (key: string) => {
    setFieldOverrides(prev => { const n = { ...prev }; delete n[key]; return n; }); setDirty(true);
  };

  // Сохранение схемы: бросает при ошибке валидации/мутации — чтобы общий «Сохранить»/гард
  // прерывались, а ошибка показывалась здесь же (issue #197 Фаза C).
  async function save() {
    setError('');
    const fieldError = validateFields(fields);
    if (fieldError) { setError(fieldError); throw new Error(fieldError); }
    const conflict = fields.find(f => inheritedKeys.has(f.key.trim()));
    if (conflict) { const m = `Ключ "${conflict.key}" уже есть в родительском типе`; setError(m); throw new Error(m); }

    // Проверка уникальности fnName Typst-блоков в рамках всей системы
    const definedFnNames = typstRenders.map(r => r.fnName.trim()).filter(Boolean);
    const localDup = definedFnNames.find((n, i) => definedFnNames.indexOf(n) !== i);
    if (localDup) { const m = `Имя функции "${localDup}" задано дважды`; setError(m); throw new Error(m); }

    const foreignFnNames = new Set<string>();
    for (const dt of allDocTypes) {
      if (dt.id === docType.id) continue;
      const def = dt.schema as unknown as SchemaDefinition;
      for (const r of def.typstRenders ?? []) {
        if (r.fnName) foreignFnNames.add(r.fnName.trim());
      }
    }
    const crossDup = definedFnNames.find(n => foreignFnNames.has(n));
    if (crossDup) { const m = `Имя функции "${crossDup}" уже используется в другом типе`; setError(m); throw new Error(m); }

    try {
      await mutation.mutateAsync({ id: docType.id, schema: schemaToJson(fields, excludedFields, fieldOverrides, groups, typstRenders, docTypeTags, ungroupedOrder) });
      setDirty(false);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка сохранения');
      throw err;
    }
  }
  useRegisterEditor('schema', dirty, save, () => {
    setFields(parseSchemaFields(docType.schema));
    setGroups(normalizeGroupMembership(schemaDef.groups ?? []));
    setExcludedFields(schemaDef.excludedFields ?? []);
    setFieldOverrides(schemaDef.fieldOverrides ?? {});
    setTypstRenders(schemaDef.typstRenders ?? []);
    setDocTypeTags(schemaDef.tags ?? []);
    setUngroupedOrder(schemaDef.ungroupedOrder ?? []);
    setError(''); setDirty(false);
  });

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
        <div className="flex items-center justify-between mb-2">
          <p className="text-xs font-medium text-fg3 uppercase tracking-wide">
            {parentType ? 'Поля и группировка' : 'Поля'}
          </p>
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
          : <GroupedFieldsEditor
              fields={fields}
              onFieldsChange={f => { setFields(f); setDirty(true); }}
              groups={groups}
              onGroupsChange={g => { setGroups(g); setDirty(true); }}
              ungroupedOrder={ungroupedOrder}
              onUngroupedOrderChange={o => { setUngroupedOrder(o); setDirty(true); }}
              parentEffectiveFields={activeInheritedFields}
              disabledKeys={inheritedKeys}
              reg={reg}
            />}
      </div>

      {!showJson && applicableTypeTags.length > 0 && (
        <SectionCard icon={<Cpu size={15} />} title="Функциональные тэги типа"
          count={docTypeTags.length} countClass="text-purple-600"
          open={showTypeTags} onToggle={() => setShowTypeTags(v => !v)}>
          <div className="flex flex-wrap gap-1.5 pt-2">
            {applicableTypeTags.map(t => {
              const on = docTypeTags.includes(t.code);
              // Ограничение носителей (issue #258): тэг занят другими типами сверх лимита и текущий тип
              // его не несёт → дизейбл + тултип с занятыми типами.
              const max = t.restriction?.maxBearers ?? null;
              const otherBearers = max == null ? [] : allDocTypes.filter(dt => dt.id !== docType.id
                && (((dt.schema as { tags?: string[] }).tags) ?? []).includes(t.code));
              const blocked = max != null && !on && otherBearers.length >= max;
              return (
                <button
                  key={t.code}
                  type="button"
                  disabled={blocked}
                  title={blocked
                    ? `Тэг уже назначен: ${otherBearers.map(b => `«${b.name}»`).join(', ')}. Допустимо не более ${max}.`
                    : t.description}
                  onClick={() => { setDocTypeTags(prev => on ? prev.filter(c => c !== t.code) : [...prev, t.code]); setDirty(true); }}
                  className={`px-2.5 py-1 rounded-full text-xs border transition-colors ${
                    blocked ? 'border-stroke text-fg4/50 opacity-60 cursor-not-allowed'
                      : on ? 'bg-purple-500/15 border-purple-400 text-purple-700'
                        : 'border-stroke text-fg4 hover:border-stroke-strong hover:text-fg2'
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
              onChange={r => { setTypstRenders(r); setDirty(true); }}
              fields={effectiveFields}
              allDocTypes={allDocTypes}
            />
          </div>
        </SectionCard>
      )}

      {!showJson && error && <p className="text-xs text-danger pt-1">{error}</p>}
    </div>
  );
}

// ─── Type row ──────────────────────────────────────────────────────────────────

/** Число эффективных полей типа — для счётчика в списке-пилюле (issue #197). */
function fieldCount(docType: DocumentType, allDocTypes: DocumentType[]): number {
  return resolveEffectiveFields(docType, allDocTypes).length;
}

/** Типы, ссылающиеся на данный тип полем complex/array/doc-ref/doc-array (по собственной схеме). */
function findReferencingTypes(id: string, allDocTypes: DocumentType[]): DocumentType[] {
  return allDocTypes.filter(dt => dt.id !== id
    && parseSchemaFields(dt.schema).some(f =>
      (f.type === 'complex' || f.type === 'array' || f.type === 'doc-ref' || f.type === 'doc-array') && f.typeId === id));
}

/** Правая панель list-detail (issue #197 Фаза A): шапка типа (метрики+действия) + редактор как есть. */
function TypeDetail({ docType, allDocTypes, allGroups, onDeleted, dirty, saving, onSaveAll, onRevert, onDuplicate }: {
  docType: DocumentType; allDocTypes: DocumentType[]; allGroups: string[]; onDeleted: () => void;
  dirty: boolean; saving: boolean; onSaveAll: () => Promise<void>; onRevert: () => void; onDuplicate: () => void;
}) {
  const deleteMutation = useDeleteDocumentType();
  const groupMutation = useSetDocumentTypeGroup();
  const [templatesOpen, setTemplatesOpen] = useState(false);
  const [deleteConfirmOpen, setDeleteConfirmOpen] = useState(false);

  const effectiveFields = resolveEffectiveFields(docType, allDocTypes);
  const ownFieldCount = parseSchemaFields(docType.schema).length;
  const parentType = docType.parentId ? allDocTypes.find(dt => dt.id === docType.parentId) : null;
  const hasChildren = allDocTypes.some(dt => dt.parentId === docType.id);
  // Типы, ссылающиеся на этот тип полем complex/array/doc-ref/doc-array (актуально для составных —
  // удаление сломало бы ссылки). issue #197 Фаза C — полировка Composite.
  const referencedBy = findReferencingTypes(docType.id, allDocTypes);
  const deleteBlock = hasChildren ? 'Нельзя удалить: есть дочерние типы'
    : referencedBy.length > 0 ? `Нельзя удалить: используется другими типами (${referencedBy.length})`
    : null;
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
      {/* Шапка типа — доменные heading/actions поверх общего DetailHeader (issue #210 Этап 1b) */}
      <DetailHeader dirty={dirty} saving={saving} onSaveAll={onSaveAll} onRevert={onRevert}
        heading={
          <>
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
              {referencedBy.length > 0 && (
                <span className={`${badge} bg-brand-subtle text-brand`} title={`Используется в: ${referencedBy.map(t => t.name).join(', ')}`}>
                  используется: {referencedBy.length}
                </span>
              )}
            </div>
          </>
        }
        actions={
          <>
            <GroupPicker groups={allGroups} value={docType.group}
              onChange={group => groupMutation.mutate({ id: docType.id, group })} />
            <RowActionsMenu ariaLabel="Действия типа" actions={[
              { key: 'dup', label: 'Дублировать', icon: <Copy size={14} />, onSelect: onDuplicate },
              ...(docType.kind === 'Document'
                ? [{ key: 'tpl', label: 'Шаблоны данных', icon: <Database size={14} />, onSelect: () => setTemplatesOpen(true) }]
                : []),
              { key: 'del', label: deleteBlock ?? 'Удалить тип', danger: true, disabled: deleteMutation.isPending || !!deleteBlock,
                icon: <Trash2 size={14} />, onSelect: () => { if (!deleteBlock) setDeleteConfirmOpen(true); } },
            ]} />
          </>
        } />
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

  // Реестр незасохранённых форм текущего типа (явное сохранение, issue #197 / #210 — общий).
  const { registry, anyDirty, saving, saveAll, resetAll } = useTypeEditorRegistry();

  // Гард при уходе с типа с несохранёнными правками (общий useDirtyGuard, issue #210 Этап 1b).
  const { request, dialogProps } = useDirtyGuard<string | null>({
    isDirty: anyDirty, saving, saveAll,
    onCommit: id => setSelectedId(id),
  });
  const requestSelect = (id: string) => { if (id !== selectedId) request(id); };

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

  // Дублирование типа со схемой (клиентский клон, issue #210 Этап 2).
  const createDoc = useCreateDocumentType();
  const duplicateType = (dt: DocumentType) => createDoc.mutate({
    name: `Копия ${dt.name}`, code: uniqueCode(dt.code, new Set(allDocTypes.map(x => x.code))),
    kind: dt.kind, parentId: dt.parentId ?? null,
    schema: JSON.stringify(dt.schema), isAbstract: dt.kind === 'Document' ? dt.isAbstract : false,
  });

  const overlay = isLoading
    ? <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Загрузка...</div>
    : filtered.length === 0
      ? <div className="flex-1 flex items-center justify-center text-fg4 text-sm">
          {kind === 'Document' ? 'Типов документов не создано' : 'Составных типов не создано'}
        </div>
      : null;

  return (
    <>
      <TypeEditorProvider value={registry}>
        <ListDetailShell
          title={title}
          subtitle={kind === 'Composite' ? 'Переиспользуемые структуры полей для использования внутри типов документов' : undefined}
          headerAction={<Button variant="filled" icon={<Plus size={16} />} onClick={() => setCreateOpen(true)}>{addLabel}</Button>}
          overlay={overlay}
          nav={<TypeListPanel
            groupOrder={groupOrder} byGroup={byGroup} allDocTypes={allDocTypes}
            selectedId={selected?.id ?? null} onSelect={requestSelect}
            query={query} onQuery={setQuery} />}
          detail={selected ? (
            <TypeDetail key={selected.id} docType={selected} allDocTypes={allDocTypes}
              allGroups={allGroups} onDeleted={() => setSelectedId(null)}
              dirty={anyDirty} saving={saving} onSaveAll={saveAll} onRevert={resetAll}
              onDuplicate={() => duplicateType(selected)} />
          ) : (
            <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Ничего не найдено</div>
          )} />
      </TypeEditorProvider>

      <LeaveGuardDialog {...dialogProps} />

      <Modal open={createOpen} onOpenChange={setCreateOpen}
        title={kind === 'Document' ? 'Новый тип документа' : 'Новый составной тип'}
        wide flushBody>
        {createOpen && (
          <CreateForm kind={kind} onClose={() => setCreateOpen(false)} allDocTypes={allDocTypes} />
        )}
      </Modal>
    </>
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
  // Сворачиваемые группы навигации (по умолчанию свёрнуты). При активном поиске все
  // группы раскрыты, чтобы результаты были видны.
  const [expandedGroups, setExpandedGroups] = useState<Set<string>>(new Set());
  const searching = query.trim().length > 0;
  const toggleGroup = (g: string) =>
    setExpandedGroups(s => { const n = new Set(s); n.has(g) ? n.delete(g) : n.add(g); return n; });

  return (
    <>
      <NavSearchInput value={query} onChange={onQuery} placeholder="Поиск типа…" />
      <div className="flex-1 overflow-y-auto px-2 pb-3">
        {groupOrder.length === 0 && <p className="px-3 py-6 text-center text-sm text-fg4">Ничего не найдено</p>}
        {groupOrder.map(g => {
          const items = byGroup.get(g)!;
          const open = searching || expandedGroups.has(g);
          return (
            <div key={g || '__ungrouped__'}>
              <button type="button" onClick={() => toggleGroup(g)}
                aria-expanded={open}
                className="w-full flex items-center gap-1.5 px-3 pt-3 pb-1 text-[11px] font-semibold uppercase tracking-wide text-fg4 hover:text-fg2 transition-colors">
                <ChevronRight size={12} className={`shrink-0 transition-transform ${open ? 'rotate-90' : ''}`} />
                <Folder size={12} className="shrink-0" />
                <span className="truncate flex-1 text-left">{g || 'Без группы'}</span>
                <span className="opacity-70">{items.length}</span>
              </button>
              {open && items.map(t => {
                const active = t.id === selectedId;
                const Icon = t.kind === 'Composite' ? Boxes : FileText;
                return (
                  <button key={t.id} type="button" onClick={() => onSelect(t.id)}
                    aria-current={active ? 'true' : undefined}
                    className={`w-full flex items-center gap-2.5 px-3 h-11 rounded-full text-left transition-colors ${
                      active ? 'bg-brand-subtle text-brand-hover font-medium' : 'text-fg2 hover:bg-muted'}`}>
                    <Icon size={17} className="shrink-0" />
                    <span className="flex-1 truncate text-sm">{t.name}</span>
                    <span className="text-xs text-fg4 shrink-0">{fieldCount(t, allDocTypes)}</span>
                  </button>
                );
              })}
            </div>
          );
        })}
      </div>
    </>
  );
}
