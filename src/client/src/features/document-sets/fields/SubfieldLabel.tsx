import type { SchemaField } from '@/shared/api/schema';

/** Подполя, у редактора которых своей подписи нет: составное, массив, ссылки, картинка, файл. */
const LABELLED = ['complex', 'array', 'doc-ref', 'doc-array', 'image', 'file'];

/**
 * Подпись над подполем составного значения — одна на строку массива и на группу подполей (issue
 * #1384: вынесена из `ComplexFields.tsx`, где жила двумя копиями). Простые поля подписывают себя сами.
 */
export function SubfieldLabel({ sf }: { sf: SchemaField }) {
  if (!LABELLED.includes(sf.type)) return null;
  return (
    <label className="block text-sm font-medium text-on-surface-variant mb-1">
      {sf.title}{sf.required && <span className="ml-0.5 text-error">*</span>}
    </label>
  );
}
