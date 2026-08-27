import type { DocumentType, PrimitiveTypeDef, EnumTypeDef } from '@/shared/api/types';
import type { SchemaField } from '@/shared/api/schema';
import type { PickType } from '@/shared/ui/TypePicker';
import type { TagDefinition } from '@/shared/api/tags';
import { TYPE_LABELS } from './schemaConstants';

// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

/** Реестры типов/тэгов, общие для карточки поля — прокидываются и в плоский FieldBuilder,
 *  и в группированный GroupedFieldsEditor (issue #197 Фаза C). */
export interface FieldRegistries {
  compositeTypes: DocumentType[];
  primitiveTypes: PrimitiveTypeDef[];
  enumTypes: EnumTypeDef[];
  allDocTypes: DocumentType[];
  tagRegistry: TagDefinition[] | undefined;
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
