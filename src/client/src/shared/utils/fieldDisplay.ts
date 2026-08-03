import type { SchemaField } from '@/shared/api/schema';
import type { EnumTypeDef, PrimitiveTypeDef } from '@/shared/api/types';
import { formatDateRu } from './date';

/**
 * Значение поля в человеческом виде — для мест, где значение ПОКАЗЫВАЮТ, а не редактируют
 * (issue #611): свёрнутые сводки составных полей и строк таблиц.
 *
 * Повод: в свёрнутой строке составного поля «Период действия» стояло «2023-08-10 · 2028-08-09»,
 * тогда как сами поля внутри показывали «10.08.2023» и «09.08.2028». Формат знали только
 * редакторы (`DateInput`, `PrimitiveInput`), а сводка звала `String(v)` и отдавала сырое хранимое
 * значение. Здесь собрано то же знание в чистом виде — без контролов.
 *
 * Хранение не меняется: дата всегда полный ISO, точность — метаданные типа (issue #60), логическое
 * значение — булево, перечисление — код. Формат живёт только на экране; в PDF о нём заботится
 * Typst-шаблон.
 */
export interface FieldTypeDefs {
  primitiveTypes?: PrimitiveTypeDef[];
  enumTypes?: EnumTypeDef[];
}

const TRUE_TOKENS = new Set(['true', 'да', 'истина', 'yes', 'y', '1', '+']);
const FALSE_TOKENS = new Set(['false', 'нет', 'ложь', 'no', 'n', '0', '-']);

/** Пустое значение отдаём пустой строкой: решение «показывать ли пропуск» принимает вызывающий. */
export function formatFieldValue(
  field: SchemaField, value: unknown, defs: FieldTypeDefs = {},
): string {
  if (value == null || value === '') return '';

  if (field.type === 'date') return formatDateRu(String(value));

  if (field.type === 'primitive') {
    const def = defs.primitiveTypes?.find(pt => pt.id === field.typeId);
    if (def?.baseType === 'date')
      return formatDateRu(String(value), def.constraints.datePrecision ?? 'day');
    return String(value);
  }

  if (field.type === 'boolean') {
    if (typeof value === 'boolean') return value ? 'Да' : 'Нет';
    // В поле-флажке лежит не только булево: распознавание кладёт туда «да»/«нет» словом
    // (`ValueTypeRules.CheckBase`), вставка из таблицы разбирает свой набор (`PasteMappingModal`).
    // Чего нет ни в одном наборе — показываем как есть: «Нет» на непонятном значении было бы
    // утверждением, причём в том самом месте, где значение смотрят не открывая редактор.
    const token = String(value).trim().toLowerCase();
    if (TRUE_TOKENS.has(token)) return 'Да';
    if (FALSE_TOKENS.has(token)) return 'Нет';
    return String(value);
  }

  if (field.type === 'enum') {
    const code = String(value);
    // Отображаемое имя знает реестр перечислений (issue #59); легаси-варианты в схеме — код и есть имя.
    const def = defs.enumTypes?.find(et => et.id === field.typeId);
    return def?.values.find(v => v.code === code)?.label ?? code;
  }

  return String(value);
}
