import { useState } from 'react';
import {
  ChevronDown, ChevronUp, SlidersHorizontal, Plus, Trash2, GripVertical, ArrowUp, ArrowDown,
} from 'lucide-react';
import { useUpdateTemplateParameters } from '@/shared/api/templates';
import { moveItem } from '@/shared/utils/moveItem';
import type { Template, TemplateParam } from '@/shared/api/types';

/** Строка редактора: параметр плюс устойчивый идентификатор — он нужен ключом списка при перестановке. */
type Row = { id: string; p: TemplateParam };

/** Свой тип перетаскиваемых данных: текстовый сделал бы ручку источником текста для всей страницы. */
const DRAG_MIME = 'application/x-crg-template-param';

export function parseTemplateParams(json: string | null): TemplateParam[] {
  if (!json) return [];
  try { const a = JSON.parse(json); return Array.isArray(a) ? (a as TemplateParam[]) : []; } catch { return []; }
}

/**
 * Объявление параметров шаблона (имя/подпись/тип/значение по умолчанию) — чтобы одним шаблоном
 * покрыть варианты без дублей. В Typst доступны как <code>data.params.имя</code>; значение по
 * умолчанию можно переопределить на конкретном документе. Сохраняется по каждому изменению
 * (как «Настройки страницы»). Родитель монтирует с key={template.id} — стейт инициализируется из шаблона.
 */
export function TemplateParamsPanel({ template, onSaved }: { template: Template; onSaved: (t: Template) => void }) {
  const [open, setOpen] = useState(false);
  // Строка — это {id, параметр}. Идентификатор нужен КЛЮЧОМ списка: по индексу React оставляет узел
  // на месте и подменяет в нём данные, поэтому фокус остаётся привязан к ПОЗИЦИИ, а не к строке.
  // Проверено: с ключом-индексом два нажатия Enter на «Ниже» возвращали исходный порядок — первое
  // двигало параметр вниз, второе двигало вниз того, кто занял освободившееся место (issue #517).
  const [rows, setRows] = useState<Row[]>(
    () => parseTemplateParams(template.parameters).map(p => ({ id: crypto.randomUUID(), p })));
  const [dragIdx, setDragIdx] = useState<number | null>(null);
  const update = useUpdateTemplateParameters();

  function save(next: Row[]) {
    setRows(next);
    const params = next.map(r => r.p);
    update.mutate({ id: template.id, parameters: params.length ? JSON.stringify(params) : null },
      { onSuccess: onSaved });
  }
  const patch = (i: number, p: Partial<TemplateParam>) =>
    save(rows.map((x, idx) => (idx === i ? { ...x, p: { ...x.p, ...p } } : x)));

  // Порядок объявления = порядок полей при заполнении документа (DocumentTemplateParams перебирает
  // массив как есть), поэтому им надо управлять. Значения на документах хранятся ПО ИМЕНИ параметра,
  // а не по позиции, — перестановка их не задевает.
  //
  // moveItem на «некуда двигать» возвращает тот же массив: сравниваем по ссылке, чтобы клик по
  // «выше» у первой строки не гонял сохранение впустую.
  function move(from: number, to: number) {
    const next = moveItem(rows, from, to);
    if (next !== rows) save(next);
  }

  return (
    <div className="border-t border-stroke bg-surface">
      <button onClick={() => setOpen(v => !v)} aria-expanded={open}
        className="w-full flex items-center gap-2 px-4 py-2.5 hover:bg-base transition-colors text-left">
        <SlidersHorizontal size={13} className="text-fg4" />
        <span className="text-xs font-medium text-fg2 flex-1">Параметры шаблона{rows.length > 0 ? ` (${rows.length})` : ''}</span>
        {open ? <ChevronUp size={13} className="text-fg4" /> : <ChevronDown size={13} className="text-fg4" />}
      </button>
      {open && (
        <div className="px-4 pb-4 pt-3 border-t border-muted space-y-2">
          <p className="text-[11px] text-fg4">
            Доступны в Typst как <code className="text-fg3">data.params.имя</code>. Значение по умолчанию можно
            переопределить на конкретном документе.
          </p>
          {rows.map(({ id, p }, i) => (
            <div key={id}
              className={`flex items-center gap-1.5 rounded ${dragIdx === i ? 'ring-1 ring-brand' : ''}`}
              onDragOver={dragIdx !== null ? e => e.preventDefault() : undefined}
              onDrop={dragIdx !== null ? e => { e.preventDefault(); move(dragIdx, i); setDragIdx(null); } : undefined}>
              {/* Перетаскивание и стрелки вместе — как в списке вариантов перечисления: мышью
                  быстрее через несколько строк, стрелками доступно с клавиатуры.

                  setData обязателен: span — не изначально перетаскиваемый элемент, и Firefox
                  отменяет перетаскивание, если dragstart не положил ничего в dataTransfer
                  (issue #517). Тип СВОЙ, не text/plain: с текстовым типом ручка становится
                  источником перетаскивания для всей страницы, и отпущенная над редактором Typst
                  прямо над панелью вставила бы в шаблон номер строки (issue #518). */}
              <span className="text-fg4 cursor-grab shrink-0" draggable
                onDragStart={e => { e.dataTransfer.setData(DRAG_MIME, String(i)); setDragIdx(i); }}
                onDragEnd={() => setDragIdx(null)} title="Перетащить">
                <GripVertical size={13} />
              </span>
              <input value={p.name} onChange={e => patch(i, { name: e.target.value })} placeholder="имя"
                className="w-24 text-xs border border-stroke-strong rounded px-1.5 py-1 bg-surface text-fg1" />
              <input value={p.label} onChange={e => patch(i, { label: e.target.value })} placeholder="подпись"
                className="flex-1 min-w-0 text-xs border border-stroke-strong rounded px-1.5 py-1 bg-surface text-fg1" />
              <select value={p.type}
                onChange={e => patch(i, { type: e.target.value as TemplateParam['type'], default: e.target.value === 'boolean' ? false : e.target.value === 'number' ? 0 : '' })}
                className="text-xs border border-stroke-strong rounded px-1 py-1 bg-surface text-fg3">
                <option value="string">текст</option>
                <option value="number">число</option>
                <option value="boolean">да/нет</option>
              </select>
              <ParamDefault type={p.type} value={p.default} onChange={v => patch(i, { default: v })} />
              <span className="flex items-center gap-0.5 shrink-0">
                <button onClick={() => move(i, i - 1)} disabled={i === 0} title="Выше"
                  className="p-0.5 text-fg4 hover:text-fg2 disabled:opacity-25"><ArrowUp size={12} /></button>
                <button onClick={() => move(i, i + 1)} disabled={i === rows.length - 1} title="Ниже"
                  className="p-0.5 text-fg4 hover:text-fg2 disabled:opacity-25"><ArrowDown size={12} /></button>
                <button onClick={() => save(rows.filter((_, idx) => idx !== i))} title="Удалить параметр"
                  className="p-1 text-stroke-strong hover:text-danger"><Trash2 size={12} /></button>
              </span>
            </div>
          ))}
          <button onClick={() => save([...rows, { id: crypto.randomUUID(), p: { name: '', label: '', type: 'string', default: '' } }])}
            className="flex items-center gap-1 text-xs text-brand hover:text-brand-hover">
            <Plus size={12} /> Добавить параметр
          </button>
        </div>
      )}
    </div>
  );
}

/** Поле значения по умолчанию, зависит от типа параметра. */
function ParamDefault({ type, value, onChange }: {
  type: TemplateParam['type']; value: TemplateParam['default']; onChange: (v: TemplateParam['default']) => void;
}) {
  if (type === 'boolean')
    return (
      <span className="w-20 shrink-0 flex justify-center">
        <input type="checkbox" checked={value === true} onChange={e => onChange(e.target.checked)} title="Значение по умолчанию" />
      </span>
    );
  return (
    <input type={type === 'number' ? 'number' : 'text'} value={value == null ? '' : String(value)}
      onChange={e => onChange(type === 'number' ? Number(e.target.value) : e.target.value)} placeholder="по умолч."
      className="w-20 shrink-0 text-xs border border-stroke-strong rounded px-1.5 py-1 bg-surface text-fg1" />
  );
}
