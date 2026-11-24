import { createContext } from 'react';
import type { CommonDataEntry, DocumentType } from '@/shared/api/types';

export type ReferenceCatalogEntry = Pick<CommonDataEntry, 'id' | 'displayName' | 'compositeTypeId' | 'scope' | 'archived'>;

export interface ReferenceCatalogPage {
  items: ReferenceCatalogEntry[];
  types: Pick<DocumentType, 'id' | 'parentId'>[];
  more: boolean;
}

export const ReferenceCatalogContext = createContext<{
  key: readonly unknown[];
  load: (typeId: string, union: boolean, archived: boolean, q: string, signal: AbortSignal) => Promise<ReferenceCatalogPage>;
} | null>(null);
