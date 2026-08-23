import { useState, useMemo, useRef, useEffect } from 'react';
import { useNavigate } from 'react-router';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import {
  Plus, ChevronRight, Trash2, Copy, Folder, FileText, Boxes, EyeOff, Check,
  Braces, RotateCcw, Code, Database, Cpu, HelpCircle, RefreshCw, ShieldCheck, AlertTriangle,
} from 'lucide-react';
import { Switch } from '@/shared/ui/Switch';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { Markdown } from '@/shared/ui/Markdown';
import { BindingTemplatesDialog } from './BindingTemplatesDialog';
import { TypeAuditModal } from './TypeAuditModal';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { TypePickerField } from '@/shared/ui/TypePickerField';
import type { PickType } from '@/shared/ui/TypePicker';
import { TextField } from '@/shared/ui/TextField';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { countTemplatesUsingTypeCode } from '@/shared/api/typstUserLib';
import {
  useListDocumentTypes,
  useCreateDocumentType,
  useUpdateDocumentType,
  useUpdateDocumentTypeSchema,
  useMigrateFieldKey,
  useDeleteDocumentType,
  useDocumentTypeUsage,
  useSetDocumentTypeAbstract,
  useSetDocumentTypeAllowsProxy,
  useSetDocumentTypeGroup,
  useIdentityImpact,
  type IdentityImpact,
} from '@/shared/api/documentTypes';
import { ruCount, ruPlural } from '@/shared/utils/pluralize';
import { GroupPicker } from './TypeGroupAccordion';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import type { DocumentType, DocumentTypeKind, EnumTypeDef } from '@/shared/api/types';
import {
  chainFieldKeys,
  parseSchemaFields,
  resolveEffectiveFields,
  type SchemaField,
  type SchemaDefinition,
  type FieldGroup,
  type TypstRender,
} from '@/shared/api/schema';
import { danglingKeyRefs, danglingRefPlaces } from './schemaKeyChecks';
import { typeHealth, healthBadgeLabel } from './typeHealth';
import { TypstRendersEditor } from './TypstRendersEditor';
import { useTypstBlocksCheck, TypstBlocksPanel, blocksCheckProblemsByFn } from './TypstBlocksCheck';
import { schemaToJson, validateFields, TYPE_LABELS, nextAutoKey } from './schemaConstants';
import { useTagRegistry, typeTags as typeTagDefs, FUNCTIONAL_TAG } from '@/shared/api/tags';
import { GroupedFieldsEditor } from './GroupedFieldsEditor';
import { JsonPreview, FieldBuilder, DefaultValueCell, type FieldRegistries } from './FieldBuilder';
import {
  TypeEditorProvider, useRegisterEditor, useTypeEditorRegistry, LeaveGuardDialog, SectionCard,
} from './typeEditorShell';
import { ListDetailShell, NavSearchInput, DetailHeader, useDirtyGuard } from '@/shared/ui/ListDetailShell';
import { useLeaveGuard } from '@/shared/ui/NavigationGuard';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { useRememberedSelection } from '@/shared/hooks/useRememberedSelection';
import { useToast } from '@/shared/ui/Toast';
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

/** Родительский тип как `PickType` для `TypePickerField` (section — единая шапка группы пикера). */
function toParentPickTypes(types: DocumentType[]): PickType[] {
  return types.map(dt => ({ id: dt.id, name: dt.name, code: dt.code, section: 'Родительский тип' }));
}

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

  // Код типа стабилен после создания (issue #355): у существующего типа переименование НЕ меняет код
  // (иначе ломаются ссылки/резолв, #59). Авто-код — только у нового (без id).
  function handleNameChange(v: string) {
    setCode(nextAutoKey(code, name, v, !docType.id));
    setName(v);
  }

  // Смена кода типа ломает вызовы «Код.имя» в шаблонах (issue #773): спрашиваем ДО сохранения,
  // сколько шаблонов затронуто. Typst сообщил бы об этом лишь при генерации документа — когда связь
  // с причиной уже не видна.
  const [codeWarning, setCodeWarning] = useState<{ count: number; onConfirm: () => void } | null>(null);

  // Сохранение параметров: бросает при ошибке — чтобы общий «Сохранить»/гард прервались (issue #197).
  async function save() {
    if (!name.trim() || !code.trim()) { setError('Наименование и код обязательны'); throw new Error('validation'); }
    setError('');

    if (docType.id && code.trim() !== docType.code) {
      const used = await countTemplatesUsingTypeCode(docType.code).catch(() => 0);
      if (used > 0) {
        const confirmed = await new Promise<boolean>(resolve =>
          setCodeWarning({ count: used, onConfirm: () => resolve(true) }));
        setCodeWarning(null);
        if (!confirmed) throw new Error('rename-cancelled');
      }
    }

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
      <ConfirmDialog
        open={codeWarning !== null}
        onOpenChange={o => { if (!o) setCodeWarning(null); }}
        title="Сменить код типа?"
        description={
          <>
            Шаблонов, которые обращаются к блокам этого типа по коду{' '}
            <code className="font-mono">{docType.code}</code>: <b>{codeWarning?.count ?? 0}</b>.
            После смены кода эти вызовы перестанут разрешаться — префикс в них придётся заменить
            вручную. Typst сообщит об этом только при генерации документа.
          </>
        }
        confirmLabel="Сменить код"
        confirmDanger={false}
        onConfirm={() => codeWarning?.onConfirm()}
      />
      <p className="text-xs font-medium text-fg3 uppercase tracking-wide">Параметры типа</p>
      <div className="grid grid-cols-2 gap-3">
        <TextField label="Наименование" value={name} onChange={e => handleNameChange(e.target.value)} required />
        <TextField label="Код" value={code} onChange={e => setCode(e.target.value)}
          required spellCheck={false} className="font-mono" />
      </div>
      <TypePickerField className="w-full" label="Родительский тип" title="Родительский тип"
        placeholder="— без родителя —" clearable={{ label: 'Без родителя' }}
        types={toParentPickTypes(eligibleParents)} value={parentId || undefined}
        onChange={id => setParentId(id ?? '')} />
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
  kind, onClose, onCreated, allDocTypes,
}: {
  kind: DocumentTypeKind;
  onClose: () => void;
  /** Созданный тип — страница выбирает его в list-detail (issue #383: открыть на редактирование). */
  onCreated: (created: DocumentType) => void;
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
    setCode(nextAutoKey(code, name, v, true)); // форма создания — тип всегда новый
    setName(v);
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
      const created = await mutation.mutateAsync({
        name, code, kind,
        parentId: parentId || null,
        schema: schemaToJson(fields, [], {}),
        isAbstract: kind === 'Document' ? isAbstract : false,
      });
      onCreated(created); // выбрать созданный тип → откроется detail-редактор (issue #383)
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
        <TypePickerField className="w-full" label="Родительский тип (наследование)" title="Родительский тип"
          placeholder="— без родителя —" clearable={{ label: 'Без родителя' }}
          types={toParentPickTypes(sameKindTypes)} value={parentId || undefined}
          onChange={id => setParentId(id ?? '')} />
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

function SchemaEditor({ docType, allDocTypes, onSelectType }: {
  docType: DocumentType;
  allDocTypes: DocumentType[];
  onSelectType: (id: string) => void;
}) {
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const { data: enumTypes = [] } = useListEnumTypes();
  const schemaDef = docType.schema as unknown as SchemaDefinition;
  const [fields, setFields] = useState<SchemaField[]>(() => parseSchemaFields(docType.schema));
  // Ключи полей из СОХРАНЁННОЙ схемы (issue #355): их ключи заморожены (переименование не меняет ключ).
  // Новое поле (ключа нет в наборе) — ключ авто-следует за именем. Пересчитывается при сохранении/сбросе.
  const persistedKeys = useMemo(() => new Set(parseSchemaFields(docType.schema).map(f => f.key)), [docType.schema]);
  const [groups, setGroups] = useState<FieldGroup[]>(() => normalizeGroupMembership(schemaDef.groups ?? []));
  const [excludedFields, setExcludedFields] = useState<string[]>(() => schemaDef.excludedFields ?? []);
  const [fieldOverrides, setFieldOverrides] = useState<Record<string, { required?: boolean; defaultValue?: unknown }>>(
    () => schemaDef.fieldOverrides ?? {},
  );
  const [typstRenders, setTypstRenders] = useState<TypstRender[]>(() => schemaDef.typstRenders ?? []);
  const blocksCheck = useTypstBlocksCheck(docType.id, onSelectType);
  const [docTypeTags, setDocTypeTags] = useState<string[]>(() => schemaDef.tags ?? []);
  const [ungroupedOrder, setUngroupedOrder] = useState<string[]>(() => schemaDef.ungroupedOrder ?? []);
  const [help, setHelp] = useState<string>(() => schemaDef.help ?? '');
  const [showHelp, setShowHelp] = useState(false);
  const [helpPreview, setHelpPreview] = useState(false);
  const { data: tagRegistry } = useTagRegistry();
  const applicableTypeTags = typeTagDefs(tagRegistry, docType.kind);
  const [showJson, setShowJson] = useState(false);
  const [showTypstRenders, setShowTypstRenders] = useState(typstRenders.length > 0);
  const [showTypeTags, setShowTypeTags] = useState(false);
  const [error, setError] = useState('');
  const [dirty, setDirty] = useState(false);
  const mutation = useUpdateDocumentTypeSchema();
  // Миграция ключа (issue #357): накапливаем переименования ключей сохранённых полей (origKey→newKey),
  // после успешного сохранения схемы предлагаем перенести данные документов старый→новый.
  const renamesRef = useRef<Map<string, string>>(new Map());
  const migrateKey = useMigrateFieldKey(docType.id);
  const schemaToast = useToast();
  const [pendingMigration, setPendingMigration] = useState<{ from: string; to: string }[] | null>(null);
  // Гейт правки полей-идентификаторов (issue #584): держим решение пользователя как промис, потому
  // что спрашивать надо ВНУТРИ save() — до записи схемы, а не после, когда связки уже осиротели.
  const identityImpact = useIdentityImpact();
  const [identityGate, setIdentityGate] = useState<
    { impact: IdentityImpact; decide: (proceed: boolean) => void } | null>(null);

  const compositeTypes = allDocTypes.filter(dt => dt.kind === 'Composite');
  const parentType = docType.parentId ? allDocTypes.find(dt => dt.id === docType.parentId) ?? null : null;
  const parentEffectiveFields = parentType ? resolveEffectiveFields(parentType, allDocTypes) : [];
  const inheritedKeys = new Set(parentEffectiveFields.map(f => f.key));
  // Ссылки на несуществующие поля (issue #639). Сверяем со ВСЕМИ ключами цепочки наследования ДО
  // исключений, а не с эффективными полями родителя: исключение ссылается на поле, которое
  // существует, — в этом его смысл, и по эффективному набору собственное исключение потомка
  // выглядело бы висячим (см. chainFieldKeys).
  const chainKeys = parentType ? chainFieldKeys(parentType, allDocTypes) : [];
  const danglingRefs = danglingKeyRefs(
    { groups, excludedFields, ungroupedOrder, fieldOverrideKeys: Object.keys(fieldOverrides) },
    [...chainKeys, ...fields.map(f => f.key)]);
  const dropDanglingRefs = () => {
    const dead = new Set(danglingRefs.map(r => r.key));
    setGroups(gs => gs.map(g => ({ ...g, fieldKeys: g.fieldKeys.filter(k => !dead.has(k)) })));
    setExcludedFields(ks => ks.filter(k => !dead.has(k)));
    setUngroupedOrder(ks => ks.filter(k => !dead.has(k)));
    setFieldOverrides(o => Object.fromEntries(Object.entries(o).filter(([k]) => !dead.has(k))));
    setDirty(true);
  };
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

    // Имена Typst-блоков уникальны В ПРЕДЕЛАХ ТИПА (issue #773): каждый тип — свой модуль со своей
    // областью, и `Адрес.full` рядом с `Подписант.full` совершенно законны. Прежняя проверка на
    // уникальность по всей системе осталась бы прямым запретом на результат миграции — она сама
    // срезает префиксы и порождает такие совпадения, после чего схему было бы не сохранить.
    const definedFnNames = typstRenders.map(r => r.fnName.trim()).filter(Boolean);
    const localDup = definedFnNames.find((n, i) => definedFnNames.indexOf(n) !== i);
    if (localDup) { const m = `Имя функции "${localDup}" задано дважды в этом типе`; setError(m); throw new Error(m); }

    const schemaJson = schemaToJson(fields, excludedFields, fieldOverrides, groups, typstRenders, docTypeTags, ungroupedOrder, help);

    // Правка полей-идентификаторов осиротит связки «материал → документ качества» (issue #584):
    // ключ составной, и добавление поля, снятие тэга или перенумерация меняют ключи ВСЕХ материалов
    // разом. Молча этого делать нельзя — документ выпустился бы без сертификатов при здоровом виде
    // в UI. Отказ сервера считать последствия сохранение не блокирует: предупреждение — не гейт
    // целостности, а помощь, и ронять из-за него правку схемы неправильно.
    try {
      const impact = await identityImpact.mutateAsync({ id: docType.id, schema: schemaJson });
      if (impact.changed && impact.affectedLinks > 0) {
        const proceed = await new Promise<boolean>(decide => setIdentityGate({ impact, decide }));
        if (!proceed) {
          const m = 'Сохранение отменено: правка полей-идентификаторов не подтверждена';
          setError(m); throw new Error(m);
        }
      }
    } catch (err: unknown) {
      if (err instanceof Error && err.message.startsWith('Сохранение отменено')) throw err;
    }

    try {
      await mutation.mutateAsync({ id: docType.id, schema: schemaJson });
      setDirty(false);
      // Проверка сборки блоков после сохранения (issue #309, фаза 2) — не блокирует save.
      if (typstRenders.length > 0) void blocksCheck.run(typstRenders);
      // issue #357: у сохранённого поля сменился ключ (persistedKeys — старые ключи в этом замыкании) →
      // предложить перенос данных документов старый→новый. Схема уже сохранена (ключ переехал в ней).
      const renames = [...renamesRef.current]
        .filter(([from, to]) => from !== to && persistedKeys.has(from) && fields.some(f => f.key.trim() === to));
      renamesRef.current.clear();
      if (renames.length) setPendingMigration(renames.map(([from, to]) => ({ from, to })));
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
    setHelp(schemaDef.help ?? '');
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

      {/* Висячие ссылки (issue #639): ключ в раскладке групп или в исключениях, которому не
          соответствует ни своё поле, ни унаследованное. В интерфейсе такой ключ не виден нигде —
          в раскладке рисуются поля, а не имена, — поэтому «ДатаДокумнета» и пережил все правки
          схемы. Чистим по кнопке и обычным «Сохранить»: молча править чужую схему нельзя. */}
      {danglingRefs.length > 0 && (
        <div className="flex items-start gap-2 rounded-md border border-warning/50 bg-warning/10 px-3 py-2">
          <AlertTriangle size={14} className="text-warning shrink-0 mt-0.5" />
          <div className="text-xs text-fg2 space-y-1 flex-1 min-w-0">
            <p>В схеме есть ссылки на поля, которых нет ни среди своих, ни среди унаследованных —
              скорее всего опечатка в ключе:</p>
            <ul className="space-y-0.5">
              {danglingRefs.map(r => (
                <li key={r.key}>
                  <span className="font-mono">{r.key}</span>
                  <span className="text-fg4"> — {danglingRefPlaces(r)}</span>
                </li>
              ))}
            </ul>
          </div>
          <button type="button" onClick={dropDanglingRefs}
            className="text-xs px-2 py-1 rounded border border-warning/60 text-fg2 hover:bg-warning/20 shrink-0">
            Убрать ссылки
          </button>
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
        {showJson && <JsonPreview fields={fields} groups={groups} excludedFields={excludedFields} fieldOverrides={fieldOverrides} />}
        {/* Редактор ПРЯЧЕМ, а не размонтируем (issue #527): в нём живёт состояние карточек полей —
            база сравнения ключа, снятый замок, раскрытая карточка. Заглянув в JSON посреди
            переименования, пользователь возвращался к полю, которое снова считается новым: замок
            снят молча, предупреждение о дрейфе данных пропало, перенос данных уже не предложится. */}
        <div className={showJson ? 'hidden' : undefined}>
            <GroupedFieldsEditor
              fields={fields}
              onFieldsChange={f => { setFields(f); setDirty(true); }}
              groups={groups}
              onGroupsChange={g => { setGroups(g); setDirty(true); }}
              ungroupedOrder={ungroupedOrder}
              onUngroupedOrderChange={o => { setUngroupedOrder(o); setDirty(true); }}
              parentEffectiveFields={activeInheritedFields}
              disabledKeys={inheritedKeys}
              persistedKeys={persistedKeys}
              onKeyRename={(from, to) => renamesRef.current.set(from, to)}
              reg={reg}
            />
        </div>
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
          {(() => {
            // issue #258: если тип назначен профилем уровня — read-only заметка «где редактируется + ключ».
            const p = [
              { code: FUNCTIONAL_TAG.profileConstruction, level: 'Стройка', key: 'стройка' },
              { code: FUNCTIONAL_TAG.profileSection, level: 'Раздел', key: 'раздел' },
              { code: FUNCTIONAL_TAG.profileSet, level: 'Комплект', key: 'комплект' },
            ].find(x => docTypeTags.includes(x.code));
            return p ? (
              <p className="mt-2 text-xs text-fg3">
                Используется как <span className="text-brand-hover font-medium">профиль уровня «{p.level}»</span>:
                его объект редактируется в «Общие данные» уровня, поля доступны в шаблоне как{' '}
                <code className="font-mono bg-muted text-fg1 px-1 rounded">data.уровень.{p.key}.*</code>.
              </p>
            ) : null;
          })()}
        </SectionCard>
      )}

      {!showJson && (
        <SectionCard icon={<HelpCircle size={15} />} title="Справка для пользователя"
          count={help.trim() ? 1 : 0} countClass="text-brand"
          open={showHelp} onToggle={() => setShowHelp(v => !v)}>
          <div className="pt-2 space-y-2">
            <p className="text-xs text-fg4">
              Показывается при редактировании документа этого типа (напр. что подтягивается из профиля
              уровня). Markdown: <code className="font-mono">**жирный**</code>, <code className="font-mono">*курсив*</code>, списки, <code className="font-mono">[ссылки](url)</code>.
            </p>
            <div className="flex items-center gap-3 text-xs">
              <button type="button" onClick={() => setHelpPreview(false)}
                className={!helpPreview ? 'text-brand font-medium' : 'text-fg4 hover:text-fg2'}>Текст</button>
              <button type="button" onClick={() => setHelpPreview(true)}
                className={helpPreview ? 'text-brand font-medium' : 'text-fg4 hover:text-fg2'}>Предпросмотр</button>
            </div>
            {helpPreview ? (
              help.trim()
                ? <div className="rounded-md border border-stroke bg-surface p-3"><Markdown>{help}</Markdown></div>
                : <p className="text-xs text-fg4 italic px-1">Пусто — введите текст на вкладке «Текст».</p>
            ) : (
              <textarea value={help} onChange={e => { setHelp(e.target.value); setDirty(true); }} rows={5}
                placeholder="Напр.: Проект, адрес и заказчик подтягиваются из профиля стройки — заполнять здесь не нужно."
                className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
            )}
          </div>
        </SectionCard>
      )}

      {!showJson && (docType.kind === 'Composite' || docType.kind === 'Document') && (
        <SectionCard icon={<Code size={15} />} title="Typst-блоки (варианты отображения)"
          count={typstRenders.length} countClass="text-purple-600"
          open={showTypstRenders} onToggle={() => setShowTypstRenders(v => !v)}>
          <div className="pt-2 space-y-3">
            <div className="flex items-center justify-between">
              <p className="text-xs text-fg4">Функции отображения для Typst-шаблонов.</p>
              <Button variant="text" size="sm" icon={<RefreshCw size={13} className={blocksCheck.checking ? 'animate-spin' : ''} />}
                disabled={blocksCheck.checking} onClick={() => void blocksCheck.run(typstRenders)}>
                Проверить блоки
              </Button>
            </div>
            {blocksCheck.problems && (
              <TypstBlocksPanel problems={blocksCheck.problems} currentTypeId={docType.id} onSelectType={onSelectType} />
            )}
            <TypstRendersEditor
              typeCode={docType.code}
              renders={typstRenders}
              onChange={r => { setTypstRenders(r); setDirty(true); }}
              onBlockCommitted={r => void blocksCheck.run(r)}
              problemsByFn={blocksCheckProblemsByFn(blocksCheck.problems, docType.id)}
              fields={effectiveFields}
              allDocTypes={allDocTypes}
            />
          </div>
        </SectionCard>
      )}

      {!showJson && error && <p className="text-xs text-danger pt-1">{error}</p>}

      {/* Гейт правки полей-идентификаторов (issue #584): показываем, во что превратится ключ и
          сколько связок перестанут находиться. Отмена диалога = отказ, поэтому решение отдаём в
          decide и в onOpenChange тоже. */}
      <ConfirmDialog
        open={!!identityGate}
        onOpenChange={o => { if (!o) { identityGate?.decide(false); setIdentityGate(null); } }}
        title="Изменение полей-идентификаторов осиротит связки"
        errorTitle="Изменение полей-идентификаторов осиротит связки"
        description={identityGate && (
          <div className="space-y-2">
            <p>
              Ключ материала склеивается из всех полей с тэгом «Идентификатор». После сохранения он
              станет другим у ВСЕХ материалов, и{' '}
              <b>{ruCount(identityGate.impact.affectedLinks, 'связка', 'связки', 'связок')}</b>{' '}
              «материал → документ качества»{' '}
              {ruPlural(identityGate.impact.affectedLinks, 'перестанет', 'перестанут', 'перестанут')}{' '}
              находиться. Сертификаты просто не попадут в документ — ошибки при этом не будет.
            </p>
            <div className="text-xs font-mono text-fg3 space-y-0.5">
              <p>было: {identityGate.impact.before.join(' | ') || '—'}</p>
              <p>станет: {identityGate.impact.after.join(' | ') || '—'}</p>
            </div>
            <p className="text-xs text-fg4">
              Связки придётся завести заново — на вкладке «Документы качества» в документе.
            </p>
          </div>
        )}
        confirmLabel="Сохранить схему"
        requireCheckbox="Понимаю, что связки материалов перестанут находиться"
        onConfirm={() => { identityGate?.decide(true); setIdentityGate(null); }}
      />

      {/* Предложение миграции данных при переименовании ключа сохранённого поля (issue #357). */}
      <ConfirmDialog
        open={!!pendingMigration}
        onOpenChange={o => { if (!o) setPendingMigration(null); }}
        title="Перенести данные документов на новый ключ?"
        description={pendingMigration && (
          <div className="space-y-1">
            <p>Ключ(и) поля изменены. Перенести значения существующих документов этого типа со старого ключа на новый?</p>
            <ul className="text-xs font-mono text-fg3">
              {pendingMigration.map(r => <li key={r.from}>{r.from} → {r.to}</li>)}
            </ul>
            {/* issue #737: ключ держат не только реквизиты — привязки наборов ссылаются на него
                своим целевым полем и ключами маппинга. Переносим вместе, иначе привязка осиротеет
                и перестанет заполнять поле молча. */}
            <p className="text-xs text-fg4">
              Вместе с данными переедут привязки наборов данных и шаблоны привязок, нацеленные на этот ключ.
            </p>
            <p className="text-xs text-fg4">Без переноса старые значения останутся под прежним ключом (осиротеют) — их потом покажет «Аудит».</p>
          </div>
        )}
        confirmLabel="Перенести данные"
        onConfirm={async () => {
          const renames = pendingMigration ?? [];
          setPendingMigration(null);
          let docs = 0, bindings = 0, templates = 0;
          for (const r of renames) {
            try {
              const res = await migrateKey.mutateAsync({ oldKey: r.from, newKey: r.to });
              docs += res.migrated; bindings += res.bindings; templates += res.templates;
            }
            catch { schemaToast.error(`Не удалось перенести «${r.from}»`); }
          }
          // Привязки называем только когда они были: в обычном переименовании их нет, и нулевой
          // счётчик в тосте — шум.
          const extra = [
            bindings > 0 ? `привязок: ${bindings}` : null,
            templates > 0 ? `шаблонов: ${templates}` : null,
          ].filter(Boolean).join(', ');
          schemaToast.success(`Перенесено документов: ${docs}${extra ? `; ${extra}` : ''}`);
        }}
      />
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
function TypeDetail({ docType, allDocTypes, allGroups, onDeleted, dirty, saving, onSaveAll, onRevert, onDuplicate, onSelectType }: {
  docType: DocumentType; allDocTypes: DocumentType[]; allGroups: string[]; onDeleted: () => void;
  dirty: boolean; saving: boolean; onSaveAll: () => Promise<void>; onRevert: () => void; onDuplicate: () => void;
  onSelectType: (id: string) => void;
}) {
  const deleteMutation = useDeleteDocumentType();
  const { data: usage } = useDocumentTypeUsage(docType.id);
  const toast = useToast();
  const groupMutation = useSetDocumentTypeGroup();
  const [templatesOpen, setTemplatesOpen] = useState(false);
  const [deleteConfirmOpen, setDeleteConfirmOpen] = useState(false);
  const [auditOpen, setAuditOpen] = useState(false);

  const effectiveFields = resolveEffectiveFields(docType, allDocTypes);
  const ownFieldCount = parseSchemaFields(docType.schema).length;
  const parentType = docType.parentId ? allDocTypes.find(dt => dt.id === docType.parentId) : null;
  // Типы, ссылающиеся на этот тип полем complex/array/doc-ref/doc-array (для бейджа «используется: N»).
  const referencedBy = findReferencingTypes(docType.id, allDocTypes);
  // Проактивное использование (issue #275): полный набор причин с backend (объекты/шаблоны/качество/
  // привязки/материализация/наследники/подтип). При наличии — диалог удаления открывается сразу в
  // состоянии «нельзя» со списком причин; реактивный 409 остаётся страховкой от гонок.
  const usageReasons = usage?.reasons ?? [];
  const deleteBlockedNode = usageReasons.length > 0 ? (
    <div>
      <p className="mb-1.5 font-medium">Тип используется — сначала снимите зависимости:</p>
      <ul className="list-disc pl-4 space-y-0.5">
        {usageReasons.map(r => (
          <li key={r.kind}>
            {r.label}{r.names.length > 0 ? `: ${r.names.join(', ')}` : r.count > 0 ? `: ${r.count}` : ''}
          </li>
        ))}
      </ul>
    </div>
  ) : undefined;
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
  // Переход по иерархии в обе стороны (issue #784): бейдж родителя и чипы прямых наследников —
  // кнопки, а не подписи. Дойти до родителя иначе можно было только поиском в списке слева, хотя
  // на него ссылается вся работа с наследованием («Как у родителя», «Ключ уже есть в родительском
  // типе»). Родитель и наследники всегда того же kind, так что переход не покидает страницу.
  const jumpBadge = `${badge} truncate max-w-[200px] transition-colors focus-visible:outline-none `
    + 'focus-visible:ring-2 focus-visible:ring-brand focus-visible:ring-offset-2 focus-visible:ring-offset-surface';
  // kind сверяем сами: клиентские пикеры родителя его держат, а команды создания/обновления
  // валидируют только циклы и уникальность — из восстановления бэкапа кросс-kind связь пройдёт.
  const children = allDocTypes
    .filter(t => t.parentId === docType.id && t.kind === docType.kind)
    .sort((a, b) => a.name.localeCompare(b.name, 'ru'));

  // Состояние типа (issue #794): красный чип — то, из-за чего схема не сохранится, жёлтый —
  // «нездоров, но сохраняется». Считаем по сохранённой схеме: правку показывает сама форма, а
  // сюда тип может приехать не своей правкой, а изменением предка.
  const schemaDef = docType.schema as unknown as SchemaDefinition;
  const health = typeHealth(
    parseSchemaFields(docType.schema),
    parentType ? resolveEffectiveFields(parentType, allDocTypes).map(f => f.key) : [],
    parentType ? chainFieldKeys(parentType, allDocTypes) : [],
    {
      groups: schemaDef.groups,
      excludedFields: schemaDef.excludedFields,
      ungroupedOrder: schemaDef.ungroupedOrder,
      fieldOverrides: schemaDef.fieldOverrides,
    },
    schemaDef.typstRenders ?? [],
  );
  const healthLabel = healthBadgeLabel(health);
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
                <button type="button" onClick={() => onSelectType(parentType.id)}
                  title="Перейти к родительскому типу"
                  className={`${jumpBadge} bg-brand-subtle text-brand hover:text-brand-hover hover:underline`}>
                  ↑ {parentType.name}
                </button>
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
              {healthLabel.blocking && (
                <span className={`${badge} bg-danger-subtle text-danger`}
                  title={health.blocking.map(b => `• ${b.text}`).join(String.fromCharCode(10))}>
                  ⚠ {healthLabel.blocking}
                </span>
              )}
              {healthLabel.soft && (
                <span className={`${badge} bg-warning-subtle text-warning`}
                  title={health.soft.map(i => `• ${i.text}`).join(String.fromCharCode(10))}>
                  {healthLabel.soft}
                </span>
              )}
            </div>
            {children.length > 0 && (
              <div className="flex items-center gap-1.5 mt-2 flex-wrap">
                <span className="text-xs text-fg4 shrink-0">наследники:</span>
                {children.map(c => (
                  <button key={c.id} type="button" onClick={() => onSelectType(c.id)}
                    title={`Перейти к типу «${c.name}»`}
                    className={`${jumpBadge} bg-muted text-fg2 hover:text-brand-hover hover:underline`}>
                    ↓ {c.name}
                  </button>
                ))}
              </div>
            )}
          </>
        }
        actions={
          <>
            <GroupPicker groups={allGroups} value={docType.group}
              onChange={group => groupMutation.mutate({ id: docType.id, group },
                { onError: e => toast.apiError(e, 'Не удалось изменить группу типа') })} />
            <RowActionsMenu ariaLabel="Действия типа" actions={[
              { key: 'dup', label: 'Дублировать', icon: <Copy size={14} />, onSelect: onDuplicate },
              { key: 'audit', label: 'Аудит инстансов', icon: <ShieldCheck size={14} />, onSelect: () => setAuditOpen(true) },
              ...(docType.kind === 'Document'
                ? [{ key: 'tpl', label: 'Шаблоны данных', icon: <Database size={14} />, onSelect: () => setTemplatesOpen(true) }]
                : []),
              { key: 'del', label: 'Удалить тип', danger: true, disabled: deleteMutation.isPending,
                icon: <Trash2 size={14} />, onSelect: () => setDeleteConfirmOpen(true) },
            ]} />
          </>
        } />
      {/* Тело редактора (существующие редакторы как есть — Фаза A) */}
      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-5">
        <div className="mx-auto max-w-4xl">
          <PropertiesEditor docType={docType} allDocTypes={allDocTypes} />
          <SchemaEditor docType={docType} allDocTypes={allDocTypes} onSelectType={onSelectType} />
        </div>
      </div>
      {templatesOpen && (
        <BindingTemplatesDialog docType={docType} allDocTypes={allDocTypes} onClose={() => setTemplatesOpen(false)} />
      )}
      <TypeAuditModal typeId={docType.id} typeName={docType.name}
        schemaFieldKeys={effectiveFields.map(f => f.key)}
        open={auditOpen} onClose={() => setAuditOpen(false)} />
      <ConfirmDialog
        open={deleteConfirmOpen}
        onOpenChange={setDeleteConfirmOpen}
        title={`Удалить тип «${docType.name}»?`}
        description={<p>Это повлияет на все документы и шаблоны, использующие этот тип. Действие необратимо.</p>}
        confirmLabel={`Удалить тип «${docType.name}»`}
        requireCheckbox="Понимаю, что это необратимо"
        blocked={deleteBlockedNode}
        onConfirm={() => deleteMutation.mutateAsync(docType.id).then(onDeleted)}
      />
    </div>
  );
}

// ─── Page (parameterised by kind) ──────────────────────────────────────────────

interface TypesPageProps {
  kind: DocumentTypeKind;
}

// Выбор в URL + память последнего открытого — общий хелпер (issue #787). Обе страницы («Типы
// документов» и «Составные типы») — один компонент, поэтому память раздельная по kind.
const lastTypeKey = (kind: DocumentTypeKind) => `types-last:${kind}`;
const SELECTION_KEYS = ['type'] as const;

export function DocumentTypesPage({ kind }: TypesPageProps) {
  const [createOpen, setCreateOpen] = useState(false);
  const [query, setQuery] = useState('');

  // Порядок разрешения при входе: `?type=` → localStorage → первый в списке (страхует `?? filtered[0]`
  // ниже, поэтому удалённый или не проходящий kind-фильтр id просто игнорируется).
  const navigate = useNavigate();
  const { values, remember } = useRememberedSelection(lastTypeKey(kind), SELECTION_KEYS);
  const selectedId = values.type || null;
  const setSelectedId = (id: string | null) => remember({ type: id ?? '' });
  const { data: allDocTypes = [], isLoading } = useListDocumentTypes();

  // Реестр незасохранённых форм текущего типа (явное сохранение, issue #197 / #210 — общий).
  const { registry, anyDirty, saving, saveAll, resetAll } = useTypeEditorRegistry();

  // Гард при уходе с типа с несохранёнными правками (общий useDirtyGuard, issue #210 Этап 1b).
  // Всё, что делает переход, живёт в onCommit — то есть ПОСЛЕ подтверждения: отменив диалог,
  // пользователь должен остаться ровно там же, с тем же поиском.
  const { request, dialogProps } = useDirtyGuard<{ id: string | null; jump?: boolean }>({
    isDirty: anyDirty, saving, saveAll,
    onCommit: ({ id, jump }) => {
      // Переход из панели проверки Typst-блоков может указать на тип другого kind (граф блоков
      // строится по всем типам): такой id страница не покажет — `filtered` его не содержит, и
      // выбор молча съехал бы на первый тип, а память страницы осталась бы отравленной. Уводим
      // на соседнюю страницу — гард уже отработал, правки сохранены или отброшены осознанно.
      const target = id ? allDocTypes.find(t => t.id === id) : null;
      if (target && target.kind !== kind) {
        navigate(`/${target.kind === 'Composite' ? 'composite-types' : 'document-types'}?type=${target.id}`);
        return;
      }
      setSelectedId(id);
      // Программный переход обязан сделать цель видимой: при активном поиске тип открывается в
      // детали, но в рейле его нет — ни строки, ни подсветки, и непонятно, где ты оказался.
      if (jump) setQuery('');
    },
  });
  const requestSelect = (id: string) => { if (id !== selectedId) request({ id }); };
  // Переход по иерархии и из панели блоков (issue #784).
  const jumpToType = (id: string) => { if (id !== selectedId) request({ id, jump: true }); };

  // Гард ухода со страницы по маршруту (issue #307): сайдбар-навигация перехватывается, показываем
  // тот же диалог. `routeLeave` хранит отложенный переход (proceed).
  const [routeLeave, setRouteLeave] = useState<(() => void) | null>(null);
  useLeaveGuard(anyDirty, (proceed) => setRouteLeave(() => proceed));

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

  // Заголовок вкладки: показанный тип замещает раздел. Именно показанный, а не `selectedId`:
  // тот приходит из URL/памяти и может называть удалённый тип или тип другого kind.
  useDocumentTitle(selected
    ? `${kind === 'Composite' ? 'Составной тип' : 'Тип'} «${selected.name}»`
    : null);

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
              onDuplicate={() => duplicateType(selected)} onSelectType={jumpToType} />
          ) : (
            <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Ничего не найдено</div>
          )} />
      </TypeEditorProvider>

      <LeaveGuardDialog {...dialogProps} />

      {/* Гард ухода по маршруту (сайдбар/перезагрузка), issue #307 */}
      <LeaveGuardDialog
        open={routeLeave !== null} saving={saving}
        onCancel={() => setRouteLeave(null)}
        onDiscard={() => { const p = routeLeave; setRouteLeave(null); p?.(); }}
        onSave={async () => {
          try { await saveAll(); const p = routeLeave; setRouteLeave(null); p?.(); }
          catch { setRouteLeave(null); /* ошибка показана в форме */ }
        }} />

      <Modal open={createOpen} onOpenChange={setCreateOpen}
        title={kind === 'Document' ? 'Новый тип документа' : 'Новый составной тип'}
        wide flushBody>
        {createOpen && (
          <CreateForm kind={kind} onClose={() => setCreateOpen(false)}
            onCreated={created => setSelectedId(created.id)} allDocTypes={allDocTypes} />
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
  // Группы навигации: храним только то, что пользователь переключил руками (true — раскрыл,
  // false — свернул). Нетронутая группа раскрыта, если в ней лежит выбранный тип — иначе
  // восстановленный при входе выбор (#778) виден только в детали, а рейл стоит свёрнутым и без
  // единой подсветки. При активном поиске все группы раскрыты, чтобы результаты были видны.
  //
  // Сворачивание группы с выбранным типом помним вместе с самим выбором (ключ «тип:группа»):
  // иначе оно пережило бы и смену группы у открытого типа, и переход выбора в свёрнутую ранее
  // группу — тип пропал бы из рейла, а подсветки не было бы вовсе.
  const [toggled, setToggled] = useState<Map<string, boolean>>(new Map());
  const searching = query.trim().length > 0;
  // Строку выбранного типа подкручиваем в видимую часть: раскрыть группу мало — на длинном списке
  // цель программного перехода (#784) осталась бы ниже сгиба, то есть снова «не видно, где ты».
  const activeRow = useRef<HTMLButtonElement | null>(null);
  useEffect(() => { activeRow.current?.scrollIntoView({ block: 'nearest' }); }, [selectedId]);
  const selectedGroup = [...byGroup].find(([, items]) => items.some(t => t.id === selectedId))?.[0];
  const groupKey = (g: string) => g === selectedGroup ? `${selectedId}:${g}` : g;
  const groupOpen = (g: string) => toggled.get(groupKey(g)) ?? g === selectedGroup;
  // Считаем от того, что нарисовано (`searching` форсирует раскрытие): клик по шапке во время
  // поиска иначе записывал бы обратное тому, о чём просил пользователь. Предыдущее значение
  // читаем из аргумента, а не из замыкания, — два клика в одном такте не схлопываются в один.
  const toggleGroup = (g: string) => {
    const key = groupKey(g);
    setToggled(m => new Map(m).set(key, !(searching || (m.get(key) ?? g === selectedGroup))));
  };

  const typeRow = (t: DocumentType) => {
    const active = t.id === selectedId;
    const Icon = t.kind === 'Composite' ? Boxes : FileText;
    return (
      <button key={t.id} type="button" onClick={() => onSelect(t.id)}
        ref={active ? activeRow : undefined}
        aria-current={active ? 'true' : undefined}
        className={`w-full flex items-center gap-2.5 px-3 h-11 rounded-full text-left transition-colors ${
          active ? 'bg-brand-subtle text-brand-hover font-medium' : 'text-fg2 hover:bg-muted'}`}>
        <Icon size={17} className="shrink-0" />
        <span className="flex-1 truncate text-sm">{t.name}</span>
        <span className="text-xs text-fg4 shrink-0">{fieldCount(t, allDocTypes)}</span>
      </button>
    );
  };

  // Открытый тип, не прошедший поиск, остаётся в рейле отдельной строкой (issue #792). Программный
  // переход (#784) снимает поиск сам, а вот набранный руками запрос иначе прятал бы открытый тип:
  // деталь показывает его, а в списке ни строки, ни подсветки.
  const listedIds = new Set([...byGroup.values()].flat().map(t => t.id));
  const outside = selectedId && !listedIds.has(selectedId)
    ? allDocTypes.find(t => t.id === selectedId) ?? null : null;

  return (
    <>
      <NavSearchInput value={query} onChange={onQuery} placeholder="Поиск типа…" />
      <div className="flex-1 overflow-y-auto px-2 pb-3">
        {outside && (
          <>
            <div className="px-3 pt-3 pb-1 text-[11px] font-semibold uppercase tracking-wide text-fg4">Открыт, вне поиска</div>
            {typeRow(outside)}
          </>
        )}
        {groupOrder.length === 0 && (
          <p className="px-3 py-6 text-center text-sm text-fg4">
            {outside ? 'Больше ничего не найдено' : 'Ничего не найдено'}
          </p>
        )}
        {groupOrder.map(g => {
          const items = byGroup.get(g)!;
          const open = searching || groupOpen(g);
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
              {open && items.map(typeRow)}
            </div>
          );
        })}
      </div>
    </>
  );
}
