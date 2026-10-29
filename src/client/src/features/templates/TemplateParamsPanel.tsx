import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Add, Delete, DragIndicator, KeyboardArrowDown, KeyboardArrowUp, Tune } from '@/shared/ui/icons';
import { MoveButtons } from '@/shared/ui/MoveButtons';
import { useUpdateTemplateParameters } from '@/shared/api/templates';
import { moveItem } from '@/shared/utils/moveItem';
import type { Template, TemplateParam } from '@/shared/api/types';
import { newLocalId } from '@/shared/utils/localId';
import { parseTemplateParams } from './templateParams';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Tooltip } from '@/shared/ui/Tooltip';

/** Строка редактора: параметр плюс устойчивый идентификатор — он нужен ключом списка при перестановке. */
type Row = { id: string; p: TemplateParam };

/** Свой тип перетаскиваемых данных: текстовый сделал бы ручку источником текста для всей страницы. */
const DRAG_MIME = 'application/x-crg-template-param';

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
    () => parseTemplateParams(template.parameters).map(p => ({ id: newLocalId(), p })));
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
    <div className="border-t border-outline-variant bg-surface">
      <button onClick={() => setOpen(v => !v)} aria-expanded={open}
        className="w-full flex items-center gap-2 px-4 py-2.5 state-layer text-left">
        <Icon icon={Tune} size={18} className="text-on-surface-variant" />
        <span className="text-xs font-medium text-on-surface-variant flex-1">Параметры шаблона{rows.length > 0 ? ` (${rows.length})` : ''}</span>
        {open ? <Icon icon={KeyboardArrowUp} size={18} className="text-on-surface-variant" /> : <Icon icon={KeyboardArrowDown} size={18} className="text-on-surface-variant" />}
      </button>
      {open && (
        <div className="px-4 pb-4 pt-3 border-t border-surface-container space-y-2">
          <p className="text-caption text-on-surface-variant">
            Доступны в Typst как <code className="text-on-surface-variant">data.params.имя</code>. Значение по умолчанию можно
            переопределить на конкретном документе.
          </p>
          {rows.map(({ id, p }, i) => (
            <div key={id}
              className={`flex items-center gap-1.5 rounded-xs ${dragIdx === i ? 'ring-1 ring-primary' : ''}`}
              onDragOver={dragIdx !== null ? e => e.preventDefault() : undefined}
              onDrop={dragIdx !== null ? e => { e.preventDefault(); move(dragIdx, i); setDragIdx(null); } : undefined}>
              {/* Перетаскивание и стрелки вместе — как в списке вариантов перечисления: мышью
                  быстрее через несколько строк, стрелками доступно с клавиатуры.

                  Тип груза СВОЙ, не text/plain: с текстовым типом ручка становится источником
                  перетаскивания для всей страницы, и отпущенная над редактором Typst прямо над
                  панелью вставила бы в шаблон номер строки (issue #518).

                  Сам груз — страховка по спецификации, а не требование Firefox: утверждение «без
                  setData Firefox отменит перетаскивание» (шло из #517) проверено вручную
                  2026-08-17 и не подтвердилось, см. ROW_DRAG_MIME в document-sets/fields. */}
              <Tooltip content="Перетащить">
                <span className="text-on-surface-variant cursor-grab shrink-0" draggable
                  onDragStart={e => { e.dataTransfer.setData(DRAG_MIME, String(i)); setDragIdx(i); }}
                  onDragEnd={() => setDragIdx(null)}>
                  <Icon icon={DragIndicator} size={18} />
                </span>
              </Tooltip>
              <input value={p.name} onChange={e => patch(i, { name: e.target.value })} placeholder="имя"
                className="w-24 text-xs border border-outline rounded-xs px-1.5 py-1 bg-surface text-on-surface" />
              <input value={p.label} onChange={e => patch(i, { label: e.target.value })} placeholder="подпись"
                className="flex-1 min-w-0 text-xs border border-outline rounded-xs px-1.5 py-1 bg-surface text-on-surface" />
              <Select size="sm" className="w-auto" aria-label={`Тип параметра ${p.name || i + 1}`} value={p.type}
                onValueChange={type => patch(i, { type: type as TemplateParam['type'], default: type === 'boolean' ? false : type === 'number' ? 0 : '' })}>
                <SelectItem value="string">текст</SelectItem>
                <SelectItem value="number">число</SelectItem>
                <SelectItem value="boolean">да/нет</SelectItem>
              </Select>
              <ParamDefault type={p.type} value={p.default} onChange={v => patch(i, { default: v })} />
              <span className="flex items-center gap-0.5 shrink-0">
                <MoveButtons onUp={() => move(i, i - 1)} onDown={() => move(i, i + 1)}
                  isFirst={i === 0} isLast={i === rows.length - 1} />
                <Tooltip content="Удалить параметр" name>
                  <button onClick={() => save(rows.filter((_, idx) => idx !== i))}
                    className="p-1 text-on-surface-variant hover:text-error"><Icon icon={Delete} size={18} /></button>
                </Tooltip>
              </span>
            </div>
          ))}
          <button onClick={() => save([...rows, { id: newLocalId(), p: { name: '', label: '', type: 'string', default: '' } }])}
            className="flex items-center gap-1 text-xs text-primary state-layer">
            <Icon icon={Add} size={18} /> Добавить параметр
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
        <Tooltip content="Значение по умолчанию"><input type="checkbox" checked={value === true} onChange={e => onChange(e.target.checked)} /></Tooltip>
      </span>
    );
  return (
    <input type={type === 'number' ? 'number' : 'text'} value={value == null ? '' : String(value)}
      onChange={e => onChange(type === 'number' ? Number(e.target.value) : e.target.value)} placeholder="по умолч."
      className="w-20 shrink-0 text-xs border border-outline rounded-xs px-1.5 py-1 bg-surface text-on-surface" />
  );
}
