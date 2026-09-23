/**
 * Ячейки таблицы массива: выбор составного значения и диспетчеризация по типу подполя.
 *
 * <p>Выделено из `ComplexFields.tsx` (issue #1014), где лежало среди 1573 строк и вычитывалось
 * целиком при любой правке. Это самый нижний слой: о редакторах, которые его зовут, он не знает,
 * поэтому вынимается, не замыкая цикл.</p>
 */
import { useState } from 'react';
import { Link2, X } from 'lucide-react';
import { DateInput } from '@/shared/ui/DateInput';
import type { CatalogScope, DocumentType, EnumTypeDef, PrimitiveTypeDef } from '@/shared/api/types';
import { isFieldRef } from '@/shared/api/types';
import { type SchemaField } from '@/shared/api/schema';
import { CELL_INPUT } from './constants';
import { RefPickerModal } from './RefPickerModal';

// ─── Complex cell picker (inline table cell) ──────────────────────────────────

export function ComplexCellPicker({ value, onChange, compositeType, setId, allDocTypes, scope, scopeId }: {
  field: SchemaField; value: unknown; onChange: (v: unknown) => void;
  compositeType: DocumentType | null;
  setId?: string; allDocTypes: DocumentType[];
  scope?: CatalogScope; scopeId?: string | null;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const ref = isFieldRef(value) ? value : null;
  return (
    <div className="flex items-center w-full h-full">
      <button type="button" onClick={() => setPickerOpen(true)}
        className="flex-1 min-w-0 h-full flex items-center gap-1 px-1.5 focus:outline-none focus:bg-brand-subtle">
        {ref
          ? <><Link2 size={10} className="text-brand shrink-0" /><span className="text-xs truncate text-brand-hover">{ref.displayName}</span></>
          : <span className="text-xs text-fg4">—</span>
        }
      </button>
      {ref && (
        <button type="button" onClick={e => { e.stopPropagation(); onChange(undefined); }}
          className="shrink-0 p-0.5 mr-0.5 text-stroke-strong hover:text-danger transition-colors">
          <X size={9} />
        </button>
      )}
      <RefPickerModal open={pickerOpen} onOpenChange={setPickerOpen}
        compositeType={compositeType}
        setId={setId} scope={scope} scopeId={scopeId}
        allDocTypes={allDocTypes}
        onSelect={r => onChange(r)} />
    </div>
  );
}

// ─── Table cell ───────────────────────────────────────────────────────────────

export function TableCell({ field, value, onChange, compositeType, setId, allDocTypes, scope, scopeId,
  primitiveTypeDef, enumTypeDef }: {
  field: SchemaField; value: unknown; onChange: (v: unknown) => void;
  compositeType: DocumentType | null;
  setId?: string; allDocTypes: DocumentType[];
  scope?: CatalogScope; scopeId?: string | null;
  primitiveTypeDef?: PrimitiveTypeDef;
  /** Перечисление из реестра (issue #59): без него ячейка читает только легаси-`options` и пустеет. */
  enumTypeDef?: EnumTypeDef;
}) {
  const strVal = value == null ? '' : String(value);
  if (field.type === 'complex') {
    return (
      <ComplexCellPicker field={field} value={value} onChange={onChange}
        compositeType={compositeType} setId={setId} allDocTypes={allDocTypes}
        scope={scope} scopeId={scopeId} />
    );
  }
  if (field.type === 'boolean') {
    return (
      <div className="flex items-center justify-center w-full h-full">
        <input type="checkbox" checked={!!value}
          onChange={e => onChange(e.target.checked)}
          className="w-3.5 h-3.5 rounded border-stroke-strong text-brand" />
      </div>
    );
  }
  if (field.type === 'enum') {
    // Варианты знает реестр (issue #59); в схеме их нет вовсе — там только typeId. Легаси-поля,
    // наоборот, хранят коды прямо в options, и код там же и есть отображаемое имя.
    const opts = enumTypeDef
      ? enumTypeDef.values.map(v => ({ code: v.code, label: v.label }))
      : (field.options ?? []).filter(o => o !== '').map(o => ({ code: o, label: o }));
    return (
      <select value={strVal} onChange={e => onChange(e.target.value)}
        className={CELL_INPUT + ' cursor-pointer'}>
        <option value="">—</option>
        {opts.map(o => <option key={o.code} value={o.code}>{o.label}</option>)}
      </select>
    );
  }
  if (field.type === 'date') {
    return <DateInput value={strVal} onChange={v => onChange(v)} compact
      className="w-full h-full flex items-center px-1.5 focus-within:bg-brand-subtle" />;
  }
  // primitive-тип на базе date (issue #60) — иначе рендерился обычным текст-инпутом без DateInput/точности
  if (field.type === 'primitive' && primitiveTypeDef?.baseType === 'date') {
    return <DateInput value={strVal} onChange={v => onChange(v)} compact
      precision={primitiveTypeDef.constraints.datePrecision ?? 'day'}
      className="w-full h-full flex items-center px-1.5 focus-within:bg-brand-subtle" />;
  }
  return (
    <input type={field.type === 'number' ? 'number' : 'text'}
      value={strVal}
      onChange={e => {
        const v = e.target.value;
        onChange(field.type === 'number' ? (v === '' ? '' : Number(v)) : v);
      }}
      className={CELL_INPUT}
    />
  );
}
