/**
 * Чистые помощники редакторов составного значения: пустота строки и разбор union-значения.
 *
 * <p>Выделено из `ComplexFields.tsx` (issue #1014). Отдельным файлом не только ради объёма:
 * `react-refresh/only-export-components` требует разводить компоненты и обычные функции, и та же
 * конвенция уже действует в этом каталоге — `fieldValidation.ts`, `lockedFields.ts`.</p>
 */
import { isFieldRef } from '@/shared/api/types';
import { type SchemaField } from '@/shared/api/schema';
import { formatFieldValue, type FieldTypeDefs } from '@/shared/utils/fieldDisplay';

/** Строка без единого заполненного подполя — выносить нечего. */
export function isRowEmpty(row: Record<string, unknown> | undefined, subFields: SchemaField[]): boolean {
  if (!row) return true;
  return subFields.every(f => { const v = row[f.key]; return v == null || v === ''; });
}

/**
 * Подписи заполненных вариантов union-значения (issue #756).
 *
 * <p>Инвариант — «заполнен ровно один» (#320), и записать иное приложение не даёт. Но
 * <code>PUT …/requisites</code> кладёт тело как есть (путь записи схема-агностичен сознательно), так
 * что значение с двумя ключами приезжает из восстановленной копии, правки JSONB руками или импорта.
 * Единственный путь через такие данные в редакторе — потеря части: он показывает ПЕРВЫЙ заполненный
 * вариант, а первая же правка выбрасывает остальные. Поэтому — сказать заранее.</p>
 *
 * <p><code>subFields</code> приходит уже без расчётных подполей (#368) — их значение считает
 * генерация, вариантами они не являются, и серверная проверка арности их так же исключает.</p>
 */
export function filledVariants(row: Record<string, unknown>, subFields: SchemaField[]): string[] {
  return subFields.filter(sf => isVariantFilled(row[sf.key])).map(sf => sf.title);
}

/**
 * Текст предупреждения о нескольких заполненных вариантах — одинаковый в списке и в редакторе.
 *
 * <p>Про «попадёт в документ» не говорим: в data.json уходят оба ключа, а что напечатается, решает
 * блок типа в шаблоне — утверждать за него нечего. Обещаем только то, что гарантирует код.</p>
 */
export function overfilledNote(titles: string[]): string {
  return `Заполнено вариантов: ${titles.length} (${titles.join(', ')}), а должен быть один. `
    + 'В данные уйдут все; что попадёт в документ, решит блок типа в шаблоне. '
    + 'Редактор откроет первый и при правке потеряет остальные.';
}

/** Вариант считается заполненным: непустой массив / FieldRef / непустой объект / непустая строка. */
export function isVariantFilled(v: unknown): boolean {
  if (v == null) return false;
  if (isFieldRef(v)) return true;
  if (Array.isArray(v)) return v.length > 0;
  if (typeof v === 'object') return Object.keys(v as object).length > 0;
  return String(v).trim() !== '';
}

/** Короткая сводка активного варианта union — для свёрнутой строки во вложенном режиме. */
export function unionSummary(sf: SchemaField | null, val: unknown, defs: FieldTypeDefs = {}): string {
  if (!sf) return '(пусто)';
  if (!isVariantFilled(val)) return `${sf.title}: —`;
  if (isFieldRef(val)) return `${sf.title} → ${val.displayName}`;
  if (Array.isArray(val)) return `${sf.title} · ${val.length} стр.`;
  return `${sf.title}: ${formatFieldValue(sf, val, defs).slice(0, 40)}`; // формат по типу (issue #611)
}
