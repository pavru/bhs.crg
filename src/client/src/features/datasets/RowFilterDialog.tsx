import { useState } from 'react';
import { Plus, Trash2, GitBranch } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import type { FilterCondition, FilterGroup, RowFilterDef } from '@/shared/api/types';
import { cleanFilterNode, isEditableFilterRoot } from '@/shared/api/datasetHelpers';
import {
  conditionProblem, fromDraft, newCondition, newGroup, pruneDraft, toDraft,
  type DraftGroup, type FilterColumn,
} from '@/shared/filter/rowFilterModel';
import { ConditionEditor } from '@/shared/filter/ConditionEditor';
import { useDialogSave } from './useDialogSave';

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
  path,
  columns,
  onChange,
  onRemove,
}: {
  cond: FilterCondition;
  /** Номер условия по уровням от корня («2.1») — им сервер называет условие в отказе. */
  path: string;
  columns: FilterColumn[];
  onChange: (c: FilterCondition) => void;
  onRemove: () => void;
}) {
  // Подсказка, а не запрет (issue #1137): годен ли отбор, решает сервер при сохранении. Здесь —
  // то, что видно сразу и без запроса; ошибись эта копия правил, она не запрёт годный отбор.
  const problem = conditionProblem(cond, columns);

  return (
    <div>
      <div className="flex items-start gap-1.5 group/cond">
        <span className="w-6 shrink-0 pt-1.5 text-[10px] tabular-nums text-fg4" title={`Условие ${path}`}>{path}</span>
        <ConditionEditor cond={cond} columns={columns} onChange={onChange} />

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
  path,
  onChange,
  onRemove,
  depth,
  columns,
}: {
  group: DraftGroup;
  /** Номер самой группы по уровням от корня; у корня пусто. */
  path: string;
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
            // Счёт узлов — тот же, что у сервера: по уровням от корня, с единицы.
            const childPath = path ? `${path}.${i + 1}` : `${i + 1}`;
            // Ключ — свой у строки, а не её номер: строка помнит недобранное значение списка и вид
            // поля, и при удалении условия это не должно переехать к следующему.
            if (child.type === 'condition') {
              return (
                <FilterConditionRow
                  key={child.key}
                  cond={child}
                  path={childPath}
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
                path={childPath}
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

const SOURCE_WORDING = {
  title: 'Фильтрация строк',
  note: 'Строки, не прошедшие фильтр, исключаются до маппинга. Вычисляемые колонки (если заданы) '
    + 'доступны для фильтрации. Можно вкладывать группы с разной логикой (AND/OR).',
  save: 'Сохранить',
  saving: 'Сохранение…',
  reset: 'Сбросить фильтр',
};

export function RowFilterDialog({
  columns,
  initial,
  onSave,
  onClose,
  wording,
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
  /**
   * Слова диалога, когда он открыт не у источника набора, а расширенным режимом отбора таблицы
   * (issue #1091): там отбор не «сохраняется» в обработку, а применяется к экрану, и про маппинг с
   * вычисляемыми колонками говорить незачем. Не задано — слова источника.
   */
  wording?: { title: string; note: string; save: string; saving: string; reset: string };
}) {
  const words = wording ?? SOURCE_WORDING;
  // Негодную форму сохранённого отбора заменяем пустым корнем и говорим об этом вслух (ниже):
  // редактировать в ней нечего, а падать диалогу нельзя — сюда приходят ПО ОТКАЗУ сервера
  // «исправьте условия отбора» (issue #966, ревью PR #1058).
  const unreadable = initial != null && !isEditableFilterRoot(initial);
  const [root, setRoot] = useState<DraftGroup>(
    () => (isEditableFilterRoot(initial) ? toDraft(initial!) as DraftGroup : newGroup())
  );

  const { saving, refusal, clearRefusal, commit, close } = useDialogSave(onClose, 'Не удалось сохранить отбор');

  // На экране с этого момента — ровно то дерево, что уехало: пустые строки на сервер не идут, а он
  // называет условие номером по отправленному («условие 2.1»). Останься они, номер в отказе указывал
  // бы на соседнюю строку.
  function handleSave() {
    const sent = pruneDraft(root);
    setRoot(sent);
    void commit(() => onSave(cleanFilterNode(fromDraft(sent)) as FilterGroup | null));
  }
  const handleReset = () => void commit(() => onSave(null));

  const hasAny = root.children.length > 0;

  return (
    <Modal
      open={true}
      onOpenChange={o => { if (!o) close(); }}
      title={words.title}
      wide
      footer={
        <div className="flex gap-2 items-center">
          <button
            onClick={handleSave}
            disabled={saving}
            className="px-4 py-2 rounded-md text-sm font-medium text-white bg-brand disabled:opacity-50"
          >
            {saving ? words.saving : words.save}
          </button>
          <button
            onClick={close}
            disabled={saving}
            className="px-4 py-2 rounded-md text-sm font-medium text-fg2 bg-muted disabled:opacity-50"
          >
            Отмена
          </button>
          {hasAny && (
            <button
              onClick={handleReset}
              disabled={saving}
              className="ml-auto px-4 py-2 rounded-md text-sm font-medium text-danger bg-muted disabled:opacity-50"
            >
              {words.reset}
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

      <p className="text-xs mb-4 text-fg4">{words.note}</p>

      <div className="rounded-lg p-3 border border-stroke bg-surface" style={{ minHeight: '60px' }}>
        <FilterGroupEditor
          group={root}
          path=""
          // Отказ сервера — про отбор, который отправляли. Тронули условие — он уже про другое
          // дерево, и висеть рядом с исправленным условием ему нельзя.
          onChange={next => { setRoot(next); clearRefusal(); }}
          onRemove={undefined}
          depth={0}
          columns={columns ?? []}
        />
      </div>

      {refusal && <p role="alert" className="mt-3 text-xs text-danger">{refusal}</p>}
    </Modal>
  );
}
