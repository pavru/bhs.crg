import { useRef, useState } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import type { ComputedColumn } from '@/shared/api/types';
import { columnAccessor } from './computedColumnAccess';

function ColumnRow({
  col,
  index,
  onChange,
  onRemove,
  onExprFocus,
}: {
  col: ComputedColumn;
  index: number;
  onChange: (c: ComputedColumn) => void;
  onRemove: () => void;
  onExprFocus: (el: HTMLInputElement, index: number) => void;
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
          onFocus={e => onExprFocus(e.currentTarget, index)}
          placeholder="Напр.: Фамилия + ' ' + Имя"
          className="w-full border border-stroke rounded px-2 py-1.5 text-xs font-mono bg-surface text-fg1"
        />
      </div>
    </div>
  );
}

export function ComputedColumnsDialog({
  initial,
  sourceColumns = [],
  onSave,
  onClose,
}: {
  initial: ComputedColumn[] | null;
  /** Колонки источника в его порядке — для вставки по клику (issue #539). */
  sourceColumns?: string[];
  onSave: (cols: ComputedColumn[] | null) => void;
  onClose: () => void;
}) {
  const [columns, setColumns] = useState<ComputedColumn[]>(initial ?? []);
  // Куда вставлять по клику: последнее выражение, в котором стоял курсор.
  const active = useRef<{ el: HTMLInputElement; index: number } | null>(null);

  function addColumn() {
    setColumns(prev => [...prev, { alias: '', expr: '' }]);
  }

  function updateColumn(i: number, c: ComputedColumn) {
    setColumns(prev => prev.map((p, idx) => idx === i ? c : p));
  }

  function removeColumn(i: number) {
    // Забываем, куда вставлять: индексы поехали, и вставка по клику ушла бы в чужое выражение
    // (или молча никуда, если удалили последнюю строку).
    active.current = null;
    setColumns(prev => prev.filter((_, idx) => idx !== i));
  }

  /** Вставляет обращение к колонке в выражение — в место курсора, если он там стоит. */
  function insert(text: string) {
    const target = active.current;
    const index = target ? target.index : columns.length - 1;
    if (index < 0 || index >= columns.length) return;

    const el = target?.el;
    if (!el || document.activeElement !== el) {
      updateColumn(index, { ...columns[index], expr: columns[index].expr + text });
      return;
    }
    const start = el.selectionStart ?? el.value.length;
    const end = el.selectionEnd ?? start;
    updateColumn(index, { ...columns[index], expr: el.value.slice(0, start) + text + el.value.slice(end) });
    // Каретку возвращаем после перерисовки — иначе она прыгнет в конец строки.
    requestAnimationFrame(() => {
      el.focus();
      el.setSelectionRange(start + text.length, start + text.length);
    });
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
          К колонке можно обратиться тремя способами:{' '}
          <code className="font-mono px-1 rounded bg-muted">Фамилия</code> — по имени, если оно годится
          в имя переменной (пробелы и знаки заменяются на <code className="font-mono px-1 rounded bg-muted">_</code>);{' '}
          <code className="font-mono px-1 rounded bg-muted">get("Кол-во")</code> — по точному заголовку,
          подходит всегда;{' '}
          <code className="font-mono px-1 rounded bg-muted">cols[1]</code> — по номеру колонки,{' '}
          <b>счёт с единицы</b>.
        </p>
        <p className="text-fg4">
          Примеры:{' '}
          <code className="font-mono text-fg2">{"Фамилия + ' ' + Имя"}</code>
          {' — '}
          <code className="font-mono text-fg2">{"String(parseFloat(get('Цена')) * parseFloat(cols[3]))"}</code>
          {' — '}
          <code className="font-mono text-fg2">{"Статус === 'Принят' ? 'Да' : 'Нет'"}</code>
        </p>
        <p className="text-fg4">
          Вычисляемые колонки применяются до фильтрации и маппинга — их можно использовать в условиях фильтра.
        </p>
      </div>

      {sourceColumns.length > 0 && (
        <div className="rounded-lg p-3 mb-4 text-xs bg-base border border-stroke">
          <p className="mb-2 text-fg3">
            Колонки источника — нажмите, чтобы вставить в выражение
            {columns.length === 0 && <span className="text-fg4"> (сначала добавьте колонку)</span>}
          </p>
          <div className="flex flex-wrap gap-1.5">
            {sourceColumns.map((name, i) => (
              <button
                key={name}
                type="button"
                /* Не отдаём фокус кнопке: иначе к моменту клика курсор уже ушёл из выражения
                   и вставлять было бы некуда, кроме конца строки. */
                onMouseDown={e => e.preventDefault()}
                onClick={() => insert(columnAccessor(name))}
                disabled={columns.length === 0}
                title={`${columnAccessor(name)} или cols[${i + 1}]`}
                className="flex items-center gap-1 px-2 py-1 rounded border border-stroke bg-surface
                           hover:border-brand hover:text-brand disabled:opacity-50 disabled:hover:border-stroke
                           disabled:hover:text-fg2 text-fg2"
              >
                <span className="text-fg4 font-mono">{i + 1}</span>
                <span className="font-mono">{name}</span>
              </button>
            ))}
          </div>
        </div>
      )}

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
            index={i}
            onChange={updated => updateColumn(i, updated)}
            onRemove={() => removeColumn(i)}
            onExprFocus={(el, index) => { active.current = { el, index }; }}
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
