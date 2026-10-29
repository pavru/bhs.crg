import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Add, Delete, DragIndicator } from '@/shared/ui/icons';
import { MoveButtons } from '@/shared/ui/MoveButtons';
import { Button, IconButton } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { useCreateEnumType, buildEnumTypeDto } from '@/shared/api/enumTypes';
import { moveItem } from '@/shared/utils/moveItem';
import { rowKey, withRowUid } from '@/shared/utils/rowIdentity';
import type { EnumOptionDef, EnumTypeDef } from '@/shared/api/types';
import { nextAutoKey } from './schemaConstants';
import { Tooltip } from '@/shared/ui/Tooltip';

// Реестр перечислений (issue #59) переведён в list-detail (issue #210) — редактор живёт в
// PrimitiveTypesPage (EnumTypeDetail). Здесь остаются переиспользуемые куски: редактор вариантов
// и форма создания (в модалке).

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
      <div className={`${cols} text-body-small text-on-surface-variant px-0.5`}>
        <span />
        <span>Код</span>
        <span>Отображаемое имя</span>
        <span />
      </div>
      {values.map((v, i) => (
        <div key={rowKey(v, i)} className={`${cols} rounded-xs ${dragIdx === i ? 'ring-1 ring-primary' : ''}`}
          onDragOver={dragIdx !== null ? e => e.preventDefault() : undefined}
          onDrop={dragIdx !== null ? e => { e.preventDefault(); move(dragIdx, i); setDragIdx(null); } : undefined}>
          <Tooltip content="Перетащить">
            <span className="flex justify-center text-on-surface-variant cursor-grab" draggable
              /* Заглушка в dataTransfer — страховка по спецификации, а не требование Firefox:
                 утверждение «пустой payload отменяет перетаскивание» проверено вручную 2026-08-17 и
                 не подтвердилось (см. ROW_DRAG_MIME в document-sets/fields).
                 Тип здесь text/plain, а не свой, как требует #518, — но значение ПУСТОЕ, и ронять в
                 соседние поля «Код»/«Название» нечего. Ставить свой тип всё равно правильнее. */
              onDragStart={e => { e.dataTransfer.setData('text/plain', ''); setDragIdx(i); }}
              onDragEnd={() => setDragIdx(null)}>
              <Icon icon={DragIndicator} size={18} />
            </span>
          </Tooltip>
          <input value={v.code} onChange={e => update(i, { code: e.target.value })} placeholder="APPROVED"
            className="border border-outline rounded-xs px-2 py-1.5 text-body-medium font-mono focus:outline-none focus-visible:ring-1 focus-visible:ring-primary bg-surface" />
          <input value={v.label} onChange={e => update(i, { label: e.target.value })} placeholder="Согласован"
            className="border border-outline rounded-xs px-2 py-1.5 text-body-medium focus:outline-none focus-visible:ring-1 focus-visible:ring-primary bg-surface" />
          <span className="flex items-center gap-0.5">
            <MoveButtons onUp={() => move(i, i - 1)} onDown={() => move(i, i + 1)}
              isFirst={i === 0} isLast={i === values.length - 1} />
            <IconButton label="Удалить" size="sm" danger onClick={() => remove(i)}>
              <Icon icon={Delete} size={18} />
            </IconButton>
          </span>
        </div>
      ))}
      <button type="button"
        onClick={() => onChange([...values, withRowUid({ code: '', label: '' })])}
        className="flex items-center gap-1 text-body-medium text-primary state-layer pt-0.5">
        <Icon icon={Add} size={18} /> Добавить вариант
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
      <div className="border-t border-outline-variant pt-3">
        <p className="text-body-medium font-medium text-on-surface mb-3">Варианты</p>
        <ValuesEditor values={values} onChange={setValues} />
      </div>
      {error && <p className="text-body-medium text-error">{error}</p>}
      <div className="flex justify-end gap-2 border-t border-outline-variant pt-3">
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
