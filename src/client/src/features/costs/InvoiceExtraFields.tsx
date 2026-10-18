import { TriangleAlert } from 'lucide-react';
import { PrimitiveInput } from '@/features/document-sets/fields/PrimitiveInput';
import { isLockedField } from '@/features/document-sets/fields/lockedFields';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import type { SchemaField } from '@/shared/api/schema';
import { apiError } from '@/shared/utils/apiError';
import type { InvoiceExtras } from './invoiceExtras';

/**
 * Поля, дописанные в тип «Счёт» (issue #1482). Тип расширяемый: заказчик дописывает свои необязательные
 * поля, сервер их хранит и сверяет вид значения, а форма раньше их не показывала — значение, положенное
 * распознаванием, было бы невидимо, и снять с него метку было бы негде.
 *
 * <p>Поле рисует тот же `PrimitiveInput`, что реквизиты документов: вид поля один на всё приложение.</p>
 */

export function ExtraField({ field, value, set, locked, hint, frame }: {
  field: SchemaField; value: unknown; set: (key: string, next: unknown) => void;
  locked: boolean; hint?: string; frame: string;
}) {
  // Пустое уезжает `null`, а не «»: человек, стерев значение, имеет в виду «значения нет».
  const onChange = (next: unknown) => set(field.key, next === '' ? null : next);
  // Запертое поле кладёт код, не человек (ТЗ CORE-20.2): дописанному полю замка не поставить, но форма по
  // схеме обязана его уважать, как остальные.
  const props = { field, value, onChange, invalid: false, readOnly: locked || isLockedField(field), label: field.title || field.key, hint };
  return (
    // Флажок — по высоте поля рядом: в одной строке с полем ввода он не висит у верхнего края.
    <div className={`${frame} ${field.type === 'boolean' ? 'min-h-14 flex items-center' : ''}`}>
      {field.typeId ? <TypedInput {...props} /> : <PrimitiveInput {...props} />}
    </div>
  );
}

/** Поле со своим типом (примитив, перечисление-справочник): определение читается, только когда нужно. */
function TypedInput(props: Parameters<typeof PrimitiveInput>[0]) {
  const primitives = useListPrimitiveTypes();
  const enums = useListEnumTypes();
  const id = props.field.typeId;
  return (
    <PrimitiveInput {...props}
      primitiveTypeDef={props.field.type === 'primitive' ? primitives.data?.find(t => t.id === id) : undefined}
      enumTypeDef={props.field.type === 'enum' ? enums.data?.find(t => t.id === id) : undefined} />
  );
}

/**
 * Чего форма не показывает, — строкой, а не молча (issue #1482): поле в типе есть, значение в счёте
 * может лежать, и человек должен знать, что оно существует и сохраняется как есть.
 */
export function UnshownExtras({ extras }: { extras: InvoiceExtras }) {
  if (extras.error != null)
    return (
      <p className="flex items-start gap-2 text-xs text-warning" data-testid="invoice-extras-unread">
        <TriangleAlert size={14} className="shrink-0 mt-0.5" />
        <span>
          Схема типа «Счёт» не прочитана: {apiError(extras.error, 'сервер отказал')}. Поля, дописанные в тип,
          не показаны — их значения в счёте сохраняются как есть.
        </span>
      </p>
    );
  if (extras.unshown.length === 0) return null;
  return (
    <p className="text-xs text-fg3" data-testid="invoice-extras-unshown">
      В типе «Счёт» есть поля, которых форма счёта не показывает:{' '}
      {extras.unshown.map(({ field, why }) => `«${field.title || field.key}» (${why})`).join(', ')}. Их значения
      в счёте сохраняются как есть.
    </p>
  );
}
