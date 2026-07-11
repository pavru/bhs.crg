import { useId } from 'react';
import { Plus, Trash2, ArrowUp, ArrowDown, Cpu } from 'lucide-react';
import { DateInput } from '@/shared/ui/DateInput';
import type { DocumentType, PrimitiveTypeDef, EnumTypeDef } from '@/shared/api/types';
import type { SchemaField, FieldGroup } from '@/shared/api/schema';
import { PRIMITIVE_TYPES, toCamelKey } from './schemaConstants';
import { useTagRegistry, fieldTags } from '@/shared/api/tags';
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

// ─── Field builder (own fields) ────────────────────────────────────────────────

interface FieldBuilderProps {
  fields: SchemaField[];
  onChange: (fields: SchemaField[]) => void;
  disabledKeys?: Set<string>;
  compositeTypes: DocumentType[];
  primitiveTypes: PrimitiveTypeDef[];
  enumTypes: EnumTypeDef[];
  allDocTypes: DocumentType[];
}

export function FieldBuilder({ fields, onChange, disabledKeys, compositeTypes, primitiveTypes, enumTypes, allDocTypes }: FieldBuilderProps) {
  const uid = useId();
  const { data: tagRegistry } = useTagRegistry();

  const add = () => onChange([...fields, { key: '', title: '', type: 'string', required: false }]);
  const remove = (i: number) => onChange(fields.filter((_, idx) => idx !== i));
  const update = (i: number, patch: Partial<SchemaField>) =>
    onChange(fields.map((f, idx) => idx === i ? { ...f, ...patch } : f));
  const toggleTag = (i: number, code: string) => {
    const cur = fields[i].tags ?? [];
    const next = cur.includes(code) ? cur.filter(c => c !== code) : [...cur, code];
    update(i, { tags: next.length ? next : undefined });
  };
  // Для поля type="primitive" применимые тэги берём из самого типа поля (allowedTags),
  // для встроенных типов — из реестра по типу поля.
  const applicableTagsFor = (f: SchemaField) => {
    if (f.type === 'primitive') {
      const pt = primitiveTypes.find(p => p.id === f.typeId);
      const codes = new Set(pt?.allowedTags ?? []);
      return (tagRegistry ?? []).filter(t => t.scope === 'Field' && codes.has(t.code));
    }
    return fieldTags(tagRegistry, f.type);
  };
  // Легаси enum-поле (issue #59): options заданы инлайн, typeId не выбран — создано до появления
  // реестра EnumType. Продолжает редактироваться старым инлайн-списком, без принудительного переноса.
  const isLegacyEnum = (f: SchemaField) => f.type === 'enum' && !f.typeId && (f.options?.length ?? 0) > 0;
  const enumTypeDefFor = (f: SchemaField) => f.type === 'enum' ? enumTypes.find(et => et.id === f.typeId) : undefined;
  const setImageOpt = (i: number, patch: Partial<NonNullable<SchemaField['image']>>) => {
    const merged = { ...(fields[i].image ?? {}), ...patch };
    const cleaned = Object.fromEntries(Object.entries(merged).filter(([, v]) => v !== undefined && v !== ''));
    update(i, { image: Object.keys(cleaned).length ? cleaned : undefined });
  };
  // Ключ считается «автоматическим», если совпадает с тем, что сгенерировал бы toCamelKey
  // из текущего названия — тогда при вводе названия перегенерируем его. Как только пользователь
  // руками поправит ключ (он разойдётся с авто-значением), автогенерация для этого поля отключается.
  const updateTitle = (i: number, title: string) => {
    const field = fields[i];
    const isKeyAuto = !field.key.trim() || field.key === toCamelKey(field.title);
    update(i, { title, ...(isKeyAuto ? { key: toCamelKey(title) } : {}) });
  };
  const moveUp = (i: number) => {
    if (i === 0) return;
    const next = [...fields]; [next[i - 1], next[i]] = [next[i], next[i - 1]]; onChange(next);
  };
  const moveDown = (i: number) => {
    if (i === fields.length - 1) return;
    const next = [...fields]; [next[i], next[i + 1]] = [next[i + 1], next[i]]; onChange(next);
  };

  return (
    <div className="space-y-2">
      {fields.length > 0 && (
        <div className="grid grid-cols-[1fr_1fr_160px_72px_48px] gap-2 px-2 pb-1">
          <span className="text-xs font-medium text-fg3">Название</span>
          <span className="text-xs font-medium text-fg3">Ключ</span>
          <span className="text-xs font-medium text-fg3">Тип</span>
          <span className="text-xs font-medium text-fg3">Обяз.</span>
          <span />
        </div>
      )}
      {fields.map((field, i) => {
        const keyConflict = !!field.key && disabledKeys?.has(field.key.trim());
        return (
          <div key={`${uid}-${i}`} className="space-y-1">
            <div className="grid grid-cols-[1fr_1fr_160px_72px_48px] gap-2 items-center">
              {/* Title */}
              <input
                value={field.title}
                onChange={e => updateTitle(i, e.target.value)}
                placeholder="Название"
                className="border border-stroke-strong rounded-md px-3 py-1.5 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface"
              />
              {/* Key */}
              <div className="relative">
                <input
                  value={field.key}
                  onChange={e => update(i, { key: e.target.value })}
                  placeholder="КлючПоля"
                  spellCheck={false}
                  className={`w-full border rounded-md px-3 py-1.5 text-sm font-mono focus:outline-none focus-visible:ring-2 ${
                    keyConflict
                      ? 'border-danger focus-visible:ring-danger'
                      : 'border-stroke-strong focus-visible:ring-brand'
                  } bg-surface`}
                />
                {keyConflict && (
                  <span className="absolute right-2 top-1/2 -translate-y-1/2 text-xs text-danger">!</span>
                )}
              </div>
              {/* Type */}
              <select
                value={field.type}
                onChange={e => {
                  const t = e.target.value as SchemaField['type'];
                  update(i, {
                    type: t,
                    typeId: (t === 'complex' || t === 'primitive' || t === 'array' || t === 'doc-ref' || t === 'doc-array' || t === 'enum') ? '' : undefined,
                    // Легаси options НЕ предзаполняем для нового enum-поля (issue #59) — новые
                    // поля идут через typeId; сохраняем, только если уже были (переключение туда-обратно).
                    options: t === 'enum' ? field.options : undefined,
                    defaultValue: undefined,
                  });
                }}
                className="border border-stroke-strong rounded-md px-2 py-1.5 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface"
              >
                {PRIMITIVE_TYPES.map(t => (
                  <option key={t.value} value={t.value}>{t.label}</option>
                ))}
              </select>
              {/* Required */}
              <label className="flex items-center justify-center gap-1.5 cursor-pointer">
                <input
                  type="checkbox"
                  checked={field.required}
                  onChange={e => update(i, { required: e.target.checked })}
                  className="w-4 h-4 rounded border-stroke-strong text-brand"
                />
                <span className="text-xs text-fg3">да</span>
              </label>
              {/* Actions */}
              <div className="flex items-center gap-0.5">
                <button type="button" onClick={() => moveUp(i)} disabled={i === 0}
                  className="p-1 text-fg4 hover:text-fg2 disabled:opacity-25">
                  <ArrowUp size={12} />
                </button>
                <button type="button" onClick={() => moveDown(i)} disabled={i === fields.length - 1}
                  className="p-1 text-fg4 hover:text-fg2 disabled:opacity-25">
                  <ArrowDown size={12} />
                </button>
                <button type="button" onClick={() => remove(i)}
                  className="p-1 text-fg4 hover:text-danger">
                  <Trash2 size={12} />
                </button>
              </div>
            </div>
            {/* Composite type selector (complex & array) */}
            {(field.type === 'complex' || field.type === 'array') && (
              <div className="ml-[calc(33%+0.5rem)] mr-[calc(5rem)]">
                <select
                  value={field.typeId ?? ''}
                  onChange={e => update(i, { typeId: e.target.value })}
                  className={`w-full border rounded-md px-2 py-1.5 text-xs focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface ${
                    !field.typeId ? 'border-yellow-400 text-fg4' : 'border-stroke-strong'
                  }`}
                >
                  <option value="">
                    {field.type === 'array' ? 'Тип строки (составной)...' : 'Выберите составной тип...'}
                  </option>
                  {compositeTypes.map(ct => (
                    <option key={ct.id} value={ct.id}>{ct.name} ({ct.code})</option>
                  ))}
                </select>
              </div>
            )}
            {/* Document type selector (doc-ref & doc-array) */}
            {(field.type === 'doc-ref' || field.type === 'doc-array') && (
              <div className="ml-[calc(33%+0.5rem)] mr-[calc(5rem)]">
                <select
                  value={field.typeId ?? ''}
                  onChange={e => update(i, { typeId: e.target.value })}
                  className={`w-full border rounded-md px-2 py-1.5 text-xs focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface ${
                    !field.typeId ? 'border-yellow-400 text-fg4' : 'border-stroke-strong'
                  }`}
                >
                  <option value="">Выберите тип документа...</option>
                  {allDocTypes.filter(dt => dt.kind === 'Document').map(dt => (
                    <option key={dt.id} value={dt.id}>{dt.name} ({dt.code})</option>
                  ))}
                </select>
              </div>
            )}
            {/* Primitive type selector */}
            {field.type === 'primitive' && (
              <div className="ml-[calc(33%+0.5rem)] mr-[calc(5rem)]">
                <select
                  value={field.typeId ?? ''}
                  onChange={e => update(i, { typeId: e.target.value })}
                  className={`w-full border rounded-md px-2 py-1.5 text-xs focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface ${
                    !field.typeId ? 'border-yellow-400 text-fg4' : 'border-stroke-strong'
                  }`}
                >
                  <option value="">Выберите тип поля...</option>
                  {primitiveTypes.map(pt => (
                    <option key={pt.id} value={pt.id}>{pt.name} ({pt.code})</option>
                  ))}
                </select>
              </div>
            )}
            {/* Enum type selector (issue #59) — только для НЕ-легаси enum-полей: у легаси (options
                инлайн, typeId не выбран) остаётся старый инлайн-редактор ниже. */}
            {field.type === 'enum' && !isLegacyEnum(field) && (
              <div className="ml-[calc(33%+0.5rem)] mr-[calc(5rem)]">
                <select
                  value={field.typeId ?? ''}
                  onChange={e => update(i, { typeId: e.target.value })}
                  className={`w-full border rounded-md px-2 py-1.5 text-xs focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface ${
                    !field.typeId ? 'border-yellow-400 text-fg4' : 'border-stroke-strong'
                  }`}
                >
                  <option value="">Выберите тип перечисления...</option>
                  {enumTypes.map(et => (
                    <option key={et.id} value={et.id}>{et.name} ({et.code})</option>
                  ))}
                </select>
              </div>
            )}
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
                      onChange={e => update(i, { defaultValue: e.target.checked ? true : undefined })}
                      className="w-3.5 h-3.5 rounded border-stroke-strong text-brand"
                    />
                    <span className="text-xs text-fg3">{field.defaultValue ? 'true' : 'не задано'}</span>
                  </label>
                ) : field.type === 'enum' ? (
                  <select
                    value={String(field.defaultValue ?? '')}
                    onChange={e => update(i, { defaultValue: e.target.value || undefined })}
                    className="flex-1 border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand"
                  >
                    <option value="">— не задано —</option>
                    {enumTypeDefFor(field)
                      ? enumTypeDefFor(field)!.values.map(v => <option key={v.code} value={v.code}>{v.label}</option>)
                      : (field.options ?? []).filter(o => o).map(opt => (
                          <option key={opt} value={opt}>{opt}</option>
                        ))}
                  </select>
                ) : field.type === 'date' ? (
                  <DateInput
                    value={field.defaultValue != null ? String(field.defaultValue) : ''}
                    onChange={v => update(i, { defaultValue: v || undefined })}
                    className="flex-1 border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand"
                  />
                ) : (
                  <input
                    type={field.type === 'number' ? 'number' : 'text'}
                    placeholder="не задано"
                    value={field.defaultValue != null ? String(field.defaultValue) : ''}
                    onChange={e => {
                      const v = e.target.value;
                      update(i, {
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
            {isLegacyEnum(field) && (
              <div className="ml-[calc(33%+0.5rem)] mr-[calc(5rem)] space-y-1.5">
                {(field.options ?? []).map((opt, oi) => (
                  <div key={oi} className="flex items-center gap-1.5">
                    <input
                      value={opt}
                      onChange={e => {
                        const opts = [...(field.options ?? [])];
                        opts[oi] = e.target.value;
                        update(i, { options: opts });
                      }}
                      placeholder={`Вариант ${oi + 1}`}
                      className="flex-1 border border-stroke-strong rounded px-2 py-1 text-xs focus:outline-none focus-visible:ring-1 focus-visible:ring-brand bg-surface"
                    />
                    <button type="button"
                      onClick={() => {
                        const opts = (field.options ?? []).filter((_, j) => j !== oi);
                        update(i, { options: opts });
                      }}
                      className="p-0.5 text-fg4 hover:text-danger">
                      <Trash2 size={11} />
                    </button>
                  </div>
                ))}
                <button type="button"
                  onClick={() => update(i, { options: [...(field.options ?? []), ''] })}
                  className="flex items-center gap-1 text-xs text-brand hover:text-brand-hover">
                  <Plus size={11} /> Добавить вариант
                </button>
              </div>
            )}
            {/* Опции изображения (размер/выравнивание) */}
            {field.type === 'image' && (
              <div className="ml-[calc(33%+0.5rem)] mr-[calc(5rem)] flex flex-wrap items-center gap-2">
                <span className="text-xs text-fg4 shrink-0 w-28">Изображение:</span>
                <input
                  value={field.image?.width ?? ''}
                  onChange={e => setImageOpt(i, { width: e.target.value })}
                  placeholder="ширина (напр. 4cm)"
                  className="w-32 border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand"
                />
                <input
                  value={field.image?.height ?? ''}
                  onChange={e => setImageOpt(i, { height: e.target.value })}
                  placeholder="высота"
                  className="w-24 border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand"
                />
                <select
                  value={field.image?.align ?? ''}
                  onChange={e => setImageOpt(i, { align: (e.target.value || undefined) as 'left' | 'center' | 'right' | undefined })}
                  className="border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand"
                >
                  <option value="">выравнивание</option>
                  <option value="left">слева</option>
                  <option value="center">по центру</option>
                  <option value="right">справа</option>
                </select>
                <select
                  value={field.image?.fit ?? ''}
                  onChange={e => setImageOpt(i, { fit: (e.target.value || undefined) as 'cover' | 'contain' | 'stretch' | undefined })}
                  className="border border-stroke rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand"
                >
                  <option value="">fit (вписывание)</option>
                  <option value="contain">contain</option>
                  <option value="cover">cover</option>
                  <option value="stretch">stretch</option>
                </select>
              </div>
            )}

            {/* Функциональные тэги поля (для primitive — из типа поля, иначе из реестра) */}
            {(() => {
              const applicable = applicableTagsFor(field);
              if (applicable.length === 0) return null;
              return (
                <div className="ml-[calc(33%+0.5rem)] mr-[calc(5rem)] flex items-start gap-2">
                  <Cpu size={12} className={`mt-1 ${field.tags?.length ? 'text-purple-500' : 'text-stroke-strong'}`} />
                  <span className="text-xs text-fg4 shrink-0 w-28 mt-1">Функц. тэги:</span>
                  <div className="flex flex-wrap gap-1.5">
                    {applicable.map(t => {
                      const on = field.tags?.includes(t.code) ?? false;
                      return (
                        <button
                          key={t.code}
                          type="button"
                          title={t.description}
                          onClick={() => toggleTag(i, t.code)}
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
              );
            })()}
          </div>
        );
      })}
      <button
        type="button"
        onClick={add}
        className="flex items-center gap-1.5 text-sm text-brand hover:text-brand-hover mt-1"
      >
        <Plus size={14} /> Добавить поле
      </button>
    </div>
  );
}

// ─── Default value cell (module-level to avoid remount on each render) ────────

export function DefaultValueCell({ field, override, onOverrideDefaultValue }: {
  field: SchemaField;
  override?: { required?: boolean; defaultValue?: unknown };
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
    // typeId-резолв (issue #59) сюда не проброшен (как и primitiveTypeDef для type="primitive"
    // выше по isPrimitive) — для таких полей переопределение default value здесь недоступно.
    if (field.typeId) return <span className="text-xs text-stroke-strong">—</span>;
    const opts = (field.options ?? []).filter(o => o);
    return (
      <select value={hasDv ? String(cur) : ''} onChange={e => {
        onOverrideDefaultValue(field.key, e.target.value || undefined);
      }} className={inputCls}>
        <option value="">{parentDv !== undefined ? String(parentDv) : 'не задано'}</option>
        {opts.map(o => <option key={o} value={o}>{o}</option>)}
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


