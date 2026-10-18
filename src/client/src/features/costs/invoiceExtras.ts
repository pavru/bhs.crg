import { createContext, useContext, useMemo } from 'react';
import { useDocumentType } from '@/shared/api/documentTypes';
import { parseSchemaFields, type SchemaField } from '@/shared/api/schema';
import { extraBlock, extraFields, type ExtraFields, type InvoiceBlock } from './invoiceFields';

/**
 * Поля, дописанные в тип «Счёт» (issue #1482): схема типа, прочитанная для формы, и контекст, через
 * который поле формы узнаёт, что оно дописанное. Рисует их `InvoiceExtraFields.tsx`.
 */

export interface InvoiceExtras extends ExtraFields {
  /** Блок «Дополнительно»; `null` — дописанных полей нет, и форма выглядит как раньше. */
  block: InvoiceBlock | null;
  byKey: ReadonlyMap<string, SchemaField>;
  /** Отказ чтения схемы. ⚠️ Не «полей нет»: их список сюда не доехал. */
  error: unknown;
  /** Схема ещё читается: какие поля форма покажет, пока неизвестно (ревью PR #1506). */
  loading: boolean;
}

export function useInvoiceExtras(typeId: string): InvoiceExtras {
  const type = useDocumentType(typeId);
  return useMemo(() => {
    const extras = extraFields(type.data ? parseSchemaFields(type.data.schema) : []);
    return {
      ...extras,
      block: extraBlock(extras.shown),
      byKey: new Map(extras.shown.map(f => [f.key, f])),
      error: type.error,
      loading: type.isPending && type.error == null,
    };
  }, [type.data, type.error, type.isPending]);
}

const ExtrasContext = createContext<ReadonlyMap<string, SchemaField>>(new Map());
export const InvoiceExtrasProvider = ExtrasContext.Provider;

/** Поле схемы, если ключ — дописанное поле; `null` — поле модуля, его рисует форма сама. */
export function useExtraField(key: string): SchemaField | null {
  return useContext(ExtrasContext).get(key) ?? null;
}
