import { createContext, useContext } from 'react';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import type { EnumTypeDef, PrimitiveTypeDef } from '@/shared/api/types';

export const FieldTypeRegistry = createContext<{
  enumTypes: EnumTypeDef[];
  primitiveTypes: PrimitiveTypeDef[];
} | null>(null);

export function useFieldTypeRegistry() {
  const own = useContext(FieldTypeRegistry);
  const { data: enumTypes = [] } = useListEnumTypes(own === null);
  const { data: primitiveTypes = [] } = useListPrimitiveTypes(own === null);
  return own ?? { enumTypes, primitiveTypes };
}
