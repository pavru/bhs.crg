import type { SchemaField, SchemaDefinition, FieldGroup, TypstRender } from '@/shared/api/schema';
export function schemaToJson(
  fields: SchemaField[],
  excludedFields: string[],
  fieldOverrides: Record<string, { required?: boolean; defaultValue?: unknown }>,
  groups: FieldGroup[] = [],
  typstRenders: TypstRender[] = [],
  typeTags: string[] = [],
  ungroupedOrder: string[] = [],
): string {
  const def: SchemaDefinition = { fields };
  if (groups.length) def.groups = groups;
  if (excludedFields.length) def.excludedFields = excludedFields;
  if (Object.keys(fieldOverrides).length) def.fieldOverrides = fieldOverrides;
  if (typstRenders.length) def.typstRenders = typstRenders;
  if (typeTags.length) def.tags = typeTags;
  if (ungroupedOrder.length) def.ungroupedOrder = ungroupedOrder;
  return JSON.stringify(def);
}

// ─── Автогенерация ключа/кода из названия (CamelCase, без транслитерации) ──────

/** "Номер документа" → "НомерДокумента": слова склеиваются, первая буква каждого — заглавная. */
export function toCamelKey(title: string): string {
  return title
    .split(/[^A-Za-zА-Яа-яЁё0-9]+/)
    .filter(Boolean)
    .map(w => w[0].toUpperCase() + w.slice(1))
    .join('');
}

// ─── Validation ───────────────────────────────────────────────────────────────

export function validateFields(fields: SchemaField[]): string | null {
  for (let i = 0; i < fields.length; i++) {
    const f = fields[i];
    if (!f.key.trim()) return `Поле #${i + 1}: ключ не может быть пустым`;
    if (!/^[A-Za-zА-Яа-яЁё0-9_]+$/.test(f.key.trim()))
      return `Поле #${i + 1}: ключ может содержать только буквы, цифры и _`;
    if (!f.title.trim()) return `Поле #${i + 1}: название не может быть пустым`;
    if (f.type === 'complex' && !f.typeId) return `Поле #${i + 1}: выберите составной тип`;
    if (f.type === 'array' && !f.typeId) return `Поле #${i + 1}: выберите составной тип для строк массива`;
    if ((f.type === 'doc-ref' || f.type === 'doc-array') && !f.typeId) return `Поле #${i + 1}: выберите тип документа`;
    if (f.type === 'primitive' && !f.typeId) return `Поле #${i + 1}: выберите тип поля`;
  }
  const keys = fields.map(f => f.key.trim());
  const dup = keys.find((k, idx) => keys.indexOf(k) !== idx);
  if (dup) return `Дублирующийся ключ: "${dup}"`;
  return null;
}

// ─── Constants ─────────────────────────────────────────────────────────────────

export const PRIMITIVE_TYPES: { value: SchemaField['type']; label: string }[] = [
  { value: 'string',    label: 'Строка'  },
  { value: 'text',      label: 'Текст'   },
  { value: 'number',    label: 'Число'   },
  { value: 'date',      label: 'Дата'    },
  { value: 'boolean',   label: 'Флаг'    },
  { value: 'enum',      label: 'Перечисление' },
  { value: 'primitive', label: 'Тип поля (свой)' },
  { value: 'complex',   label: 'Составной тип' },
  { value: 'array',     label: 'Массив (повт. строки)' },
  { value: 'doc-ref',  label: 'Документ (ссылка)' },
  { value: 'doc-array', label: 'Документы (список)' },
  { value: 'image',    label: 'Изображение' },
  { value: 'file',     label: 'Файл (вложение)' },
];

export const TYPE_LABELS: Record<string, string> = Object.fromEntries(
  PRIMITIVE_TYPES.map(t => [t.value, t.label]),
);

