import { useState } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import type { ComputedColumn } from '@/shared/api/types';

function ColumnRow({
  col,
  onChange,
  onRemove,
}: {
  col: ComputedColumn;
  onChange: (c: ComputedColumn) => void;
  onRemove: () => void;
}) {
  return (
    <div className="rounded-lg p-3 space-y-2 border border-stroke bg-base">
      <div className="flex items-center gap-2">
        <div className="flex-1 space-y-1">
          <label className="block text-[10px] font-medium uppercase tracking-wide text-fg4">
            Имя новой колонки
          </label>
          <input
            value={col.alias}
            onChange={e => onChange({ ...col, alias: e.target.value })}
            placeholder="Напр.: ФИО"
            className="w-full border border-stroke rounded px-2 py-1.5 text-xs font-mono bg-surface text-fg1"
          />
        </div>
        <button
          onClick={onRemove}
          className="p-1.5 rounded mt-4 text-fg4 hover:text-danger transition-colors"
        >
          <Trash2 size={13} />
        </button>
      </div>
      <div className="space-y-1">
        <label className="block text-[10px] font-medium uppercase tracking-wide text-fg4">
          Выражение (JavaScript)
        </label>
        <input
          value={col.expr}
          onChange={e => onChange({ ...col, expr: e.target.value })}
          placeholder="Напр.: Фамилия + ' ' + Имя"
          className="w-full border border-stroke rounded px-2 py-1.5 text-xs font-mono bg-surface text-fg1"
        />
      </div>
    </div>
  );
}

export function ComputedColumnsDialog({
  initial,
  onSave,
  onClose,
}: {
  initial: ComputedColumn[] | null;
  onSave: (cols: ComputedColumn[] | null) => void;
  onClose: () => void;
}) {
  const [columns, setColumns] = useState<ComputedColumn[]>(initial ?? []);

  function addColumn() {
    setColumns(prev => [...prev, { alias: '', expr: '' }]);
  }

  function updateColumn(i: number, c: ComputedColumn) {
    setColumns(prev => prev.map((p, idx) => idx === i ? c : p));
  }

  function removeColumn(i: number) {
    setColumns(prev => prev.filter((_, idx) => idx !== i));
  }

  function handleSave() {
    const valid = columns.filter(c => c.alias.trim() && c.expr.trim());
    onSave(valid.length > 0 ? valid : null);
    onClose();
  }

  return (
    <Modal
      open={true}
      onOpenChange={o => { if (!o) onClose(); }}
      title="Вычисляемые колонки"
      wide
      footer={
        <div className="flex gap-2">
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
          {columns.length > 0 && (
            <button
              onClick={() => { setColumns([]); onSave(null); onClose(); }}
              className="ml-auto px-4 py-2 rounded-md text-sm font-medium text-danger bg-muted"
            >
              Сбросить
            </button>
          )}
        </div>
      }
    >
      <div className="rounded-lg p-3 mb-4 text-xs space-y-1 bg-base border border-stroke">
        <p className="font-semibold text-fg2">Синтаксис JavaScript</p>
        <p className="text-fg3">
          Имена колонок доступны как переменные. Пробелы заменяются на <code className="font-mono px-1 rounded bg-muted">_</code>.
        </p>
        <p className="text-fg4">
          Примеры:{' '}
          <code className="font-mono text-fg2">{"Фамилия + ' ' + Имя"}</code>
          {' — '}
          <code className="font-mono text-fg2">{"String(parseFloat(Цена) * parseFloat(Кол_во))"}</code>
          {' — '}
          <code className="font-mono text-fg2">{"Статус === 'Принят' ? 'Да' : 'Нет'"}</code>
        </p>
        <p className="text-fg4">
          Вычисляемые колонки применяются до фильтрации и маппинга — их можно использовать в условиях фильтра.
        </p>
      </div>

      <div className="space-y-3 mb-4">
        {columns.length === 0 && (
          <p className="text-xs py-2 text-center text-fg4">
            Нет вычисляемых колонок.
          </p>
        )}
        {columns.map((c, i) => (
          <ColumnRow
            key={i}
            col={c}
            onChange={updated => updateColumn(i, updated)}
            onRemove={() => removeColumn(i)}
          />
        ))}
      </div>

      <button
        onClick={addColumn}
        className="flex items-center gap-1.5 text-xs font-medium px-3 py-1.5 rounded-md text-brand bg-brand-subtle"
      >
        <Plus size={13} /> Добавить колонку
      </button>
    </Modal>
  );
}
