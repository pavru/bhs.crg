import type { DocumentType } from '@/shared/api/types';
import { parseSchemaFields } from '@/shared/api/schema';

/** Имена типов документов, ссылающихся на данный тип поля (primitive/enum) полем с этим typeId
 *  (по своим схемам). Единственная причина занятости типа поля = ссылки из схем, и она полностью
 *  выводима на клиенте из уже загруженных схем — тот же критерий, что и backend-guard удаления. */
export function findReferencingTypeNames(typeId: string, kind: 'primitive' | 'enum', allDocTypes: DocumentType[]): string[] {
  return allDocTypes
    .filter(dt => parseSchemaFields(dt.schema).some(f => f.type === kind && f.typeId === typeId))
    .map(dt => dt.name);
}
