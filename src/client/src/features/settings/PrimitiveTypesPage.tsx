import { useState } from 'react';
import { Plus, Pencil, Trash2 } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { DateInput } from '@/shared/ui/DateInput';
import {
  useListPrimitiveTypes,
  useCreatePrimitiveType,
  useUpdatePrimitiveType,
  useDeletePrimitiveType,
  buildPrimitiveTypeDto,
} from '@/shared/api/primitiveTypes';
import type { FieldConstraints, PrimitiveTypeDef } from '@/shared/api/types';

// ─── Base type options ────────────────────────────────────────────────────────

const BASE_TYPES = [
  { value: 'string' as const, label: 'Строка' },
  { value: 'number' as const, label: 'Число' },
  { value: 'date' as const, label: 'Дата' },
];

// ─── Constraint editor ────────────────────────────────────────────────────────

interface ConstraintEditorProps {
  baseType: 'string' | 'number' | 'date';
  constraints: FieldConstraints;
  onChange: (c: FieldConstraints) => void;
}

function ConstraintEditor({ baseType, constraints, onChange }: ConstraintEditorProps) {
  function set<K extends keyof FieldConstraints>(key: K, val: FieldConstraints[K]) {
    onChange({ ...constraints, [key]: val });
  }
  function unset(key: keyof FieldConstraints) {
    const next = { ...constraints };
    delete next[key];
    onChange(next);
  }

  if (baseType === 'string') {
    return (
      <div className="space-y-3">
        <div>
          <label className="block text-sm font-medium text-[--color-text-primary] mb-1">Шаблон (regex)</label>
          <input
            type="text"
            className="w-full px-3 py-2 rounded-lg border border-[--color-border-default] bg-[--color-bg-primary] text-sm font-mono"
            placeholder="например: ^[\w.]+@[\w]+\.\w+$"
            value={constraints.pattern ?? ''}
            onChange={e => e.target.value ? set('pattern', e.target.value) : unset('pattern')}
          />
        </div>
        <div>
          <label className="block text-sm font-medium text-[--color-text-primary] mb-1">Сообщение об ошибке</label>
          <input
            type="text"
            className="w-full px-3 py-2 rounded-lg border border-[--color-border-default] bg-[--color-bg-primary] text-sm"
            placeholder="например: Введите корректный email"
            value={constraints.patternMessage ?? ''}
            onChange={e => e.target.value ? set('patternMessage', e.target.value) : unset('patternMessage')}
          />
        </div>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className="block text-sm font-medium text-[--color-text-primary] mb-1">Мин. длина</label>
            <input
              type="number"
              min={0}
              className="w-full px-3 py-2 rounded-lg border border-[--color-border-default] bg-[--color-bg-primary] text-sm"
              value={constraints.minLength ?? ''}
              onChange={e => e.target.value ? set('minLength', Number(e.target.value)) : unset('minLength')}
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-[--color-text-primary] mb-1">Макс. длина</label>
            <input
              type="number"
              min={0}
              className="w-full px-3 py-2 rounded-lg border border-[--color-border-default] bg-[--color-bg-primary] text-sm"
              value={constraints.maxLength ?? ''}
              onChange={e => e.target.value ? set('maxLength', Number(e.target.value)) : unset('maxLength')}
            />
          </div>
        </div>
      </div>
    );
  }

  if (baseType === 'number') {
    return (
      <div className="space-y-3">
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className="block text-sm font-medium text-[--color-text-primary] mb-1">Минимум</label>
            <input
              type="number"
              className="w-full px-3 py-2 rounded-lg border border-[--color-border-default] bg-[--color-bg-primary] text-sm"
              value={constraints.min ?? ''}
              onChange={e => e.target.value ? set('min', Number(e.target.value)) : unset('min')}
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-[--color-text-primary] mb-1">Максимум</label>
            <input
              type="number"
              className="w-full px-3 py-2 rounded-lg border border-[--color-border-default] bg-[--color-bg-primary] text-sm"
              value={constraints.max ?? ''}
              onChange={e => e.target.value ? set('max', Number(e.target.value)) : unset('max')}
            />
          </div>
        </div>
        <label className="flex items-center gap-2 cursor-pointer">
          <input
            type="checkbox"
            className="w-4 h-4 rounded accent-[--color-brand-default]"
            checked={constraints.integer ?? false}
            onChange={e => e.target.checked ? set('integer', true) : unset('integer')}
          />
          <span className="text-sm text-[--color-text-primary]">Только целые числа</span>
        </label>
      </div>
    );
  }

  // date
  const dateCls = 'w-full px-3 py-2 rounded-lg border border-[--color-border-default] bg-[--color-bg-primary] text-sm';
  return (
    <div className="grid grid-cols-2 gap-3">
      <div>
        <label className="block text-sm font-medium text-[--color-text-primary] mb-1">Минимальная дата</label>
        <DateInput
          value={constraints.minDate ?? ''}
          onChange={v => v ? set('minDate', v) : unset('minDate')}
          className={dateCls}
        />
      </div>
      <div>
        <label className="block text-sm font-medium text-[--color-text-primary] mb-1">Максимальная дата</label>
        <DateInput
          value={constraints.maxDate ?? ''}
          onChange={v => v ? set('maxDate', v) : unset('maxDate')}
          className={dateCls}
        />
      </div>
    </div>
  );
}

// ─── Edit modal ───────────────────────────────────────────────────────────────

interface EditModalProps {
  initial?: PrimitiveTypeDef;
  onClose: () => void;
}

function EditModal({ initial, onClose }: EditModalProps) {
  const [name, setName] = useState(initial?.name ?? '');
  const [code, setCode] = useState(initial?.code ?? '');
  const [baseType, setBaseType] = useState<'string' | 'number' | 'date'>(initial?.baseType ?? 'string');
  const [description, setDescription] = useState(initial?.description ?? '');
  const [constraints, setConstraints] = useState<FieldConstraints>(initial?.constraints ?? {});
  const [error, setError] = useState('');

  const create = useCreatePrimitiveType();
  const update = useUpdatePrimitiveType(initial?.id ?? '');

  function handleBaseTypeChange(bt: 'string' | 'number' | 'date') {
    setBaseType(bt);
    setConstraints({});
  }

  async function handleSave() {
    if (!name.trim()) { setError('Укажите название'); return; }
    if (!code.trim()) { setError('Укажите код'); return; }
    if (!/^[a-zA-Z][a-zA-Z0-9_]*$/.test(code.trim())) {
      setError('Код: только латинские буквы, цифры и _'); return;
    }
    setError('');
    const dto = buildPrimitiveTypeDto(name.trim(), code.trim(), baseType, description.trim() || undefined, constraints);
    try {
      if (initial) await update.mutateAsync(dto);
      else await create.mutateAsync(dto);
      onClose();
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Ошибка сохранения');
    }
  }

  const isPending = create.isPending || update.isPending;

  return (
    <Modal open onOpenChange={open => { if (!open) onClose(); }} title={initial ? 'Редактировать тип' : 'Новый тип поля'}>
      <div className="space-y-4 min-w-[480px]">
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className="block text-sm font-medium text-[--color-text-primary] mb-1">Название</label>
            <input
              type="text"
              className="w-full px-3 py-2 rounded-lg border border-[--color-border-default] bg-[--color-bg-primary] text-sm"
              placeholder="Email"
              value={name}
              onChange={e => setName(e.target.value)}
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-[--color-text-primary] mb-1">Код</label>
            <input
              type="text"
              className="w-full px-3 py-2 rounded-lg border border-[--color-border-default] bg-[--color-bg-primary] text-sm font-mono"
              placeholder="email"
              value={code}
              onChange={e => setCode(e.target.value)}
              disabled={!!initial}
            />
          </div>
        </div>

        <div>
          <label className="block text-sm font-medium text-[--color-text-primary] mb-1">Базовый тип</label>
          <div className="flex gap-2">
            {BASE_TYPES.map(bt => (
              <button
                key={bt.value}
                type="button"
                onClick={() => handleBaseTypeChange(bt.value)}
                disabled={!!initial}
                className={`px-4 py-2 rounded-lg text-sm font-medium transition-colors ${
                  baseType === bt.value
                    ? 'bg-[--color-brand-default] text-white'
                    : 'bg-[--color-bg-secondary] text-[--color-text-secondary] hover:bg-[--color-bg-tertiary]'
                } disabled:opacity-50`}
              >
                {bt.label}
              </button>
            ))}
          </div>
        </div>

        <div>
          <label className="block text-sm font-medium text-[--color-text-primary] mb-1">Описание</label>
          <input
            type="text"
            className="w-full px-3 py-2 rounded-lg border border-[--color-border-default] bg-[--color-bg-primary] text-sm"
            placeholder="Необязательное описание типа"
            value={description}
            onChange={e => setDescription(e.target.value)}
          />
        </div>

        <div className="border-t border-[--color-border-subtle] pt-3">
          <p className="text-sm font-medium text-[--color-text-primary] mb-3">Ограничения</p>
          <ConstraintEditor baseType={baseType} constraints={constraints} onChange={setConstraints} />
        </div>

        {error && <p className="text-sm text-[--color-error-text]">{error}</p>}

        <div className="flex justify-end gap-2 pt-2">
          <button
            type="button"
            onClick={onClose}
            className="px-4 py-2 rounded-lg text-sm font-medium bg-[--color-bg-secondary] text-[--color-text-secondary] hover:bg-[--color-bg-tertiary]"
          >
            Отмена
          </button>
          <button
            type="button"
            onClick={handleSave}
            disabled={isPending}
            className="px-4 py-2 rounded-lg text-sm font-medium bg-[--color-brand-default] text-white hover:bg-[--color-brand-hover] disabled:opacity-50"
          >
            {isPending ? 'Сохранение…' : 'Сохранить'}
          </button>
        </div>
      </div>
    </Modal>
  );
}

// ─── Constraint summary ───────────────────────────────────────────────────────

function ConstraintSummary({ baseType, c }: { baseType: string; c: FieldConstraints }) {
  const parts: string[] = [];
  if (baseType === 'string') {
    if (c.pattern) parts.push(`pattern: ${c.pattern}`);
    if (c.minLength != null) parts.push(`мин: ${c.minLength}`);
    if (c.maxLength != null) parts.push(`макс: ${c.maxLength}`);
  } else if (baseType === 'number') {
    if (c.min != null) parts.push(`≥ ${c.min}`);
    if (c.max != null) parts.push(`≤ ${c.max}`);
    if (c.integer) parts.push('целое');
  } else if (baseType === 'date') {
    if (c.minDate) parts.push(`от ${c.minDate}`);
    if (c.maxDate) parts.push(`до ${c.maxDate}`);
  }
  if (parts.length === 0) return <span className="text-[--color-text-tertiary] italic">нет ограничений</span>;
  return <span>{parts.join(', ')}</span>;
}

// ─── Main page ────────────────────────────────────────────────────────────────

export function PrimitiveTypesPage() {
  const { data: types = [], isLoading } = useListPrimitiveTypes();
  const deleteMut = useDeletePrimitiveType();
  const [editing, setEditing] = useState<PrimitiveTypeDef | null | 'new'>(null);
  const [confirmDelete, setConfirmDelete] = useState<PrimitiveTypeDef | null>(null);

  const baseTypeLabel: Record<string, string> = { string: 'Строка', number: 'Число', date: 'Дата' };

  return (
    <div className="p-6 max-w-4xl mx-auto">
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-semibold text-[--color-text-primary]">Типы полей</h1>
          <p className="text-sm text-[--color-text-secondary] mt-1">
            Пользовательские типы реквизитов на основе строки, числа или даты с ограничениями
          </p>
        </div>
        <button
          type="button"
          onClick={() => setEditing('new')}
          className="flex items-center gap-2 px-4 py-2 rounded-lg text-sm font-medium bg-[--color-brand-default] text-white hover:bg-[--color-brand-hover]"
        >
          <Plus size={16} />
          Добавить тип
        </button>
      </div>

      {isLoading ? (
        <p className="text-[--color-text-secondary]">Загрузка…</p>
      ) : types.length === 0 ? (
        <div className="text-center py-16 text-[--color-text-tertiary]">
          <p>Пользовательских типов ещё нет.</p>
          <p className="text-sm mt-1">Создайте тип, например «Email» или «ИНН».</p>
        </div>
      ) : (
        <div className="border border-[--color-border-default] rounded-xl overflow-hidden">
          <table className="w-full text-sm">
            <thead>
              <tr className="bg-[--color-bg-secondary] border-b border-[--color-border-subtle]">
                <th className="text-left px-4 py-3 font-medium text-[--color-text-secondary]">Название</th>
                <th className="text-left px-4 py-3 font-medium text-[--color-text-secondary]">Код</th>
                <th className="text-left px-4 py-3 font-medium text-[--color-text-secondary]">Базовый тип</th>
                <th className="text-left px-4 py-3 font-medium text-[--color-text-secondary]">Ограничения</th>
                <th className="px-4 py-3" />
              </tr>
            </thead>
            <tbody className="divide-y divide-[--color-border-subtle]">
              {[...types].sort((a, b) => a.name.localeCompare(b.name, 'ru')).map(t => (
                <tr key={t.id} className="hover:bg-[--color-bg-secondary] transition-colors">
                  <td className="px-4 py-3 font-medium text-[--color-text-primary]">
                    {t.name}
                    {t.description && (
                      <p className="text-xs text-[--color-text-tertiary] font-normal">{t.description}</p>
                    )}
                  </td>
                  <td className="px-4 py-3 font-mono text-[--color-text-secondary]">{t.code}</td>
                  <td className="px-4 py-3 text-[--color-text-secondary]">{baseTypeLabel[t.baseType] ?? t.baseType}</td>
                  <td className="px-4 py-3 text-[--color-text-secondary] text-xs">
                    <ConstraintSummary baseType={t.baseType} c={t.constraints} />
                  </td>
                  <td className="px-4 py-3">
                    <div className="flex items-center justify-end gap-1">
                      <button
                        type="button"
                        onClick={() => setEditing(t)}
                        className="p-2 rounded-lg hover:bg-[--color-bg-tertiary] text-[--color-text-secondary]"
                      >
                        <Pencil size={15} />
                      </button>
                      <button
                        type="button"
                        onClick={() => setConfirmDelete(t)}
                        className="p-2 rounded-lg hover:bg-[--color-bg-tertiary] text-[--color-error-text]"
                      >
                        <Trash2 size={15} />
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {editing && (
        <EditModal
          initial={editing === 'new' ? undefined : editing}
          onClose={() => setEditing(null)}
        />
      )}

      {confirmDelete && (
        <Modal open onOpenChange={o => { if (!o) setConfirmDelete(null); }} title="Удалить тип поля">
          <div className="space-y-4 min-w-[360px]">
            <p className="text-sm text-[--color-text-secondary]">
              Тип <span className="font-semibold text-[--color-text-primary]">«{confirmDelete.name}»</span> будет удалён.
              Поля документов, использующие этот тип, перестанут валидироваться.
            </p>
            <div className="flex justify-end gap-2">
              <button
                type="button"
                onClick={() => setConfirmDelete(null)}
                className="px-4 py-2 rounded-lg text-sm font-medium bg-[--color-bg-secondary] text-[--color-text-secondary] hover:bg-[--color-bg-tertiary]"
              >
                Отмена
              </button>
              <button
                type="button"
                disabled={deleteMut.isPending}
                onClick={() => deleteMut.mutateAsync(confirmDelete.id).then(() => setConfirmDelete(null))}
                className="px-4 py-2 rounded-lg text-sm font-medium bg-[--color-error-default] text-white hover:bg-[--color-error-hover] disabled:opacity-50"
              >
                Удалить
              </button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}
