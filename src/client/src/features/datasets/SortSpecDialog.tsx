import { useState } from 'react';
import { Plus, Trash2, ArrowUp, ArrowDown } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import type { SortColumn, SortSpec } from '@/shared/api/types';

function SortRow({
  level, columns, onChange, onRemove, onMoveUp, onMoveDown, isFirst, isLast,
}: {
  level: SortColumn;
  columns?: string[];
  onChange: (l: SortColumn) => void;
  onRemove: () => void;
  onMoveUp: () => void;
  onMoveDown: () => void;
  isFirst: boolean;
  isLast: boolean;
}) {
  return (
    <div className="flex items-center gap-1.5">
      {columns && columns.length > 0 ? (
        <select
          value={level.column}
          onChange={e => onChange({ ...level, column: e.target.value })}
          className="border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1 flex-1 min-w-0"
        >
          <option value="">— колонка —</option>
          {columns.map(c => <option key={c} value={c}>{c}</option>)}
        </select>
      ) : (
        <input
          value={level.column}
          onChange={e => onChange({ ...level, column: e.target.value })}
          placeholder="Колонка"
          className="border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1 flex-1 min-w-0"
        />
      )}
      <div className="flex rounded text-xs font-medium overflow-hidden shrink-0 border border-stroke">
        {(['asc', 'desc'] as const).map(d => (
          <button
            key={d}
            type="button"
            onClick={() => onChange({ ...level, direction: d })}
            className={`px-2 py-1 transition-colors ${level.direction === d ? 'bg-brand text-white' : 'bg-surface text-fg3'}`}
          >
            {d === 'asc' ? '↑ возр.' : '↓ убыв.'}
          </button>
        ))}
      </div>
      <button type="button" onClick={onMoveUp} disabled={isFirst}
        className="p-1 rounded text-fg4 hover:text-fg2 disabled:opacity-25 shrink-0" title="Выше приоритетом">
        <ArrowUp size={12} />
      </button>
      <button type="button" onClick={onMoveDown} disabled={isLast}
        className="p-1 rounded text-fg4 hover:text-fg2 disabled:opacity-25 shrink-0" title="Ниже приоритетом">
        <ArrowDown size={12} />
      </button>
      <button type="button" onClick={onRemove} className="p-1 rounded text-fg4 hover:text-danger shrink-0" title="Удалить">
        <Trash2 size={12} />
      </button>
    </div>
  );
}

export function SortSpecDialog({
  columns,
  initial,
  onSave,
  onClose,
}: {
  columns?: string[];
  initial: SortSpec | null;
  onSave: (spec: SortSpec | null) => void;
  onClose: () => void;
}) {
  const [levels, setLevels] = useState<SortColumn[]>(initial ?? []);

  function addLevel() {
    setLevels(prev => [...prev, { column: '', direction: 'asc' }]);
  }
  function updateLevel(i: number, l: SortColumn) {
    setLevels(prev => prev.map((p, idx) => idx === i ? l : p));
  }
  function removeLevel(i: number) {
    setLevels(prev => prev.filter((_, idx) => idx !== i));
  }
  function moveLevel(i: number, dir: -1 | 1) {
    setLevels(prev => {
      const next = [...prev];
      const swap = i + dir;
      if (swap < 0 || swap >= next.length) return prev;
      [next[i], next[swap]] = [next[swap], next[i]];
      return next;
    });
  }

  function handleSave() {
    const valid = levels.filter(l => l.column.trim());
    onSave(valid.length > 0 ? valid : null);
    onClose();
  }

  return (
    <Modal
      open={true}
      onOpenChange={o => { if (!o) onClose(); }}
      title="Сортировка строк"
      wide
      footer={
        <div className="flex gap-2">
          <button onClick={handleSave} className="px-4 py-2 rounded-md text-sm font-medium text-white bg-brand">
            Сохранить
          </button>
          <button onClick={onClose} className="px-4 py-2 rounded-md text-sm font-medium text-fg2 bg-muted">
            Отмена
          </button>
          {levels.length > 0 && (
            <button
              onClick={() => { setLevels([]); onSave(null); onClose(); }}
              className="ml-auto px-4 py-2 rounded-md text-sm font-medium text-danger bg-muted"
            >
              Сбросить
            </button>
          )}
        </div>
      }
    >
      <p className="text-xs mb-4 text-fg4">
        Сортировка выполняется после вычисляемых колонок — можно сортировать и по ним.
        Пустые значения всегда в конце, независимо от направления. Несколько уровней — по приоритету сверху вниз.
      </p>

      <div className="space-y-2 mb-4">
        {levels.length === 0 && (
          <p className="text-xs py-2 text-center text-fg4">Сортировка не задана.</p>
        )}
        {levels.map((l, i) => (
          <SortRow
            key={i}
            level={l}
            columns={columns}
            onChange={next => updateLevel(i, next)}
            onRemove={() => removeLevel(i)}
            onMoveUp={() => moveLevel(i, -1)}
            onMoveDown={() => moveLevel(i, 1)}
            isFirst={i === 0}
            isLast={i === levels.length - 1}
          />
        ))}
      </div>

      <button
        onClick={addLevel}
        className="flex items-center gap-1.5 text-xs font-medium px-3 py-1.5 rounded-md text-brand bg-brand-subtle"
      >
        <Plus size={13} /> Добавить уровень сортировки
      </button>
    </Modal>
  );
}
