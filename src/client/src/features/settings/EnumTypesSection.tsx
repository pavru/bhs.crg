import { useState } from 'react';
import { Plus, Trash2, ChevronDown, ChevronUp } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button, IconButton } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import {
  useListEnumTypes,
  useCreateEnumType,
  useUpdateEnumType,
  useDeleteEnumType,
  useSetEnumTypeGroup,
  buildEnumTypeDto,
} from '@/shared/api/enumTypes';
import type { EnumOptionDef, EnumTypeDef } from '@/shared/api/types';
import { TypeGroupAccordion, GroupPicker } from './TypeGroupAccordion';
import { toCamelKey } from './schemaConstants';

// ─── Values editor (список код|имя) ────────────────────────────────────────────

function ValuesEditor({ values, onChange }: { values: EnumOptionDef[]; onChange: (v: EnumOptionDef[]) => void }) {
  function update(i: number, patch: Partial<EnumOptionDef>) {
    onChange(values.map((v, vi) => vi === i ? { ...v, ...patch } : v));
  }
  function remove(i: number) {
    onChange(values.filter((_, vi) => vi !== i));
  }
  return (
    <div className="space-y-1.5">
      <div className="grid grid-cols-[1fr_1fr_auto] gap-1.5 text-xs text-fg4 px-0.5">
        <span>Код</span>
        <span>Отображаемое имя</span>
        <span />
      </div>
      {values.map((v, i) => (
        <div key={i} className="grid grid-cols-[1fr_1fr_auto] gap-1.5 items-center">
          <input
            value={v.code}
            onChange={e => update(i, { code: e.target.value })}
            placeholder="APPROVED"
            className="border border-stroke-strong rounded px-2 py-1 text-xs font-mono focus:outline-none focus-visible:ring-1 focus-visible:ring-brand bg-surface"
          />
          <input
            value={v.label}
            onChange={e => update(i, { label: e.target.value })}
            placeholder="Согласован"
            className="border border-stroke-strong rounded px-2 py-1 text-xs focus:outline-none focus-visible:ring-1 focus-visible:ring-brand bg-surface"
          />
          <button type="button" onClick={() => remove(i)} className="p-0.5 text-fg4 hover:text-danger">
            <Trash2 size={12} />
          </button>
        </div>
      ))}
      <button type="button"
        onClick={() => onChange([...values, { code: '', label: '' }])}
        className="flex items-center gap-1 text-xs text-brand hover:text-brand-hover">
        <Plus size={11} /> Добавить вариант
      </button>
    </div>
  );
}

// ─── Enum form (инлайн для редактирования строки, в модалке для создания) ─────

function EnumForm({ initial, onSaved, onCancel }: {
  initial?: EnumTypeDef; onSaved: () => void; onCancel: () => void;
}) {
  const [name, setName] = useState(initial?.name ?? '');
  const [code, setCode] = useState(initial?.code ?? '');
  const [description, setDescription] = useState(initial?.description ?? '');
  const [values, setValues] = useState<EnumOptionDef[]>(initial?.values ?? []);
  const [error, setError] = useState('');

  const create = useCreateEnumType();
  const update = useUpdateEnumType(initial?.id ?? '');

  // См. DocumentTypesPage.handleNameChange/FieldBuilder.updateTitle — тот же принцип: код
  // перегенерируется из названия, пока совпадает с авто-значением (или пуст); ручная правка
  // кода отключает автогенерацию. Код не ограничен латиницей — тот же toCamelKey уже даёт
  // кириллические PascalCase-коды для DocumentType/ключей полей, никакого спец. формата не нужно.
  function handleNameChange(v: string) {
    const isCodeAuto = !code.trim() || code === toCamelKey(name);
    setName(v);
    if (isCodeAuto) setCode(toCamelKey(v));
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
    const dto = buildEnumTypeDto(name.trim(), code.trim(), description.trim() || undefined, cleaned);
    try {
      if (initial) await update.mutateAsync(dto);
      else await create.mutateAsync(dto);
      onSaved();
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Ошибка сохранения');
    }
  }

  const isPending = create.isPending || update.isPending;

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-3">
        <TextField label="Название" value={name} onChange={e => handleNameChange(e.target.value)}
          hint="Статус документа" />
        <TextField label="Код" value={code} onChange={e => setCode(e.target.value)}
          disabled={!!initial} className="font-mono" hint="status" />
      </div>

      <TextField label="Описание" value={description} onChange={e => setDescription(e.target.value)}
        hint="Необязательное описание типа" />

      <div className="border-t border-stroke pt-3">
        <p className="text-sm font-medium text-fg1 mb-3">Варианты</p>
        <ValuesEditor values={values} onChange={setValues} />
      </div>

      {error && <p className="text-sm text-danger">{error}</p>}

      <div className="flex justify-end gap-2 border-t border-stroke pt-3">
        <Button type="button" variant="text" onClick={onCancel}>Отмена</Button>
        <Button type="button" variant="filled" onClick={handleSave} loading={isPending}>
          {isPending ? 'Сохранение…' : 'Сохранить'}
        </Button>
      </div>
    </div>
  );
}

// ─── Row ──────────────────────────────────────────────────────────────────────

function EnumTypeRow({ type, allGroups, expanded, onToggle }: {
  type: EnumTypeDef; allGroups: string[]; expanded: boolean; onToggle: () => void;
}) {
  const deleteMutation = useDeleteEnumType();
  const groupMutation = useSetEnumTypeGroup();
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [deleteError, setDeleteError] = useState('');

  function handleDelete(e: React.MouseEvent) {
    e.stopPropagation();
    setDeleteError('');
    setConfirmDelete(true);
  }

  return (
    <div className={`overflow-hidden group ${expanded ? 'bg-base' : ''}`}>
      <div className="flex items-center hover:bg-base transition-colors">
        <button type="button" onClick={onToggle} aria-expanded={expanded}
          className="flex-1 min-w-0 flex items-center gap-2 px-4 py-2.5 text-left">
          {expanded
            ? <ChevronUp size={15} className="text-fg4 shrink-0" />
            : <ChevronDown size={15} className="text-fg4 shrink-0" />}
          <span className="text-sm font-medium text-fg1 shrink-0">{type.name}</span>
          <span className="text-xs text-fg4 font-mono shrink-0">{type.code}</span>
          <span className="flex-1" />
          <span className="text-xs text-fg4 shrink-0 truncate max-w-[280px]">
            {type.values.length === 0
              ? <span className="italic">нет вариантов</span>
              : type.values.map(v => v.label).join(', ')}
          </span>
        </button>
        <span className="pr-1" onClick={e => e.stopPropagation()}>
          <GroupPicker groups={allGroups} value={type.group}
            onChange={group => groupMutation.mutate({ id: type.id, group })} />
        </span>
        <IconButton label="Удалить" size="sm" danger onClick={handleDelete} disabled={deleteMutation.isPending}
          className="opacity-0 group-hover:opacity-100 group-focus-within:opacity-100">
          <Trash2 size={14} />
        </IconButton>
      </div>
      {expanded && (
        <div className="px-4 pb-5 pt-3 border-t border-stroke bg-base">
          <EnumForm initial={type} onSaved={onToggle} onCancel={onToggle} />
        </div>
      )}
      {confirmDelete && (
        <Modal open onOpenChange={o => { if (!o) setConfirmDelete(false); }} title="Удалить тип перечисления"
          footer={
            <div className="flex justify-end gap-2">
              <button
                type="button"
                onClick={() => setConfirmDelete(false)}
                className="px-4 py-2 rounded-lg text-sm font-medium bg-base text-fg2 hover:bg-muted"
              >
                Отмена
              </button>
              <button
                type="button"
                disabled={deleteMutation.isPending}
                onClick={() => deleteMutation.mutateAsync(type.id)
                  .then(() => setConfirmDelete(false))
                  .catch((e: unknown) => {
                    const err = e as { response?: { data?: { error?: string } }; message?: string };
                    setDeleteError(err?.response?.data?.error || err?.message || 'Не удалось удалить тип.');
                  })}
                className="px-4 py-2 rounded-lg text-sm font-medium bg-danger text-white hover:bg-danger disabled:opacity-50"
              >
                Удалить
              </button>
            </div>
          }>
          <div className="space-y-4 min-w-[360px]">
            <p className="text-sm text-fg2">
              Тип <span className="font-semibold text-fg1">«{type.name}»</span> будет удалён.
              Поля документов, использующие этот тип, перестанут резолвить варианты.
            </p>
            {deleteError && <p className="text-sm text-danger">{deleteError}</p>}
          </div>
        </Modal>
      )}
    </div>
  );
}

// ─── Section ──────────────────────────────────────────────────────────────────

export function EnumTypesSection() {
  const { data: types = [], isLoading } = useListEnumTypes();
  const [createOpen, setCreateOpen] = useState(false);
  const [expandedId, setExpandedId] = useState<string | null>(null);

  const sorted = [...types].sort((a, b) => a.name.localeCompare(b.name, 'ru'));
  const allGroups = [...new Set(sorted.map(t => t.group).filter((g): g is string => !!g))]
    .sort((a, b) => a.localeCompare(b, 'ru'));

  function toggleExpanded(id: string) {
    setExpandedId(prev => prev === id ? null : id);
  }

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <p className="text-xs text-fg3">
          Переиспользуемые списки вариантов (код + отображаемое имя) для полей типа «Перечисление»
        </p>
        <Button variant="filled" icon={<Plus size={16} />} onClick={() => setCreateOpen(true)}>
          Добавить тип
        </Button>
      </div>

      {isLoading ? (
        <div className="text-center text-fg4 text-sm py-10">Загрузка...</div>
      ) : types.length === 0 ? (
        <div className="text-center text-fg4 text-sm py-10">
          Типов перечисления ещё нет. Создайте тип, например «Статус документа».
        </div>
      ) : (
        <TypeGroupAccordion items={sorted} getGroup={t => t.group} renderItem={t => (
          <EnumTypeRow key={t.id} type={t} allGroups={allGroups}
            expanded={expandedId === t.id} onToggle={() => toggleExpanded(t.id)} />
        )} />
      )}

      <Modal open={createOpen} onOpenChange={setCreateOpen} title="Новый тип перечисления">
        {createOpen && (
          <EnumForm onSaved={() => setCreateOpen(false)} onCancel={() => setCreateOpen(false)} />
        )}
      </Modal>
    </div>
  );
}
