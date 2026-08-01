import { useState } from 'react';
import { Plus, Trash2, GripVertical, ArrowUp, ArrowDown } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { useCreateEnumType, buildEnumTypeDto } from '@/shared/api/enumTypes';
import { moveItem } from '@/shared/utils/moveItem';
import { rowKey, withRowUid } from '@/shared/utils/rowIdentity';
import type { EnumOptionDef, EnumTypeDef } from '@/shared/api/types';
import { nextAutoKey } from './schemaConstants';

// Реестр перечислений (issue #59) переведён в list-detail (issue #210) — редактор живёт в
// PrimitiveTypesPage (EnumTypeDetail). Здесь остаются переиспользуемые куски: редактор вариантов
// и форма создания (в модалке).

/** Человекочитаемое превью вариантов перечисления для строки списка. */
export function humanEnumPreview(values: EnumOptionDef[]): string {
  if (values.length === 0) return 'нет вариантов';
  const labels = values.map(v => v.label).filter(Boolean);
  const head = labels.slice(0, 3).join(', ');
  return labels.length > 3 ? `${head} … (+${labels.length - 3})` : head;
}

// ─── Values editor (список код|имя) ────────────────────────────────────────────

export function ValuesEditor({ values, onChange }: { values: EnumOptionDef[]; onChange: (v: EnumOptionDef[]) => void }) {
  const [dragIdx, setDragIdx] = useState<number | null>(null);
  function update(i: number, patch: Partial<EnumOptionDef>) {
    onChange(values.map((v, vi) => vi === i ? { ...v, ...patch } : v));
  }
  function remove(i: number) {
    onChange(values.filter((_, vi) => vi !== i));
  }
  // Порядок вариантов = порядок в выпадающем списке документа (хранится порядком массива).
  function move(from: number, to: number) {
    const next = moveItem(values, from, to);
    if (next !== values) onChange(next);
  }
  const cols = 'grid grid-cols-[20px_1fr_1.4fr_auto] gap-1.5 items-center';
  return (
    <div className="space-y-1.5">
      <div className={`${cols} text-xs text-fg4 px-0.5`}>
        <span />
        <span>Код</span>
        <span>Отображаемое имя</span>
        <span />
      </div>
      {values.map((v, i) => (
        <div key={rowKey(v, i)} className={`${cols} rounded ${dragIdx === i ? 'ring-1 ring-brand' : ''}`}
          onDragOver={dragIdx !== null ? e => e.preventDefault() : undefined}
          onDrop={dragIdx !== null ? e => { e.preventDefault(); move(dragIdx, i); setDragIdx(null); } : undefined}>
          <span className="flex justify-center text-fg4 cursor-grab" draggable
            /* Пустой payload Firefox считает отсутствием перетаскивания и отменяет его — кладём
               заглушку (та же ловушка описана в TemplateParamsPanel). */
            onDragStart={e => { e.dataTransfer.setData('text/plain', ''); setDragIdx(i); }}
            onDragEnd={() => setDragIdx(null)} title="Перетащить">
            <GripVertical size={14} />
          </span>
          <input value={v.code} onChange={e => update(i, { code: e.target.value })} placeholder="APPROVED"
            className="border border-stroke-strong rounded px-2 py-1.5 text-sm font-mono focus:outline-none focus-visible:ring-1 focus-visible:ring-brand bg-surface" />
          <input value={v.label} onChange={e => update(i, { label: e.target.value })} placeholder="Согласован"
            className="border border-stroke-strong rounded px-2 py-1.5 text-sm focus:outline-none focus-visible:ring-1 focus-visible:ring-brand bg-surface" />
          <span className="flex items-center gap-0.5">
            {/* aria-disabled, а не disabled (issue #517): браузер снимает фокус с кнопки, которая
                стала disabled, и пользователь, доведя строку клавиатурой до края, терял место —
                последний шаг ровно того сценария, ради которого всё это. */}
            <button type="button" onClick={() => { if (i > 0) move(i, i - 1); }} aria-disabled={i === 0}
              className="p-0.5 text-fg4 hover:text-fg2 aria-disabled:opacity-25 aria-disabled:hover:text-fg4"
              title="Выше"><ArrowUp size={12} /></button>
            <button type="button" onClick={() => { if (i < values.length - 1) move(i, i + 1); }}
              aria-disabled={i === values.length - 1}
              className="p-0.5 text-fg4 hover:text-fg2 aria-disabled:opacity-25 aria-disabled:hover:text-fg4"
              title="Ниже"><ArrowDown size={12} /></button>
            <button type="button" onClick={() => remove(i)} className="p-0.5 text-fg4 hover:text-danger" title="Удалить"><Trash2 size={13} /></button>
          </span>
        </div>
      ))}
      <button type="button"
        onClick={() => onChange([...values, withRowUid({ code: '', label: '' })])}
        className="flex items-center gap-1 text-sm text-brand hover:text-brand-hover pt-0.5">
        <Plus size={13} /> Добавить вариант
      </button>
    </div>
  );
}

// ─── Enum create form (в модалке «Новый тип перечисления») ─────────────────────

export function EnumForm({ onSaved, onCancel }: { onSaved: () => void; onCancel: () => void }) {
  const [name, setName] = useState('');
  const [code, setCode] = useState('');
  const [description, setDescription] = useState('');
  const [values, setValues] = useState<EnumOptionDef[]>([]);
  const [error, setError] = useState('');
  const create = useCreateEnumType();

  function handleNameChange(v: string) {
    setCode(nextAutoKey(code, name, v, true)); // форма создания — enum-тип всегда новый (issue #355)
    setName(v);
  }

  async function handleSave() {
    if (!name.trim()) { setError('Укажите название'); return; }
    if (!code.trim()) { setError('Укажите код'); return; }
    const cleaned = values.map(v => ({ code: v.code.trim(), label: v.label.trim() })).filter(v => v.code && v.label);
    if (cleaned.length === 0) { setError('Добавьте хотя бы один вариант'); return; }
    const codes = new Set<string>();
    for (const v of cleaned) {
      if (codes.has(v.code)) { setError(`Код «${v.code}» повторяется`); return; }
      codes.add(v.code);
    }
    setError('');
    try {
      await create.mutateAsync(buildEnumTypeDto(name.trim(), code.trim(), description.trim() || undefined, cleaned));
      onSaved();
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Ошибка сохранения');
    }
  }

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-3">
        <TextField label="Название" value={name} onChange={e => handleNameChange(e.target.value)} />
        <TextField label="Код" value={code} onChange={e => setCode(e.target.value)} className="font-mono" />
      </div>
      <TextField label="Описание" value={description} onChange={e => setDescription(e.target.value)} />
      <div className="border-t border-stroke pt-3">
        <p className="text-sm font-medium text-fg1 mb-3">Варианты</p>
        <ValuesEditor values={values} onChange={setValues} />
      </div>
      {error && <p className="text-sm text-danger">{error}</p>}
      <div className="flex justify-end gap-2 border-t border-stroke pt-3">
        <Button type="button" variant="text" onClick={onCancel}>Отмена</Button>
        <Button type="button" variant="filled" onClick={handleSave} loading={create.isPending}>
          {create.isPending ? 'Сохранение…' : 'Сохранить'}
        </Button>
      </div>
    </div>
  );
}

// Экспорт типа для удобства импортеров.
export type { EnumTypeDef };
