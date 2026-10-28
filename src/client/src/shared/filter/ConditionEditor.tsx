import type { Ref } from 'react';
import type { FilterCondition, FilterOp } from '@/shared/api/types';
import { Select, SelectItem } from '@/shared/ui/Select';
import { columnLabel, operatorsFor, opLabel, withColumn, withOperator, type FilterColumn } from './rowFilterModel';
import { RowFilterValue } from './RowFilterValue';

const FIELD_CLS = 'border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1';

/**
 * Три поля одного условия — колонка, оператор, значение. Один редактор на оба лица отбора: строку
 * диалога дерева условий и чип над таблицей (issue #1091). У каждого свой — значит, однажды колонке-
 * дате один предложил бы календарь, а другой строку.
 *
 * `stacked` — поля столбиком (в окошке чипа); иначе они встают в строку родителя.
 */
export function ConditionEditor({ cond, columns, onChange, stacked = false, hintId, valueRef }: {
  cond: FilterCondition;
  /** Колонки с их видами; пусто (шаблон обработки без источника) — колонка вписывается текстом. */
  columns: FilterColumn[];
  onChange: (c: FilterCondition) => void;
  stacked?: boolean;
  /** `id` строки под условием (`ConditionHint`) — поле значения ссылается на неё, когда в ней пометка. */
  hintId?: string;
  /** Текстовое поле значения — строке под условием: после замены фокус возвращается в него. */
  valueRef?: Ref<HTMLInputElement>;
}) {
  const column = columns.find(c => c.name === cond.column);
  // Колонке — её операторы (issue #1133). Оператор сохранённого условия, который к ней не подходит,
  // остаётся в списке и назван: молча подменить его значило бы переписать чужое условие.
  const allowed = operatorsFor(column);
  const ops = allowed.includes(cond.op) ? allowed : [cond.op, ...allowed];
  // Колонка условия, которой в источнике уже нет, тоже остаётся в списке: иначе выбор показал бы
  // «— колонка —» у условия, которое в базе стоит на конкретной колонке.
  const missing = cond.column !== '' && !column;

  return (
    <div className={stacked ? 'flex flex-col gap-1.5' : 'contents'}>
      {columns.length > 0 ? (
        <Select
          size="sm"
          value={cond.column}
          onValueChange={v => onChange(withColumn(cond, v, columns))}
          emptyItem="— колонка —"
          className={stacked ? undefined : 'w-auto min-w-30 max-w-40'}
          aria-label="Колонка"
        >
          {missing && <SelectItem value={cond.column}>{cond.column}</SelectItem>}
          {columns.map(c => {
            const label = columnLabel(c, c.name);
            return (
              <SelectItem key={c.name} value={c.name} disabled={!!c.unavailable && c.name !== cond.column}>
                {c.unavailable ? `${label} — ${c.unavailable}` : label}
              </SelectItem>
            );
          })}
        </Select>
      ) : (
        <input
          value={cond.column}
          onChange={e => onChange({ ...cond, column: e.target.value })}
          placeholder="Колонка"
          aria-label="Колонка"
          className={`${FIELD_CLS} ${stacked ? '' : 'w-30'}`}
        />
      )}

      <Select
        size="sm"
        value={cond.op}
        // Значение НЕ сбрасываем при смене оператора (issue #401): оно перекладывается туда, где его
        // ждёт сервер, — одно в value, границы и список в values (withOperator).
        onValueChange={v => onChange(withOperator(cond, v as FilterOp))}
        className={stacked ? undefined : 'w-36 shrink-0'}
        aria-label="Оператор"
      >
        {ops.map(op => (
          <SelectItem key={op} value={op}>{opLabel(op)}</SelectItem>
        ))}
      </Select>

      <RowFilterValue cond={cond} column={column} hintId={hintId} valueRef={valueRef} onChange={onChange} />
    </div>
  );
}
