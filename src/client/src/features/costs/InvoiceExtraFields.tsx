import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Warning } from '@/shared/ui/icons';
import { TextField } from '@/shared/ui/TextField';
import { PrimitiveInput } from '@/features/document-sets/fields/PrimitiveInput';
import { isLockedField } from '@/features/document-sets/fields/lockedFields';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import type { SchemaField } from '@/shared/api/schema';
import { apiError } from '@/shared/utils/apiError';
import type { InvoiceExtras } from './invoiceExtras';
import { toNumber } from './invoiceLines';

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
      {field.typeId ? <TypedInput {...props} />
        : field.type === 'number' ? <NumberInput {...props} />
        : <PrimitiveInput {...props} />}
    </div>
  );
}

type InputProps = Parameters<typeof PrimitiveInput>[0];

/**
 * Поле со своим типом (примитив, перечисление-справочник): определение читается, только когда нужно.
 *
 * Пока определения нет, поле только показывает значение и говорит почему (ревью PR #1506): без него
 * перечисление объявило бы «Нет вариантов — добавьте их в схеме», а примитив-дата стал бы простым
 * текстом, и набранное сервер отверг бы при сохранении.
 */
function TypedInput(props: InputProps) {
  const primitives = useListPrimitiveTypes();
  const enums = useListEnumTypes();
  const enumField = props.field.type === 'enum';
  const query = enumField ? enums : primitives;
  const id = props.field.typeId;
  const primitiveTypeDef = enumField ? undefined : primitives.data?.find(t => t.id === id);
  const enumTypeDef = enumField ? enums.data?.find(t => t.id === id) : undefined;
  if (primitiveTypeDef || enumTypeDef)
    return <PrimitiveInput {...props} primitiveTypeDef={primitiveTypeDef} enumTypeDef={enumTypeDef} />;
  const why = query.isPending && query.error == null ? 'Тип поля загружается…'
    : query.error != null ? `Тип поля не прочитан: ${apiError(query.error, 'сервер отказал')}`
    : 'Тип поля не найден — значение только читается';
  return <PrimitiveInput {...props} field={{ ...props.field, type: 'string' }} readOnly hint={why} />;
}

const shownNumber = (value: unknown) =>
  typeof value === 'number' ? String(value).replace('.', ',') : value == null ? '' : String(value);

/**
 * Число — с запятой и пробелами, как суммы шапки (ревью PR #1506): `type="number"` браузера с чужой
 * локалью считает «1 234,50» недопустимым и молча отдаёт пустоту. Уезжает числом — сервер сверяет вид
 * значения поля; не число уезжает как набрано, и отказ сервера назовёт поле.
 */
function NumberInput({ field, value, onChange, readOnly, label, hint }: InputProps) {
  const [text, setText] = useState(() => shownNumber(value));
  // Значение сменили не здесь (перечитали счёт, «Взять») — показать его. Своё набранное не трогаем:
  // «1,» уже значит 1, и переписанный текст сдвинул бы курсор.
  const [seen, setSeen] = useState(value);
  if (seen !== value) {
    setSeen(value);
    if (value !== text && !(typeof value === 'number' && toNumber(text) === value)) setText(shownNumber(value));
  }
  const invalid = text.trim() !== '' && toNumber(text) === null;
  return (
    <TextField label={label ?? field.title} required={readOnly ? undefined : field.required} readOnly={readOnly}
      hint={invalid ? 'Не число' : hint} invalid={invalid} inputMode="decimal" value={text}
      onChange={e => {
        const typed = e.target.value;
        setText(typed);
        onChange(typed.trim() === '' ? null : toNumber(typed) ?? typed);
      }} />
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
        <Icon icon={Warning} size={18} className="shrink-0 mt-0.5" />
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
