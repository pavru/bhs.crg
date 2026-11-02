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
import { RefPickerModal } from './RefPickerModal';
import { ArchivedRefMark } from './ArchivedRefs';
import { Select, SelectItem } from '@/shared/ui/Select';
import { SuggestField } from '@/shared/ui/SuggestField';
import { useSuggestSource } from './suggestContext';
import { IconButton } from '@/shared/ui/Button';
import { Checkbox } from '@/shared/ui/Checkbox';
import { cellLine, fieldBox } from '@/shared/ui/fieldBox';

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
      {/* Вид поля клетки листа ([UI-78]): фокус — чертой `primary` под клеткой, а не заливкой. */}
      <button type="button" onClick={() => setPickerOpen(true)}
        className={`flex-1 min-w-0 flex items-center gap-1 ${fieldBox('cell')} outline-none ${cellLine()}`}>
        {ref
          ? <><Icon icon={LinkIcon} size={18} className="text-primary shrink-0" /><span className="truncate text-primary">{ref.displayName}</span><ArchivedRefMark value={ref} words={false} /></>
          : <span className="text-on-surface-variant">—</span>
        }
      </button>
      {ref && (
        <IconButton label="Очистить"
          danger
          className="-my-1 mr-0.5"
          onClick={e => { e.stopPropagation(); onChange(undefined); }} icon={Close} />
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
  primitiveTypeDef, enumTypeDef, row }: {
  field: SchemaField; value: unknown; onChange: (v: unknown) => void;
  /** Номер строки с нуля: флажку клетки он нужен в имени — подписи над клеткой нет, а колонка у всех одна. */
  row?: number;
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
        <Checkbox aria-label={row == null ? field.title : `${field.title}, строка ${row + 1}`}
          checked={!!value} onChange={onChange} />
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
      <Select density="cell" aria-label={field.title} value={strVal} onValueChange={onChange} emptyItem="—">
        {opts.map(o => <SelectItem key={o.code} value={o.code}>{o.label}</SelectItem>)}
      </Select>
    );
  }
  if (field.type === 'date') {
    return <DateInput label={field.title} value={strVal} onChange={v => onChange(v)} density="cell" className="w-full" />;
  }
  // primitive-тип на базе date (issue #60) — иначе рендерился обычным текст-инпутом без DateInput/точности
  if (field.type === 'primitive' && primitiveTypeDef?.baseType === 'date') {
    return <DateInput label={field.title} value={strVal} onChange={v => onChange(v)} density="cell" className="w-full"
      precision={primitiveTypeDef.constraints.datePrecision ?? 'day'}
      min={primitiveTypeDef.constraints.minDate} max={primitiveTypeDef.constraints.maxDate} />;
  }
  // Поле клетки ([UI-78]): подпись — для диктора (у клетки своей нет), ↓ при закрытом списке — переход по строкам.
  return (
    <SuggestField source={source} density="cell" label={field.title}
      type={field.type === 'number' ? 'number' : 'text'}
      value={strVal}
      onChange={v => onChange(field.type === 'number' ? (v === '' ? '' : Number(v)) : v)}
      className="tabular-nums"
    />
  );
}
