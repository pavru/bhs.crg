import { useState } from 'react';
import { Plus, Trash2, ChevronDown, ChevronUp } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button, IconButton } from '@/shared/ui/Button';
import { DateInput } from '@/shared/ui/DateInput';
import {
  useListPrimitiveTypes,
  useCreatePrimitiveType,
  useUpdatePrimitiveType,
  useDeletePrimitiveType,
  useSetPrimitiveTypeGroup,
  buildPrimitiveTypeDto,
} from '@/shared/api/primitiveTypes';
import type { FieldConstraints, PrimitiveTypeDef, DatePrecision } from '@/shared/api/types';
import { formatDateRu } from '@/shared/utils/date';
import { useTagRegistry, fieldTags } from '@/shared/api/tags';
import { TypeGroupAccordion, GroupPicker } from './TypeGroupAccordion';
import { EnumTypesSection } from './EnumTypesSection';

// ─── Base type options ────────────────────────────────────────────────────────

const BASE_TYPES = [
  { value: 'string' as const, label: 'Строка' },
  { value: 'number' as const, label: 'Число' },
  { value: 'date' as const, label: 'Дата' },
];

const DATE_PRECISIONS: { value: DatePrecision; label: string }[] = [
  { value: 'day', label: 'Полная (ДД.ММ.ГГГГ)' },
  { value: 'month', label: 'Месяц и год (ММ.ГГГГ)' },
  { value: 'year', label: 'Только год (ГГГГ)' },
];

const DATE_PRECISION_LABEL: Record<DatePrecision, string> = {
  day: 'полная', month: 'месяц и год', year: 'только год',
};

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
          <label className="block text-sm font-medium text-fg1 mb-1">Шаблон (regex)</label>
          <input
            type="text"
            className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm font-mono"
            placeholder="например: ^[\w.]+@[\w]+\.\w+$"
            value={constraints.pattern ?? ''}
            onChange={e => e.target.value ? set('pattern', e.target.value) : unset('pattern')}
          />
        </div>
        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">Сообщение об ошибке</label>
          <input
            type="text"
            className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm"
            placeholder="например: Введите корректный email"
            value={constraints.patternMessage ?? ''}
            onChange={e => e.target.value ? set('patternMessage', e.target.value) : unset('patternMessage')}
          />
        </div>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className="block text-sm font-medium text-fg1 mb-1">Мин. длина</label>
            <input
              type="number"
              min={0}
              className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm"
              value={constraints.minLength ?? ''}
              onChange={e => e.target.value ? set('minLength', Number(e.target.value)) : unset('minLength')}
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-fg1 mb-1">Макс. длина</label>
            <input
              type="number"
              min={0}
              className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm"
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
            <label className="block text-sm font-medium text-fg1 mb-1">Минимум</label>
            <input
              type="number"
              className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm"
              value={constraints.min ?? ''}
              onChange={e => e.target.value ? set('min', Number(e.target.value)) : unset('min')}
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-fg1 mb-1">Максимум</label>
            <input
              type="number"
              className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm"
              value={constraints.max ?? ''}
              onChange={e => e.target.value ? set('max', Number(e.target.value)) : unset('max')}
            />
          </div>
        </div>
        <label className="flex items-center gap-2 cursor-pointer">
          <input
            type="checkbox"
            className="w-4 h-4 rounded accent-brand"
            checked={constraints.integer ?? false}
            onChange={e => e.target.checked ? set('integer', true) : unset('integer')}
          />
          <span className="text-sm text-fg1">Только целые числа</span>
        </label>
      </div>
    );
  }

  // date
  const dateCls = 'w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm';
  const precision = constraints.datePrecision ?? 'day';
  return (
    <div className="space-y-3">
      <div>
        <label className="block text-sm font-medium text-fg1 mb-1">Точность</label>
        <div className="flex gap-2">
          {DATE_PRECISIONS.map(p => (
            <button
              key={p.value}
              type="button"
              onClick={() => p.value === 'day' ? unset('datePrecision') : set('datePrecision', p.value)}
              className={`px-3 py-2 rounded-lg text-sm font-medium transition-colors ${
                precision === p.value ? 'bg-brand text-white' : 'bg-base text-fg2 hover:bg-muted'
              }`}
            >
              {p.label}
            </button>
          ))}
        </div>
        <p className="text-xs text-fg3 mt-1">
          Управляет форматом ввода и отображения. Значение хранится как полная дата.
        </p>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">Минимальная дата</label>
          <DateInput
            value={constraints.minDate ?? ''}
            onChange={v => v ? set('minDate', v) : unset('minDate')}
            precision={precision}
            className={dateCls}
          />
        </div>
        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">Максимальная дата</label>
          <DateInput
            value={constraints.maxDate ?? ''}
            onChange={v => v ? set('maxDate', v) : unset('maxDate')}
            precision={precision}
            className={dateCls}
          />
        </div>
      </div>
    </div>
  );
}

// ─── Type form (инлайн для редактирования строки, в модалке для создания) ─────

interface TypeFormProps {
  initial?: PrimitiveTypeDef;
  onSaved: () => void;
  onCancel: () => void;
}

function TypeForm({ initial, onSaved, onCancel }: TypeFormProps) {
  const [name, setName] = useState(initial?.name ?? '');
  const [code, setCode] = useState(initial?.code ?? '');
  const [baseType, setBaseType] = useState<'string' | 'number' | 'date'>(initial?.baseType ?? 'string');
  const [description, setDescription] = useState(initial?.description ?? '');
  const [constraints, setConstraints] = useState<FieldConstraints>(initial?.constraints ?? {});
  const [allowedTags, setAllowedTags] = useState<string[]>(initial?.allowedTags ?? []);
  const [error, setError] = useState('');

  const create = useCreatePrimitiveType();
  const update = useUpdatePrimitiveType(initial?.id ?? '');
  const { data: tagRegistry } = useTagRegistry();
  const applicableTags = fieldTags(tagRegistry, baseType);

  function handleBaseTypeChange(bt: 'string' | 'number' | 'date') {
    setBaseType(bt);
    setConstraints({});
    setAllowedTags([]); // применимость тэгов зависит от базового типа
  }

  async function handleSave() {
    if (!name.trim()) { setError('Укажите название'); return; }
    if (!code.trim()) { setError('Укажите код'); return; }
    if (!/^[a-zA-Z][a-zA-Z0-9_]*$/.test(code.trim())) {
      setError('Код: только латинские буквы, цифры и _'); return;
    }
    setError('');
    const dto = buildPrimitiveTypeDto(name.trim(), code.trim(), baseType, description.trim() || undefined, constraints, allowedTags);
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
        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">Название</label>
          <input
            type="text"
            className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm"
            placeholder="Email"
            value={name}
            onChange={e => setName(e.target.value)}
          />
        </div>
        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">Код</label>
          <input
            type="text"
            className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm font-mono"
            placeholder="email"
            value={code}
            onChange={e => setCode(e.target.value)}
            disabled={!!initial}
          />
        </div>
      </div>

      <div>
        <label className="block text-sm font-medium text-fg1 mb-1">Базовый тип</label>
        <div className="flex gap-2">
          {BASE_TYPES.map(bt => (
            <button
              key={bt.value}
              type="button"
              onClick={() => handleBaseTypeChange(bt.value)}
              disabled={!!initial}
              className={`px-4 py-2 rounded-lg text-sm font-medium transition-colors ${
                baseType === bt.value
                  ? 'bg-brand text-white'
                  : 'bg-base text-fg2 hover:bg-muted'
              } disabled:opacity-50`}
            >
              {bt.label}
            </button>
          ))}
        </div>
      </div>

      <div>
        <label className="block text-sm font-medium text-fg1 mb-1">Описание</label>
        <input
          type="text"
          className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm"
          placeholder="Необязательное описание типа"
          value={description}
          onChange={e => setDescription(e.target.value)}
        />
      </div>

      <div className="border-t border-stroke pt-3">
        <p className="text-sm font-medium text-fg1 mb-3">Ограничения</p>
        <ConstraintEditor baseType={baseType} constraints={constraints} onChange={setConstraints} />
      </div>

      <div className="border-t border-stroke pt-3">
        <p className="text-sm font-medium text-fg1 mb-1">Применимые функциональные тэги</p>
        <p className="text-xs text-fg3 mb-2">
          Какие функциональные тэги можно назначить полям этого типа (показываются в редакторе схемы).
        </p>
        {applicableTags.length === 0 ? (
          <p className="text-xs text-fg4">Для базового типа «{baseType}» подходящих тэгов нет.</p>
        ) : (
          <div className="flex flex-wrap gap-1.5">
            {applicableTags.map(t => {
              const on = allowedTags.includes(t.code);
              return (
                <button key={t.code} type="button" title={t.description}
                  onClick={() => setAllowedTags(prev => on ? prev.filter(c => c !== t.code) : [...prev, t.code])}
                  className={`px-2.5 py-1 rounded-full text-xs border transition-colors ${
                    on ? 'bg-purple-500/15 border-purple-400 text-purple-700' : 'border-stroke text-fg3 hover:border-stroke-strong hover:text-fg1'
                  }`}>
                  {t.label}
                </button>
              );
            })}
          </div>
        )}
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
    const prec = c.datePrecision ?? 'day';
    if (prec !== 'day') parts.push(DATE_PRECISION_LABEL[prec]);
    if (c.minDate) parts.push(`от ${formatDateRu(c.minDate, prec)}`);
    if (c.maxDate) parts.push(`до ${formatDateRu(c.maxDate, prec)}`);
  }
  if (parts.length === 0) return <span className="text-fg4 italic">нет ограничений</span>;
  return <span>{parts.join(', ')}</span>;
}

// ─── Row ──────────────────────────────────────────────────────────────────────

const BASE_TYPE_LABEL: Record<string, string> = { string: 'Строка', number: 'Число', date: 'Дата' };

function FieldTypeRow({ type, allGroups, expanded, onToggle }: {
  type: PrimitiveTypeDef; allGroups: string[]; expanded: boolean; onToggle: () => void;
}) {
  const deleteMutation = useDeletePrimitiveType();
  const groupMutation = useSetPrimitiveTypeGroup();
  const [confirmDelete, setConfirmDelete] = useState(false);

  function handleDelete(e: React.MouseEvent) {
    e.stopPropagation();
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
          <span className="text-[11px] bg-brand-subtle text-brand px-1.5 py-0.5 rounded-full shrink-0">
            {BASE_TYPE_LABEL[type.baseType] ?? type.baseType}
          </span>
          <span className="flex-1" />
          <span className="text-xs text-fg4 shrink-0 truncate max-w-[240px]">
            <ConstraintSummary baseType={type.baseType} c={type.constraints} />
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
          <TypeForm initial={type} onSaved={onToggle} onCancel={onToggle} />
        </div>
      )}
      {confirmDelete && (
        <Modal open onOpenChange={o => { if (!o) setConfirmDelete(false); }} title="Удалить тип поля"
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
                onClick={() => deleteMutation.mutateAsync(type.id).then(() => setConfirmDelete(false))}
                className="px-4 py-2 rounded-lg text-sm font-medium bg-danger text-white hover:bg-danger disabled:opacity-50"
              >
                Удалить
              </button>
            </div>
          }>
          <div className="space-y-4 min-w-[360px]">
            <p className="text-sm text-fg2">
              Тип <span className="font-semibold text-fg1">«{type.name}»</span> будет удалён.
              Поля документов, использующие этот тип, перестанут валидироваться.
            </p>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ─── Primitive section (существующее содержимое страницы, без изменений) ──────

function PrimitiveTypesSection() {
  const { data: types = [], isLoading } = useListPrimitiveTypes();
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
          Пользовательские типы реквизитов на основе строки, числа или даты с ограничениями
        </p>
        <Button variant="filled" icon={<Plus size={16} />} onClick={() => setCreateOpen(true)}>
          Добавить тип
        </Button>
      </div>

      {isLoading ? (
        <div className="text-center text-fg4 text-sm py-10">Загрузка...</div>
      ) : types.length === 0 ? (
        <div className="text-center text-fg4 text-sm py-10">
          Пользовательских типов ещё нет. Создайте тип, например «Email» или «ИНН».
        </div>
      ) : (
        <TypeGroupAccordion items={sorted} getGroup={t => t.group} renderItem={t => (
          <FieldTypeRow key={t.id} type={t} allGroups={allGroups}
            expanded={expandedId === t.id} onToggle={() => toggleExpanded(t.id)} />
        )} />
      )}

      <Modal open={createOpen} onOpenChange={setCreateOpen} title="Новый тип поля">
        {createOpen && (
          <TypeForm onSaved={() => setCreateOpen(false)} onCancel={() => setCreateOpen(false)} />
        )}
      </Modal>
    </div>
  );
}

// ─── Main page — вкладки «Примитивные»/«Перечисления» (issue #59) ──────────────

type FieldTypesMode = 'primitive' | 'enum';

export function PrimitiveTypesPage() {
  const [mode, setMode] = useState<FieldTypesMode>('primitive');

  return (
    <div className="px-6 py-4">
      <div className="flex items-center justify-between mb-1">
        <h1 className="text-xl font-semibold text-fg1">Типы полей</h1>
      </div>
      <div className="flex items-center gap-1 bg-muted rounded-lg p-0.5 mb-4 w-fit">
        <button
          onClick={() => setMode('primitive')}
          className={`px-3 py-1.5 text-sm rounded-md transition-colors ${
            mode === 'primitive' ? 'bg-surface text-fg1 font-medium shadow-sm' : 'text-fg3 hover:text-fg2'
          }`}
        >
          Примитивные
        </button>
        <button
          onClick={() => setMode('enum')}
          className={`px-3 py-1.5 text-sm rounded-md transition-colors ${
            mode === 'enum' ? 'bg-surface text-fg1 font-medium shadow-sm' : 'text-fg3 hover:text-fg2'
          }`}
        >
          Перечисления
        </button>
      </div>

      {mode === 'primitive' ? <PrimitiveTypesSection /> : <EnumTypesSection />}
    </div>
  );
}
