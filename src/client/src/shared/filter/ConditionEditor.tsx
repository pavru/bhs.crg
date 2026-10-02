import type { FilterCondition, FilterOp } from '@/shared/api/types';
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
export function ConditionEditor({ cond, columns, onChange, stacked = false }: {
  cond: FilterCondition;
  /** Колонки с их видами; пусто (шаблон обработки без источника) — колонка вписывается текстом. */
  columns: FilterColumn[];
  onChange: (c: FilterCondition) => void;
  stacked?: boolean;
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
        <select
          value={cond.column}
          onChange={e => onChange(withColumn(cond, e.target.value, columns))}
          className={FIELD_CLS}
          style={stacked ? undefined : { minWidth: '120px', maxWidth: '160px' }}
          aria-label="Колонка"
        >
          <option value="">— колонка —</option>
          {missing && <option value={cond.column}>{cond.column}</option>}
          {columns.map(c => {
            const label = columnLabel(c, c.name);
            return (
              <option key={c.name} value={c.name} disabled={!!c.unavailable && c.name !== cond.column}>
                {c.unavailable ? `${label} — ${c.unavailable}` : label}
              </option>
            );
          })}
        </select>
      ) : (
        <input
          value={cond.column}
          onChange={e => onChange({ ...cond, column: e.target.value })}
          placeholder="Колонка"
          aria-label="Колонка"
          className={FIELD_CLS}
          style={stacked ? undefined : { width: '120px' }}
        />
      )}

      <select
        value={cond.op}
        // Значение НЕ сбрасываем при смене оператора (issue #401): оно перекладывается туда, где его
        // ждёт сервер, — одно в value, границы и список в values (withOperator).
        onChange={e => onChange(withOperator(cond, e.target.value as FilterOp))}
        className={`${FIELD_CLS} shrink-0`}
        style={stacked ? undefined : { width: '148px' }}
        aria-label="Оператор"
      >
        {ops.map(op => (
          <option key={op} value={op}>{opLabel(op)}</option>
        ))}
      </select>

      <RowFilterValue cond={cond} column={column} onChange={onChange} />
    </div>
  );
}
