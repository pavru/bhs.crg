import type { SchemaField, SchemaDefinition, FieldGroup, TypstRender } from '@/shared/api/schema';
export function schemaToJson(
  fields: SchemaField[],
  excludedFields: string[],
  fieldOverrides: Record<string, { required?: boolean; defaultValue?: unknown }>,
  groups: FieldGroup[] = [],
  typstRenders: TypstRender[] = [],
  typeTags: string[] = [],
  ungroupedOrder: string[] = [],
  help = '',
): string {
  const def: SchemaDefinition = { fields };
  if (groups.length) def.groups = groups;
  if (excludedFields.length) def.excludedFields = excludedFields;
  if (Object.keys(fieldOverrides).length) def.fieldOverrides = fieldOverrides;
  if (typstRenders.length) def.typstRenders = typstRenders;
  if (typeTags.length) def.tags = typeTags;
  if (ungroupedOrder.length) def.ungroupedOrder = ungroupedOrder;
  if (help.trim()) def.help = help.trim();
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

/**
 * Ключ/код при переименовании сущности (issue #355). Авто-генерация — свойство НОВОЙ (ещё не
 * сохранённой) сущности: ключ следует за именем только пока `isNew` И ключ не тронут вручную (всё ещё
 * совпадает с камелизацией ТЕКУЩЕГО имени `currentTitle`). Тогда возвращаем ключ из НОВОГО имени
 * `newTitle`. Иначе — у сохранённой сущности или после ручной правки ключа — ключ ЗАМОРОЖЕН (как есть),
 * чтобы переименование не осиротило данные.
 */
export function nextAutoKey(currentKey: string, currentTitle: string, newTitle: string, isNew: boolean): string {
  const isAuto = isNew && (!currentKey.trim() || currentKey === toCamelKey(currentTitle));
  return isAuto ? toCamelKey(newTitle) : currentKey;
}

/**
 * Новая база сравнения ключа поля — ключ, под которым поле лежит в СОХРАНЁННОЙ схеме (issue #527),
 * или null, пока поле не сохранено. Карточка поля держит её собственным состоянием и двигает только
 * этой функцией; печать в поле ключа базу не трогает.
 *
 * Почему база не выводится из текущего ключа (`persistedKeys.has(field.key)`, как было):
 * у сохранённого поля первое же нажатие клавиши уводит ключ из набора, и поле начинает выглядеть
 * новым — предупреждение о дрейфе данных гаснет ровно тогда, когда оно нужно, а перенос данных на
 * новый ключ перестаёт предлагаться.
 *
 * Почему база и не фиксируется навсегда при монтировании (второй половиной прежней схемы): новое
 * поле после сохранения становится сохранённым, а база остаётся пустой строкой с момента добавления —
 * это и есть сам issue #527, «ключ изменён с «»» на поле, которое никто не переименовывал.
 */
export function nextSavedKey(
  savedKey: string | null,
  currentKey: string,
  persistedKeys: Set<string> | undefined,
  prevPersistedKeys?: Set<string>,
): string | null {
  const key = currentKey.trim();
  if (!persistedKeys?.has(key)) return savedKey;   // такого ключа в сохранённой схеме нет
  // Ключ был сохранён и ДО этой перезагрузки схемы — значит записали не нас, а совпадение это чужое
  // поле: пользователь набрал ключ, который уже занят (валидация схемы такое сохранить не даст).
  // Присвоив его, карточка заморозила бы ввод и — хуже — записала бы перенос данных ОТ чужого поля:
  // подтверждение диалога перенесло бы его реальные данные под наш ключ.
  // prevPersistedKeys не передан — это первичная оценка при монтировании, там совпадение с сохранённым
  // ключом означает ровно «поле уже сохранено».
  if (prevPersistedKeys?.has(key)) return savedKey;
  return key;
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

