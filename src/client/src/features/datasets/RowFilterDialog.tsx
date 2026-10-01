import { useState } from 'react';
import { Plus, Trash2, GitBranch } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import type { FilterCondition, FilterGroup, FilterOp, RowFilterDef } from '@/shared/api/types';
import { cleanFilterNode, isEditableFilterRoot } from '@/shared/api/datasetHelpers';
import { apiError } from '@/shared/utils/apiError';
import {
  conditionProblem, fromDraft, newCondition, newGroup, operatorsFor, opLabel, toDraft,
  withColumn, withOperator, type DraftGroup, type FilterColumn,
} from './rowFilterModel';
import { RowFilterValue } from './RowFilterValue';

// Reused field styling for condition selects/inputs.
const FIELD_CLS = 'border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1';

// ─── Logic toggle ─────────────────────────────────────────────────────────────

function LogicToggle({
  logic,
  onChange,
}: {
  logic: 'and' | 'or';
  onChange: (l: 'and' | 'or') => void;
}) {
  return (
    <div className="flex rounded text-xs font-bold overflow-hidden shrink-0 border border-stroke">
      {(['and', 'or'] as const).map(l => (
        <button
          key={l}
          type="button"
          onClick={() => onChange(l)}
          className={`px-2.5 py-1 transition-colors ${
            logic === l ? 'bg-brand text-white' : 'bg-surface text-fg3'
          }`}
        >
          {l.toUpperCase()}
        </button>
      ))}
    </div>
  );
}

// ─── Condition row ────────────────────────────────────────────────────────────

function FilterConditionRow({
  cond,
  columns,
  onChange,
  onRemove,
}: {
  cond: FilterCondition;
  columns: FilterColumn[];
  onChange: (c: FilterCondition) => void;
  onRemove: () => void;
}) {
  const column = columns.find(c => c.name === cond.column);
  // Колонке — её операторы (issue #1133). Оператор сохранённого условия, который к ней не подходит,
  // остаётся в списке и назван: молча подменить его значило бы переписать чужое условие.
  const allowed = operatorsFor(column);
  const ops = allowed.includes(cond.op) ? allowed : [cond.op, ...allowed];
  // Подсказка, а не запрет (issue #1137): годен ли отбор, решает сервер при сохранении. Здесь —
  // то, что видно сразу и без запроса; ошибись эта копия правил, она не запрёт годный отбор.
  const problem = conditionProblem(cond, columns);
  // Колонка условия, которой в источнике уже нет, тоже остаётся в списке: иначе выбор показал бы
  // «— колонка —» у условия, которое в базе стоит на конкретной колонке.
  const missing = cond.column !== '' && !column;

  return (
    <div>
      <div className="flex items-start gap-1.5 group/cond">
        {/* Column */}
        {columns.length > 0 ? (
          <select
            value={cond.column}
            onChange={e => onChange(withColumn(cond, e.target.value, columns))}
            className={FIELD_CLS}
            style={{ minWidth: '120px', maxWidth: '160px' }}
          >
            <option value="">— колонка —</option>
            {missing && <option value={cond.column}>{cond.column}</option>}
            {columns.map(c => (
              <option key={c.name} value={c.name} disabled={!!c.unavailable && c.name !== cond.column}>
                {c.unavailable ? `${c.name} — ${c.unavailable}` : c.name}
              </option>
            ))}
          </select>
        ) : (
          <input
            value={cond.column}
            onChange={e => onChange({ ...cond, column: e.target.value })}
            placeholder="Колонка"
            className={FIELD_CLS}
            style={{ width: '120px' }}
          />
        )}

        {/* Operator */}
        <select
          value={cond.op}
          // Значение НЕ сбрасываем при смене оператора (issue #401): оно перекладывается туда, где его
          // ждёт сервер, — одно в value, границы и список в values (withOperator).
          onChange={e => onChange(withOperator(cond, e.target.value as FilterOp))}
          className={`${FIELD_CLS} shrink-0`}
          style={{ width: '148px' }}
        >
          {ops.map(op => (
            <option key={op} value={op}>{opLabel(op)}</option>
          ))}
        </select>

        {/* Value */}
        <RowFilterValue cond={cond} kind={column?.kind} onChange={onChange} />

        {/* Remove */}
        <button
          type="button"
          onClick={onRemove}
          className="p-1 rounded opacity-0 group-hover/cond:opacity-100 transition-all text-fg4 hover:text-danger"
          title="Удалить условие"
        >
          <Trash2 size={12} />
        </button>
      </div>
      {problem && <p className="mt-0.5 text-xs text-danger">Похоже, условие не выполнится: {problem}.</p>}
    </div>
  );
}

// ─── Group editor (recursive) ─────────────────────────────────────────────────

const DEPTH_COLORS = ['var(--f-brand)', 'color-mix(in srgb, var(--f-brand) 50%, var(--f-fg3))', 'var(--f-fg3)'];

function FilterGroupEditor({
  group,
  onChange,
  onRemove,
  depth,
  columns,
}: {
  group: DraftGroup;
  onChange: (g: DraftGroup) => void;
  onRemove?: () => void;
  depth: number;
  columns: FilterColumn[];
}) {
  function setLogic(l: 'and' | 'or') {
    onChange({ ...group, logic: l });
  }

  function addCondition() {
    onChange({ ...group, children: [...group.children, newCondition()] });
  }

  function addSubGroup() {
    onChange({ ...group, children: [...group.children, newGroup()] });
  }

  // Ключ строки возвращаем узлу здесь: строка условия о ключах не знает и отдаёт обычный узел дерева.
  function updateChild(i: number, node: FilterCondition | DraftGroup) {
    onChange({
      ...group,
      children: group.children.map((c, idx) => (idx === i ? { ...node, key: c.key } as typeof c : c)),
    });
  }

  function removeChild(i: number) {
    onChange({ ...group, children: group.children.filter((_, idx) => idx !== i) });
  }

  const accentColor = DEPTH_COLORS[Math.min(depth, DEPTH_COLORS.length - 1)];

  const content = (
    <div>
      {/* Group header */}
      <div className="flex items-center gap-2 flex-wrap">
        <LogicToggle logic={group.logic} onChange={setLogic} />
        <button
          type="button"
          onClick={addCondition}
          className="flex items-center gap-1 text-xs px-2 py-1 rounded transition-colors text-fg2 bg-muted hover:bg-brand-subtle"
        >
          <Plus size={11} /> Условие
        </button>
        {depth < 2 && (
          <button
            type="button"
            onClick={addSubGroup}
            className="flex items-center gap-1 text-xs px-2 py-1 rounded transition-colors text-fg2 bg-muted hover:bg-brand-subtle"
          >
            <GitBranch size={11} /> Группа
          </button>
        )}
        {onRemove && (
          <button
            type="button"
            onClick={onRemove}
            className="flex items-center gap-1 text-xs px-2 py-1 rounded ml-auto transition-colors text-fg4 hover:text-danger"
            title="Удалить группу"
          >
            <Trash2 size={11} /> Удалить группу
          </button>
        )}
      </div>

      {/* Children */}
      {group.children.length > 0 ? (
        <div className="mt-2 space-y-1.5">
          {group.children.map((child, i) => {
            // Ключ — свой у строки, а не её номер: строка помнит недобранное значение списка и вид
            // поля, и при удалении условия это не должно переехать к следующему.
            if (child.type === 'condition') {
              return (
                <FilterConditionRow
                  key={child.key}
                  cond={child}
                  columns={columns}
                  onChange={c => updateChild(i, c)}
                  onRemove={() => removeChild(i)}
                />
              );
            }
            return (
              <FilterGroupEditor
                key={child.key}
                group={child}
                depth={depth + 1}
                columns={columns}
                onChange={g => updateChild(i, g)}
                onRemove={() => removeChild(i)}
              />
            );
          })}
        </div>
      ) : (
        <p className="mt-2 text-xs text-fg4">
          Нет условий в этой группе.
        </p>
      )}
    </div>
  );

  // Root: no visual box
  if (depth === 0) return content;

  // Sub-group: visually wrapped (left accent colour is depth-based → stays inline)
  return (
    <div className="rounded-r-lg p-2.5 bg-base" style={{ borderLeft: `3px solid ${accentColor}` }}>
      {content}
    </div>
  );
}

// ─── Main dialog ──────────────────────────────────────────────────────────────

export function RowFilterDialog({
  columns,
  initial,
  onSave,
  onClose,
}: {
  /** Колонки источника с их видами; без них (шаблон обработки) колонка вписывается текстом. */
  columns?: FilterColumn[];
  initial: RowFilterDef | null;
  /**
   * Сохранение. Обещание ждём: отказ сервера («такой отбор источник не выполнит») показываем здесь
   * же, и диалог остаётся открытым — исправить условие можно только в нём.
   */
  onSave: (filter: RowFilterDef | null) => void | Promise<unknown>;
  onClose: () => void;
}) {
  // Негодную форму сохранённого отбора заменяем пустым корнем и говорим об этом вслух (ниже):
  // редактировать в ней нечего, а падать диалогу нельзя — сюда приходят ПО ОТКАЗУ сервера
  // «исправьте условия отбора» (issue #966, ревью PR #1058).
  const unreadable = initial != null && !isEditableFilterRoot(initial);
  const [root, setRoot] = useState<DraftGroup>(
    () => (isEditableFilterRoot(initial) ? toDraft(initial!) as DraftGroup : newGroup())
  );

  const [saving, setSaving] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);

  async function commit(filter: RowFilterDef | null) {
    setSaving(true);
    setRefusal(null);
    try {
      await onSave(filter);
      onClose();
    } catch (e) {
      setRefusal(apiError(e, 'Не удалось сохранить отбор'));
      setSaving(false);
    }
  }

  const handleSave = () => commit(cleanFilterNode(fromDraft(root)) as FilterGroup | null);
  const handleReset = () => commit(null);

  const hasAny = root.children.length > 0;

  return (
    <Modal
      open={true}
      onOpenChange={o => { if (!o) onClose(); }}
      title="Фильтрация строк"
      wide
      footer={
        <div className="flex gap-2 items-center">
          <button
            onClick={handleSave}
            disabled={saving}
            className="px-4 py-2 rounded-md text-sm font-medium text-white bg-brand disabled:opacity-50"
          >
            {saving ? 'Сохранение…' : 'Сохранить'}
          </button>
          <button
            onClick={onClose}
            className="px-4 py-2 rounded-md text-sm font-medium text-fg2 bg-muted"
          >
            Отмена
          </button>
          {hasAny && (
            <button
              onClick={handleReset}
              disabled={saving}
              className="ml-auto px-4 py-2 rounded-md text-sm font-medium text-danger bg-muted disabled:opacity-50"
            >
              Сбросить фильтр
            </button>
          )}
        </div>
      }
    >
      {unreadable && (
        <p className="text-xs mb-3 text-danger">
          Сохранённый отбор не прочитан — он записан не деревом условий. Источник такой отбор не
          выполняет; заданное здесь заменит его целиком.
        </p>
      )}

      <p className="text-xs mb-4 text-fg4">
        Строки, не прошедшие фильтр, исключаются до маппинга.
        Вычисляемые колонки (если заданы) доступны для фильтрации.
        Можно вкладывать группы с разной логикой (AND/OR).
      </p>

      <div className="rounded-lg p-3 border border-stroke bg-surface" style={{ minHeight: '60px' }}>
        <FilterGroupEditor
          group={root}
          // Отказ сервера — про отбор, который отправляли. Тронули условие — он уже про другое
          // дерево, и висеть рядом с исправленным условием ему нельзя.
          onChange={next => { setRoot(next); setRefusal(null); }}
          onRemove={undefined}
          depth={0}
          columns={columns ?? []}
        />
      </div>

      {refusal && <p role="alert" className="mt-3 text-xs text-danger">{refusal}</p>}
    </Modal>
  );
}
