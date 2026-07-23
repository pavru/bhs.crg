import { useId, useMemo, useState } from 'react';
import { Plus, Trash2, ArrowUp, ArrowDown, Cpu, GripVertical, ChevronDown, ChevronUp, Lock, Link2 } from 'lucide-react';
import { DateInput } from '@/shared/ui/DateInput';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { TypePicker, type PickType } from '@/shared/ui/TypePicker';
import type { DocumentType, PrimitiveTypeDef, EnumTypeDef } from '@/shared/api/types';
import type { SchemaField, FieldGroup } from '@/shared/api/schema';
import { TYPE_LABELS, toCamelKey, nextAutoKey } from './schemaConstants';
import { useTagRegistry, fieldTags, type TagDefinition } from '@/shared/api/tags';
// ─── JSON preview ──────────────────────────────────────────────────────────────

export function JsonPreview({
  fields, groups, excludedFields, fieldOverrides,
}: {
  fields: SchemaField[];
  groups: FieldGroup[];
  excludedFields: string[];
  fieldOverrides: Record<string, { required?: boolean; defaultValue?: unknown }>;
}) {
  const json = JSON.stringify(
    {
      fields,
      ...(groups.length ? { groups } : {}),
      ...(excludedFields.length ? { excludedFields } : {}),
      ...(Object.keys(fieldOverrides).length ? { fieldOverrides } : {}),
    },
    null, 2,
  );
  return (
    <pre
      className="rounded-lg px-4 py-3 text-xs font-mono overflow-x-auto whitespace-pre leading-relaxed select-all"
      style={{ background: '#161616', color: '#d4d4d4' }}
    >
      {json}
    </pre>
  );
}

// ─── Field card (single own field, collapsible) ────────────────────────────────

/** Реестры типов/тэгов, общие для карточки поля — прокидываются и в плоский FieldBuilder,
 *  и в группированный GroupedFieldsEditor (issue #197 Фаза C). */
export interface FieldRegistries {
  compositeTypes: DocumentType[];
  primitiveTypes: PrimitiveTypeDef[];
  enumTypes: EnumTypeDef[];
  allDocTypes: DocumentType[];
  tagRegistry: TagDefinition[] | undefined;
}

/** Человеко-имя функц. тэга по коду (фолбэк — сам код). */
export function tagLabelOf(tagRegistry: TagDefinition[] | undefined, code: string): string {
  return (tagRegistry ?? []).find(t => t.code === code)?.label ?? code;
}

/** Краткая метка типа поля для свёрнутой карточки. */
export function fieldTypeSummary(f: SchemaField, reg: FieldRegistries): string {
  if (f.type === 'complex' || f.type === 'array') {
    const ct = reg.compositeTypes.find(c => c.id === f.typeId);
    return ct ? ct.name : (f.type === 'array' ? 'Массив' : 'Составной');
  }
  if (f.type === 'enum') { const et = reg.enumTypes.find(e => e.id === f.typeId); return et ? et.name : 'Перечисление'; }
  if (f.type === 'primitive') { const pt = reg.primitiveTypes.find(p => p.id === f.typeId); return pt ? pt.name : 'Тип поля'; }
  return TYPE_LABELS[f.type] ?? f.type;
}

// ─── Пикер типа поля (issue #197, переиспользует общий TypePicker) ──────────────
// Каждый выбираемый тип поля кодируется одним PickType с id вида "kind::targetId":
// базовые скаляры, реестр типов полей/перечислений, составные (одиночно/список), ссылки на
// документы (одиночно/список). onSelect декодирует id обратно в пару {type, typeId}.
const BUILTIN_TYPES: { type: SchemaField['type']; label: string }[] = [
  { type: 'string', label: 'Строка' },
  { type: 'text', label: 'Текст' },
  { type: 'number', label: 'Число' },
  { type: 'date', label: 'Дата' },
  { type: 'boolean', label: 'Флаг' },
  { type: 'image', label: 'Изображение' },
  { type: 'file', label: 'Файл (вложение)' },
];

/** Плоский список выбираемых типов поля для TypePicker (сгруппирован по section). */
export function buildFieldTypeOptions(reg: FieldRegistries): PickType[] {
  const opts: PickType[] = [];
  for (const b of BUILTIN_TYPES) opts.push({ id: `builtin::${b.type}`, name: b.label, code: b.type, section: 'Базовые' });
  for (const pt of reg.primitiveTypes) opts.push({ id: `primitive::${pt.id}`, name: pt.name, code: pt.code, section: 'Типы полей (реестр)' });
  for (const et of reg.enumTypes) opts.push({ id: `enum::${et.id}`, name: `${et.name} · ${et.values.length}`, code: et.code, section: 'Перечисления' });
  for (const ct of reg.compositeTypes) opts.push({ id: `complex::${ct.id}`, name: ct.name, code: ct.code, section: 'Составные типы' });
  for (const ct of reg.compositeTypes) opts.push({ id: `array::${ct.id}`, name: `${ct.name} — список`, code: ct.code, section: 'Списки (массивы)' });
  const docs = reg.allDocTypes.filter(dt => dt.kind === 'Document');
  for (const dt of docs) opts.push({ id: `doc-ref::${dt.id}`, name: dt.name, code: dt.code, section: 'Ссылки на документы' });
  for (const dt of docs) opts.push({ id: `doc-array::${dt.id}`, name: `${dt.name} — список`, code: dt.code, section: 'Списки документов' });
  return opts;
}

/** Декодирует id из buildFieldTypeOptions обратно в патч {type, typeId}. */
export function decodeFieldType(id: string): Pick<SchemaField, 'type' | 'typeId'> {
  const sep = id.indexOf('::');
  const kind = id.slice(0, sep);
  const target = id.slice(sep + 2);
  if (kind === 'builtin') return { type: target as SchemaField['type'], typeId: undefined };
  return { type: kind as SchemaField['type'], typeId: target };
}

interface FieldCardProps {
  field: SchemaField;
  reg: FieldRegistries;
  keyConflict: boolean;
  /** Поле ещё НЕ сохранено (issue #355): ключ авто-следует за именем. У сохранённого — заморожен. */
  isNew: boolean;
  /** Смена ключа СОХРАНЁННОГО поля (issue #357) — для предложения миграции данных на сохранении схемы. */
  onKeyRename?: (from: string, to: string) => void;
  open: boolean;
  onToggleOpen: () => void;
  onChange: (patch: Partial<SchemaField>) => void;
  onRemove: () => void;
  onMoveUp?: () => void;
  onMoveDown?: () => void;
  isFirst?: boolean;
  isLast?: boolean;
  // Drag&drop (управляется контейнером): dragging — эта карточка перетаскивается.
  dragging?: boolean;
  onDragStart?: () => void;
  onDragEnd?: () => void;
  onDragOver?: (e: React.DragEvent) => void;
  onDrop?: (e: React.DragEvent) => void;
}

/** Одно СВОЁ поле: свёрнутая шапка (drag-ручка + сводка + chip типа + chevron) → раскрытый
 *  редактор (все ветки типов/опций/тэгов). Индекс-независима — переиспользуется в плоском и
 *  группированном представлении (issue #197 Фаза C). */
export function FieldCard({
  field, reg, keyConflict, isNew, onKeyRename, open, onToggleOpen, onChange, onRemove,
  onMoveUp, onMoveDown, isFirst, isLast, dragging, onDragStart, onDragEnd, onDragOver, onDrop,
}: FieldCardProps) {
  const { primitiveTypes, enumTypes, tagRegistry } = reg;
  const tags = field.tags ?? [];
  const [pickerOpen, setPickerOpen] = useState(false);
  // Стабильность ключа (issue #355): у сохранённого поля ключ заморожен (read-only + замок), меняется
  // только явной разблокировкой с предупреждением о дрейфе данных. originalKey фиксируем на монтировании
  // (для сохранённого = персистентный ключ) — по нему показываем warning, пока ключ изменён.
  const [keyUnlocked, setKeyUnlocked] = useState(false);
  const [confirmUnlock, setConfirmUnlock] = useState(false);
  const [originalKey] = useState(field.key);
  const keyLocked = !isNew && !keyUnlocked;
  const keyChanged = !isNew && field.key !== originalKey;
  const keyAutoNew = isNew && !!field.title.trim() && field.key === toCamelKey(field.title);
  const pickTypes = useMemo(() => buildFieldTypeOptions(reg),
    [reg.compositeTypes, reg.primitiveTypes, reg.enumTypes, reg.allDocTypes]); // eslint-disable-line react-hooks/exhaustive-deps

  const toggleTag = (code: string) => {
    const next = tags.includes(code) ? tags.filter(c => c !== code) : [...tags, code];
    onChange({ tags: next.length ? next : undefined });
  };
  // Для поля type="primitive" применимые тэги берём из самого типа поля (allowedTags),
  // для встроенных типов — из реестра по типу поля.
  const applicableTags = (() => {
    if (field.type === 'primitive') {
      const pt = primitiveTypes.find(p => p.id === field.typeId);
      const codes = new Set(pt?.allowedTags ?? []);
      return (tagRegistry ?? []).filter(t => t.scope === 'Field' && codes.has(t.code));
    }
    return fieldTags(tagRegistry, field.type);
  })();
  // Легаси enum-поле (issue #59): options инлайн, typeId не выбран.
  const isLegacyEnum = field.type === 'enum' && !field.typeId && (field.options?.length ?? 0) > 0;
  const enumTypeDef = field.type === 'enum' ? enumTypes.find(et => et.id === field.typeId) : undefined;
  // Ключ следует за именем только у НОВОГО поля, пока не тронут вручную (issue #355). У сохранённого —
  // заморожен: переименование НЕ меняет ключ (иначе данные документов осиротеют).
  const updateTitle = (title: string) => {
    const key = nextAutoKey(field.key, field.title, title, isNew);
    onChange({ title, ...(key !== field.key ? { key } : {}) });
  };

  return (
    <div
      className={`border rounded-lg overflow-hidden bg-surface transition-colors ${dragging ? 'border-brand' : 'border-stroke'}`}
      onDragOver={onDragOver}
      onDrop={onDrop}>
      {/* Свёрнутая шапка карточки: drag handle + сводка + chip типа + chevron */}
      <div className="flex items-center gap-2 pl-2 pr-3 py-2 hover:bg-muted/40 transition-colors"
        draggable onDragStart={onDragStart} onDragEnd={onDragEnd}>
        <GripVertical size={15} className="text-fg4 shrink-0 cursor-grab" />
        <button type="button" onClick={onToggleOpen}
          className="flex-1 min-w-0 flex items-center gap-2 text-left">
          <span className="min-w-0 flex-1">
            <span className="flex items-center gap-1.5">
              <span className="text-sm font-medium text-fg1 truncate">{field.title || '(без названия)'}</span>
              {field.required && <span className="text-[11px] text-danger shrink-0">обяз.</span>}
              {keyConflict && <span className="text-[11px] text-danger shrink-0">! ключ занят</span>}
            </span>
            <span className="block text-xs text-fg4 font-mono truncate">{field.key || '—'}</span>
          </span>
          {tags.slice(0, 2).map(tc => (
            <span key={tc} className="hidden md:inline text-[11px] px-1.5 py-0.5 rounded bg-brand-subtle text-brand shrink-0">{tagLabelOf(tagRegistry, tc)}</span>
          ))}
          {tags.length > 2 && <span className="text-[11px] text-fg4 shrink-0">+{tags.length - 2}</span>}
          <span className="text-xs px-2 py-0.5 rounded-full bg-muted text-fg3 shrink-0">{fieldTypeSummary(field, reg)}</span>
          {open ? <ChevronUp size={16} className="text-fg4 shrink-0" /> : <ChevronDown size={16} className="text-fg4 shrink-0" />}
        </button>
      </div>
      {open && (
      <div className="px-3 pb-3 pt-2 border-t border-stroke space-y-2">
      <div className="grid grid-cols-[1fr_1fr_160px_72px_76px] gap-2 items-center">
        {/* Title */}
        <input
          value={field.title}
          onChange={e => updateTitle(e.target.value)}
          placeholder="Название"
          className="border border-stroke-strong rounded-md px-3 py-1.5 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface"
        />
        {/* Key — стабильный идентификатор хранения (issue #355) */}
        <div className="relative">
          <input
            value={field.key}
            onChange={e => { onChange({ key: e.target.value }); if (!isNew) onKeyRename?.(originalKey, e.target.value); }}
            readOnly={keyLocked}
            placeholder="КлючПоля"
            spellCheck={false}
            title={keyLocked ? 'Идентификатор хранения — заморожен. Нажмите замок, чтобы изменить.' : undefined}
            className={`w-full border rounded-md pl-3 pr-7 py-1.5 text-sm font-mono focus:outline-none focus-visible:ring-2 ${
              keyConflict ? 'border-danger focus-visible:ring-danger' : 'border-stroke-strong focus-visible:ring-brand'
            } ${keyLocked ? 'bg-muted/60 text-fg3 cursor-default' : 'bg-surface'}`}
          />
          {keyConflict ? (
            <span className="absolute right-2 top-1/2 -translate-y-1/2 text-xs text-danger">!</span>
          ) : keyLocked ? (
            <button type="button" onClick={() => setConfirmUnlock(true)}
              title="Изменить ключ (осторожно: осиротит данные документов)"
              className="absolute right-1.5 top-1/2 -translate-y-1/2 p-0.5 text-fg4 hover:text-fg2">
              <Lock size={13} />
            </button>
          ) : keyAutoNew ? (
            <span title="Ключ авто-генерируется из названия" className="absolute right-2 top-1/2 -translate-y-1/2 text-fg4 pointer-events-none">
              <Link2 size={13} />
            </span>
          ) : null}
        </div>
        {/* Type — открывает searchable TypePicker (issue #197) */}
        <button type="button" onClick={() => setPickerOpen(true)} title="Выбрать тип поля"
          className="flex items-center justify-between gap-1 border border-stroke-strong rounded-md px-2 py-1.5 text-sm bg-surface hover:bg-muted/50 focus:outline-none focus-visible:ring-2 focus-visible:ring-brand">
          <span className="truncate text-left">{fieldTypeSummary(field, reg)}</span>
          <ChevronDown size={14} className="text-fg4 shrink-0" />
        </button>
        {/* Required */}
        <label className="flex items-center justify-center gap-1.5 cursor-pointer">
          <input
            type="checkbox"
            checked={field.required}
            onChange={e => onChange({ required: e.target.checked })}
            className="w-4 h-4 rounded border-stroke-strong text-brand"
          />
          <span className="text-xs text-fg3">да</span>
        </label>
        {/* Actions */}
        <div className="flex items-center justify-end gap-0.5">
          <button type="button" onClick={onMoveUp} disabled={isFirst}
            className="p-1 text-fg4 hover:text-fg2 disabled:opacity-25">
            <ArrowUp size={12} />
          </button>
          <button type="button" onClick={onMoveDown} disabled={isLast}
            className="p-1 text-fg4 hover:text-fg2 disabled:opacity-25">
            <ArrowDown size={12} />
          </button>
          <button type="button" onClick={onRemove}
            className="p-1 text-fg4 hover:text-danger">
            <Trash2 size={12} />
          </button>
        </div>
      </div>
      {/* Предупреждение о дрейфе данных при изменённом ключе существующего поля (issue #355). */}
      {keyChanged && (
        <p className="text-xs text-danger flex items-start gap-1.5">
          <Lock size={12} className="shrink-0 mt-0.5" />
          <span>Ключ изменён с «<span className="font-mono">{originalKey}</span>» — данные существующих
            документов останутся под старым ключом (осиротеют). Перенос старый→новый — через «Аудит»
            (переименование осиротевшего ключа в поле схемы).</span>
        </p>
      )}
      <ConfirmDialog
        open={confirmUnlock}
        onOpenChange={setConfirmUnlock}
        title="Изменить ключ поля?"
        description={<p>Ключ — идентификатор хранения. После смены данные всех существующих документов
          этого типа осиротеют по старому ключу «<span className="font-mono">{originalKey}</span>».
          Перенести их на новый ключ можно через «Аудит» (переименование осиротевшего ключа). Продолжить?</p>}
        confirmLabel="Изменить ключ"
        onConfirm={() => { setConfirmUnlock(false); setKeyUnlocked(true); }}
      />
      {/* Default value editor */}
      {field.type !== 'complex' && field.type !== 'array' && field.type !== 'primitive'
        && field.type !== 'doc-ref' && field.type !== 'doc-array' && (
        <div className="ml-[calc(33%+0.5rem)] mr-[calc(5rem)] flex items-center gap-2">
          <span className="text-xs text-fg4 shrink-0 w-28">Значение по умолч.:</span>
          {field.type === 'boolean' ? (
            <label className="flex items-center gap-1.5 cursor-pointer">
              <input
                type="checkbox"
                checked={!!field.defaultValue}
                onChange={e => onChange({ defaultValue: e.target.checked ? true : undefined })}
                className="w-3.5 h-3.5 rounded border-stroke-strong text-brand"
              />
              <span className="text-xs text-fg3">{field.defaultValue ? 'true' : 'не задано'}</span>
            </label>
          ) : field.type === 'enum' ? (
            <select
              value={String(field.defaultValue ?? '')}
              onChange={e => onChange({ defaultValue: e.target.value || undefined })}
              className="flex-1 border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand"
            >
              <option value="">— не задано —</option>
              {enumTypeDef
                ? enumTypeDef.values.map(v => <option key={v.code} value={v.code}>{v.label}</option>)
                : (field.options ?? []).filter(o => o).map(opt => (
                    <option key={opt} value={opt}>{opt}</option>
                  ))}
            </select>
          ) : field.type === 'date' ? (
            <DateInput
              value={field.defaultValue != null ? String(field.defaultValue) : ''}
              onChange={v => onChange({ defaultValue: v || undefined })}
              className="flex-1 border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand"
            />
          ) : (
            <input
              type={field.type === 'number' ? 'number' : 'text'}
              placeholder="не задано"
              value={field.defaultValue != null ? String(field.defaultValue) : ''}
              onChange={e => {
                const v = e.target.value;
                onChange({
                  defaultValue: v === '' ? undefined
                    : field.type === 'number' ? Number(v) : v,
                });
              }}
              className="flex-1 border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand"
            />
          )}
        </div>
      )}
      {/* Enum options editor — только легаси (options инлайн, без typeId, issue #59) */}
      {isLegacyEnum && (
        <div className="ml-[calc(33%+0.5rem)] mr-[calc(5rem)] space-y-1.5">
          {(field.options ?? []).map((opt, oi) => (
            <div key={oi} className="flex items-center gap-1.5">
              <input
                value={opt}
                onChange={e => {
                  const opts = [...(field.options ?? [])];
                  opts[oi] = e.target.value;
                  onChange({ options: opts });
                }}
                placeholder={`Вариант ${oi + 1}`}
                className="flex-1 border border-stroke-strong rounded px-2 py-1 text-xs focus:outline-none focus-visible:ring-1 focus-visible:ring-brand bg-surface"
              />
              <button type="button"
                onClick={() => {
                  const opts = (field.options ?? []).filter((_, j) => j !== oi);
                  onChange({ options: opts });
                }}
                className="p-0.5 text-fg4 hover:text-danger">
                <Trash2 size={11} />
              </button>
            </div>
          ))}
          <button type="button"
            onClick={() => onChange({ options: [...(field.options ?? []), ''] })}
            className="flex items-center gap-1 text-xs text-brand hover:text-brand-hover">
            <Plus size={11} /> Добавить вариант
          </button>
        </div>
      )}
      {/* Функциональные тэги поля (для primitive — из типа поля, иначе из реестра) */}
      {applicableTags.length > 0 && (
        <div className="ml-[calc(33%+0.5rem)] mr-[calc(5rem)] flex items-start gap-2">
          <Cpu size={12} className={`mt-1 ${tags.length ? 'text-purple-500' : 'text-stroke-strong'}`} />
          <span className="text-xs text-fg4 shrink-0 w-28 mt-1">Функц. тэги:</span>
          <div className="flex flex-wrap gap-1.5">
            {applicableTags.map(t => {
              const on = tags.includes(t.code);
              return (
                <button
                  key={t.code}
                  type="button"
                  title={t.description}
                  onClick={() => toggleTag(t.code)}
                  className={`px-2 py-0.5 rounded-full text-xs border transition-colors ${
                    on
                      ? 'bg-purple-500/15 border-purple-400 text-purple-700'
                      : 'border-stroke text-fg4 hover:border-stroke-strong hover:text-fg2'
                  }`}
                >
                  {t.label}
                </button>
              );
            })}
          </div>
        </div>
      )}
      </div>
      )}
      {/* Searchable-пикер типа поля (issue #197): один список — базовые + реестры + составные/ссылки */}
      <TypePicker
        open={pickerOpen}
        onOpenChange={setPickerOpen}
        types={pickTypes}
        recentKey="field-type"
        title="Тип поля"
        onSelect={id => {
          const dec = decodeFieldType(id);
          const changed = dec.type !== field.type || dec.typeId !== field.typeId;
          onChange(changed ? { ...dec, defaultValue: undefined, options: undefined } : dec);
        }}
      />
    </div>
  );
}

// ─── Field builder (own fields, flat list) ─────────────────────────────────────

interface FieldBuilderProps {
  fields: SchemaField[];
  onChange: (fields: SchemaField[]) => void;
  disabledKeys?: Set<string>;
  /** Ключи полей из СОХРАНЁННОЙ схемы (issue #355): их ключи заморожены. Пусто (по умолч.) = все поля
   *  новые (форма создания типа) → ключ авто-следует за именем. */
  persistedKeys?: Set<string>;
  compositeTypes: DocumentType[];
  primitiveTypes: PrimitiveTypeDef[];
  enumTypes: EnumTypeDef[];
  allDocTypes: DocumentType[];
}

export function FieldBuilder({ fields, onChange, disabledKeys, persistedKeys, compositeTypes, primitiveTypes, enumTypes, allDocTypes }: FieldBuilderProps) {
  const uid = useId();
  const { data: tagRegistry } = useTagRegistry();
  const reg: FieldRegistries = { compositeTypes, primitiveTypes, enumTypes, allDocTypes, tagRegistry };

  const add = () => onChange([...fields, { key: '', title: '', type: 'string', required: false }]);
  // Раскрытие карточки (одна за раз) и drag-and-drop переупорядочивания (issue #197 Фаза B).
  const [openIndex, setOpenIndex] = useState<number | null>(null);
  const [dragIndex, setDragIndex] = useState<number | null>(null);
  const move = (from: number, to: number) => {
    if (from === to) return;
    const next = [...fields];
    const [moved] = next.splice(from, 1);
    next.splice(to, 0, moved);
    onChange(next);
    setOpenIndex(o => o === from ? to : o);
  };

  return (
    <div className="space-y-2">
      {fields.map((field, i) => (
        <FieldCard
          key={`${uid}-${i}`}
          field={field}
          reg={reg}
          keyConflict={!!field.key && !!disabledKeys?.has(field.key.trim())}
          isNew={!persistedKeys?.has(field.key.trim())}
          open={openIndex === i}
          onToggleOpen={() => setOpenIndex(o => o === i ? null : i)}
          onChange={patch => onChange(fields.map((f, idx) => idx === i ? { ...f, ...patch } : f))}
          onRemove={() => onChange(fields.filter((_, idx) => idx !== i))}
          onMoveUp={() => move(i, i - 1)}
          onMoveDown={() => move(i, i + 1)}
          isFirst={i === 0}
          isLast={i === fields.length - 1}
          dragging={dragIndex === i}
          onDragStart={() => setDragIndex(i)}
          onDragEnd={() => setDragIndex(null)}
          onDragOver={dragIndex !== null ? e => e.preventDefault() : undefined}
          onDrop={dragIndex !== null ? () => { move(dragIndex, i); setDragIndex(null); } : undefined}
        />
      ))}
      <Button type="button" variant="tonal" onClick={add} icon={<Plus size={14} />} className="w-full justify-center">
        Добавить поле
      </Button>
    </div>
  );
}


// ─── Default value cell (module-level to avoid remount on each render) ────────

export function DefaultValueCell({ field, override, enumTypes, onOverrideDefaultValue }: {
  field: SchemaField;
  override?: { required?: boolean; defaultValue?: unknown };
  enumTypes: EnumTypeDef[];
  onOverrideDefaultValue: (key: string, value: unknown) => void;
}) {
  const isPrimitive = field.type !== 'complex' && field.type !== 'array' && field.type !== 'primitive';
  if (!isPrimitive) return <span className="text-xs text-stroke-strong">—</span>;

  const cur = override?.defaultValue;
  const hasDv = cur !== undefined;
  const parentDv = field.defaultValue;
  const inputCls = 'w-full border rounded px-1.5 py-0.5 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand ' + (hasDv ? 'border-brand-subtle' : 'border-stroke');

  if (field.type === 'boolean') {
    return (
      <select value={hasDv ? String(cur) : ''} onChange={e => {
        const v = e.target.value;
        onOverrideDefaultValue(field.key, v === '' ? undefined : v === 'true');
      }} className={inputCls}>
        <option value="">{parentDv !== undefined ? String(parentDv) : 'не задано'}</option>
        <option value="true">true</option>
        <option value="false">false</option>
      </select>
    );
  }
  if (field.type === 'enum') {
    const enumTypeDef = field.typeId ? enumTypes.find(et => et.id === field.typeId) : undefined;
    if (field.typeId && !enumTypeDef) return <span className="text-xs text-stroke-strong">—</span>;
    return (
      <select value={hasDv ? String(cur) : ''} onChange={e => {
        onOverrideDefaultValue(field.key, e.target.value || undefined);
      }} className={inputCls}>
        <option value="">{parentDv !== undefined ? String(parentDv) : 'не задано'}</option>
        {enumTypeDef
          ? enumTypeDef.values.map(v => <option key={v.code} value={v.code}>{v.label}</option>)
          : (field.options ?? []).filter(o => o).map(o => <option key={o} value={o}>{o}</option>)}
      </select>
    );
  }
  if (field.type === 'date') {
    return (
      <DateInput
        value={hasDv ? String(cur) : ''}
        onChange={v => onOverrideDefaultValue(field.key, v || undefined)}
        className={inputCls}
      />
    );
  }
  return (
    <input
      type={field.type === 'number' ? 'number' : 'text'}
      value={hasDv ? String(cur) : ''}
      placeholder={parentDv !== undefined ? String(parentDv) : 'от родит.'}
      onChange={e => {
        const v = e.target.value;
        onOverrideDefaultValue(field.key,
          v === '' ? undefined
          : field.type === 'number' ? Number(v) : v,
        );
      }}
      className={inputCls}
    />
  );
}

// ─── Inherited fields panel ────────────────────────────────────────────────────


