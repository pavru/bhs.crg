/**
 * Чистые помощники страницы типов документов — те, что нужны СРАЗУ НЕСКОЛЬКИМ её частям.
 *
 * Отдельным файлом не только ради объёма: `react-refresh/only-export-components` запрещает
 * экспортировать из файла с компонентами обычные функции. Одиночные помощники поэтому остались
 * приватными внутри своих компонентов, а сюда вынесены только разделяемые (issue #1031).
 */
import type { PickType } from '@/shared/ui/TypePicker';
import type { DocumentType } from '@/shared/api/types';
import { resolveEffectiveFields } from '@/shared/api/schema';

/** Родительский тип как `PickType` для `TypePickerField` (section — единая шапка группы пикера). */
export function toParentPickTypes(types: DocumentType[]): PickType[] {
  return types.map(dt => ({ id: dt.id, name: dt.name, code: dt.code, section: 'Родительский тип' }));
}

/** Число эффективных полей типа — для счётчика в списке-пилюле (issue #197). */
export function fieldCount(docType: DocumentType, allDocTypes: DocumentType[]): number {
  return resolveEffectiveFields(docType, allDocTypes).length;
}
