import { useId } from 'react';
import { Plus, Trash2, ArrowUp, ArrowDown, Cpu } from 'lucide-react';
import { DateInput } from '@/shared/ui/DateInput';
import type { DocumentType, PrimitiveTypeDef } from '@/shared/api/types';
import type { SchemaField, FieldGroup } from '@/shared/api/schema';
import { PRIMITIVE_TYPES, META_TAGS } from './schemaConstants';
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
  allDocTypes: DocumentType[];
}

export function FieldBuilder({ fields, onChange, disabledKeys, compositeTypes, primitiveTypes, allDocTypes }: FieldBuilderProps) {
  const uid = useId();

  const add = () => onChange([...fields, { key: '', title: '', type: 'string', required: false }]);
  const remove = (i: number) => onChange(fields.filter((_, idx) => idx !== i));
  const update = (i: number, patch: Partial<SchemaField>) =>
    onChange(fields.map((f, idx) => idx === i ? { ...f, ...patch } : f));
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
          <span className="text-xs font-medium text-fg3">Ключ</span>
          <span className="text-xs font-medium text-fg3">Название</span>
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
              {/* Title */}
              <input
                value={field.title}
                onChange={e => update(i, { title: e.target.value })}
                placeholder="Название"
                className="border border-stroke-strong rounded-md px-3 py-1.5 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface"
              />
              {/* Type */}
              <select
                value={field.type}
                onChange={e => {
                  const t = e.target.value as SchemaField['type'];
                  update(i, {
                    type: t,
                    typeId: (t === 'complex' || t === 'primitive' || t === 'array' || t === 'doc-ref' || t === 'doc-array') ? '' : undefined,
                    options: t === 'enum' ? (field.options ?? []) : undefined,
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
                    {(field.options ?? []).filter(o => o).map(opt => (
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
            {/* Enum options editor */}
            {field.type === 'enum' && (
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
            {/* metaTag selector — только для скалярных полей */}
            {field.type !== 'complex' && field.type !== 'array' && field.type !== 'doc-ref' && field.type !== 'doc-array' && (
              <div className="ml-[calc(33%+0.5rem)] mr-[calc(5rem)] flex items-center gap-2">
                <Cpu size={12} className={field.metaTag ? 'text-purple-500' : 'text-stroke-strong'} />
                <span className="text-xs text-fg4 shrink-0 w-28">Метаданные авто:</span>
                <select
                  value={field.metaTag ?? ''}
                  onChange={e => update(i, { metaTag: e.target.value || undefined })}
                  className={`flex-1 border rounded px-2 py-1 text-xs bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-purple-400 ${
                    field.metaTag ? 'border-purple-300 text-purple-700' : 'border-stroke text-fg4'
                  }`}
                >
                  <option value="">— не задано —</option>
                  {META_TAGS
                    .filter(t => field.type === 'file' ? t.fileOnly : !t.fileOnly)
                    .map(t => (
                      <option key={t.value} value={t.value}>{t.label}</option>
                    ))}
                </select>
              </div>
            )}
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


