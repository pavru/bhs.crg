/**
 * Ячейки таблицы массива: выбор составного значения и диспетчеризация по типу подполя.
 *
 * <p>Выделено из `ComplexFields.tsx` (issue #1014), где лежало среди 1573 строк и вычитывалось
 * целиком при любой правке. Это самый нижний слой: о редакторах, которые его зовут, он не знает,
 * поэтому вынимается, не замыкая цикл.</p>
 */
import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Close, LinkIcon } from '@/shared/ui/icons';
import { DateInput } from '@/shared/ui/DateInput';
import type { CatalogScope, DocumentType, EnumTypeDef, PrimitiveTypeDef } from '@/shared/api/types';
import { isFieldRef } from '@/shared/api/types';
import { type SchemaField } from '@/shared/api/schema';
import { CELL_INPUT } from './constants';
import { RefPickerModal } from './RefPickerModal';
import { ArchivedRefMark } from './ArchivedRefs';
import { Select, SelectItem } from '@/shared/ui/Select';
import { SuggestField } from '@/shared/ui/SuggestField';
import { useSuggestSource } from './suggestContext';

// ─── Complex cell picker (inline table cell) ──────────────────────────────────

export function ComplexCellPicker({ value, onChange, compositeType, setId, allDocTypes, scope, scopeId }: {
  // `field` здесь НЕ объявляется намеренно: кандидатов выбирает `compositeType`, а `RefPickerModal`
  // пропа `field` не принимает вовсе. Объявленный, но не берущийся проп — обещание, что значение
  // на что-то влияет, и вызывающий передавал его впустую (поймано ревью PR #1020). Теперь лишний
  // проп отвергают типы: проверено возвратом `field=` — TS2322.
  value: unknown; onChange: (v: unknown) => void;
  compositeType: DocumentType | null;
  setId?: string; allDocTypes: DocumentType[];
  scope?: CatalogScope; scopeId?: string | null;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const ref = isFieldRef(value) ? value : null;
  return (
    <div className="flex items-center w-full h-full">
      <button type="button" onClick={() => setPickerOpen(true)}
        className="flex-1 min-w-0 h-full flex items-center gap-1 px-1.5 focus:outline-none focus:bg-primary-container">
        {ref
          ? <><Icon icon={LinkIcon} size={18} className="text-primary shrink-0" /><span className="text-xs truncate text-primary">{ref.displayName}</span><ArchivedRefMark value={ref} words={false} /></>
          : <span className="text-xs text-on-surface-variant">—</span>
        }
      </button>
      {ref && (
        <button type="button" onClick={e => { e.stopPropagation(); onChange(undefined); }}
          className="shrink-0 p-0.5 mr-0.5 text-on-surface-variant hover:text-error transition-colors">
          <Icon icon={Close} size={18} />
        </button>
      )}
      <RefPickerModal open={pickerOpen} onOpenChange={setPickerOpen}
        compositeType={compositeType}
        setId={setId} scope={scope} scopeId={scopeId}
        allDocTypes={allDocTypes} current={value}
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
  // Подсказка — по виду поля схемы, а не контрола (issue #1533): `text` здесь однострочный, но абзацы
  // не подсказываются и в клетке. Тип-владелец — тип строки, его задаёт таблица.
  const source = useSuggestSource(field);
  if (field.type === 'complex') {
    return (
      <ComplexCellPicker value={value} onChange={onChange}
        compositeType={compositeType} setId={setId} allDocTypes={allDocTypes}
        scope={scope} scopeId={scopeId} />
    );
  }
  if (field.type === 'boolean') {
    return (
      <div className="flex items-center justify-center w-full h-full">
        <input type="checkbox" checked={!!value}
          onChange={e => onChange(e.target.checked)}
          className="w-3.5 h-3.5 rounded border-outline text-primary" />
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
      <Select size="inline" className="w-full h-full px-1.5 text-xs rounded-none focus:bg-primary-container"
        aria-label={field.title} value={strVal} onValueChange={onChange} emptyItem="—">
        {opts.map(o => <SelectItem key={o.code} value={o.code}>{o.label}</SelectItem>)}
      </Select>
    );
  }
  if (field.type === 'date') {
    return <DateInput label={field.title} value={strVal} onChange={v => onChange(v)} compact
      className="w-full h-full flex items-center px-1.5 focus-within:bg-primary-container" />;
  }
  // primitive-тип на базе date (issue #60) — иначе рендерился обычным текст-инпутом без DateInput/точности
  if (field.type === 'primitive' && primitiveTypeDef?.baseType === 'date') {
    return <DateInput label={field.title} value={strVal} onChange={v => onChange(v)} compact
      precision={primitiveTypeDef.constraints.datePrecision ?? 'day'}
      min={primitiveTypeDef.constraints.minDate} max={primitiveTypeDef.constraints.maxDate}
      className="w-full h-full flex items-center px-1.5 focus-within:bg-primary-container" />;
  }
  // Без источника `SuggestField` — голый `<input>` клетки: подпись ему — `aria-label` (у клетки своей нет).
  return (
    <SuggestField source={source} compact label={field.title}
      type={field.type === 'number' ? 'number' : 'text'}
      value={strVal}
      onChange={v => onChange(field.type === 'number' ? (v === '' ? '' : Number(v)) : v)}
      className={CELL_INPUT}
    />
  );
}
