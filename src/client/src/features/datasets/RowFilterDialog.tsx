import { useState } from 'react';
import { Plus, Trash2, GitBranch } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import type { FilterCondition, FilterGroup, FilterNode, FilterOp, RowFilterDef } from '@/shared/api/types';
import { FILTER_OP_LABELS, FILTER_OPS_NO_VALUE } from '@/shared/api/types';
import { cleanFilterNode } from '@/shared/api/datasetHelpers';

const ALL_OPS: FilterOp[] = [
  'eq', 'neq', 'contains', 'not_contains',
  'starts_with', 'ends_with',
  'gt', 'gte', 'lt', 'lte',
  'is_empty', 'is_not_empty',
];

function makeCondition(): FilterCondition {
  return { type: 'condition', column: '', op: 'eq', value: '' };
}

function makeGroup(): FilterGroup {
  return { type: 'group', logic: 'and', children: [] };
}

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
  columns: string[];
  onChange: (c: FilterCondition) => void;
  onRemove: () => void;
}) {
  const noValue = FILTER_OPS_NO_VALUE.includes(cond.op);

  return (
    <div className="flex items-center gap-1.5 group/cond">
      {/* Column */}
      {columns.length > 0 ? (
        <select
          value={cond.column}
          onChange={e => onChange({ ...cond, column: e.target.value })}
          className={FIELD_CLS}
          style={{ minWidth: '120px', maxWidth: '160px' }}
        >
          <option value="">— колонка —</option>
          {columns.map(c => <option key={c} value={c}>{c}</option>)}
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
        onChange={e => onChange({ ...cond, op: e.target.value as FilterOp, value: undefined })}
        className={`${FIELD_CLS} shrink-0`}
        style={{ width: '148px' }}
      >
        {ALL_OPS.map(op => (
          <option key={op} value={op}>{FILTER_OP_LABELS[op]}</option>
        ))}
      </select>

      {/* Value */}
      {!noValue ? (
        <input
          value={cond.value ?? ''}
          onChange={e => onChange({ ...cond, value: e.target.value })}
          placeholder="Значение"
          className={`${FIELD_CLS} flex-1 min-w-0`}
        />
      ) : (
        <div className="flex-1" />
      )}

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
  group: FilterGroup;
  onChange: (g: FilterGroup) => void;
  onRemove?: () => void;
  depth: number;
  columns: string[];
}) {
  function setLogic(l: 'and' | 'or') {
    onChange({ ...group, logic: l });
  }

  function addCondition() {
    onChange({ ...group, children: [...group.children, makeCondition()] });
  }

  function addSubGroup() {
    onChange({ ...group, children: [...group.children, makeGroup()] });
  }

  function updateChild(i: number, node: FilterNode) {
    onChange({ ...group, children: group.children.map((c, idx) => idx === i ? node : c) });
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
            if (child.type === 'condition') {
              return (
                <FilterConditionRow
                  key={i}
                  cond={child}
                  columns={columns}
                  onChange={c => updateChild(i, c)}
                  onRemove={() => removeChild(i)}
                />
              );
            }
            return (
              <FilterGroupEditor
                key={i}
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
  columns?: string[];
  initial: RowFilterDef | null;
  onSave: (filter: RowFilterDef | null) => void;
  onClose: () => void;
}) {
  const [root, setRoot] = useState<FilterGroup>(
    initial ?? { type: 'group', logic: 'and', children: [] }
  );

  function handleSave() {
    const cleaned = cleanFilterNode(root) as FilterGroup | null;
    onSave(cleaned);
    onClose();
  }

  function handleReset() {
    onSave(null);
    onClose();
  }

  const hasAny = root.children.length > 0;

  return (
    <Modal
      open={true}
      onOpenChange={o => { if (!o) onClose(); }}
      title="Фильтрация строк"
      wide
    >
      <p className="text-xs mb-4 text-fg4">
        Строки, не прошедшие фильтр, исключаются до маппинга.
        Вычисляемые колонки (если заданы) доступны для фильтрации.
        Можно вкладывать группы с разной логикой (AND/OR).
      </p>

      <div className="rounded-lg p-3 mb-4 border border-stroke bg-surface" style={{ minHeight: '60px' }}>
        <FilterGroupEditor
          group={root}
          onChange={setRoot}
          onRemove={undefined}
          depth={0}
          columns={columns ?? []}
        />
      </div>

      <div className="flex gap-2 items-center">
        <button
          onClick={handleSave}
          className="px-4 py-2 rounded-md text-sm font-medium text-white bg-brand"
        >
          Сохранить
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
            className="ml-auto px-4 py-2 rounded-md text-sm font-medium text-danger bg-muted"
          >
            Сбросить фильтр
          </button>
        )}
      </div>
    </Modal>
  );
}
