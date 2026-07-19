import { useState } from 'react';
import {
  Plus, Trash2, Copy, CaseSensitive, Hash, Calendar, List as ListIcon,
  CheckCircle2, AlertCircle,
} from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { ListDetailShell, NavSearchInput, DetailHeader, useDirtyGuard } from '@/shared/ui/ListDetailShell';
import { TextField } from '@/shared/ui/TextField';
import { DateInput } from '@/shared/ui/DateInput';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { validateConstraint } from '@/features/document-sets/fields/PrimitiveInput';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { parseSchemaFields } from '@/shared/api/schema';
import {
  useListPrimitiveTypes,
  useCreatePrimitiveType,
  useUpdatePrimitiveType,
  useDeletePrimitiveType,
  useSetPrimitiveTypeGroup,
  buildPrimitiveTypeDto,
} from '@/shared/api/primitiveTypes';
import {
  useListEnumTypes,
  useCreateEnumType,
  useUpdateEnumType,
  useDeleteEnumType,
  useSetEnumTypeGroup,
  buildEnumTypeDto,
} from '@/shared/api/enumTypes';
import type { FieldConstraints, PrimitiveTypeDef, EnumTypeDef, EnumOptionDef, DatePrecision, DocumentType } from '@/shared/api/types';
import { formatDateRu } from '@/shared/utils/date';
import { useTagRegistry, fieldTags } from '@/shared/api/tags';
import { GroupPicker } from './TypeGroupAccordion';
import { ValuesEditor, EnumForm, humanEnumPreview } from './EnumTypesSection';
import {
  TypeEditorProvider, useRegisterEditor, useTypeEditorRegistry, LeaveGuardDialog,
} from './typeEditorShell';

// ─── Constants ──────────────────────────────────────────────────────────────────

const BASE_TYPES = [
  { value: 'string' as const, label: 'Строка' },
  { value: 'number' as const, label: 'Число' },
  { value: 'date' as const, label: 'Дата' },
];
const BASE_TYPE_LABEL: Record<string, string> = { string: 'Строка', number: 'Число', date: 'Дата' };

const DATE_PRECISIONS: { value: DatePrecision; label: string }[] = [
  { value: 'day', label: 'Полная (ДД.ММ.ГГГГ)' },
  { value: 'month', label: 'Месяц и год (ММ.ГГГГ)' },
  { value: 'year', label: 'Только год (ГГГГ)' },
];
const DATE_PRECISION_LABEL: Record<DatePrecision, string> = {
  day: 'полная', month: 'месяц и год', year: 'только год',
};

const baseTypeIcon = (bt: string) => bt === 'number' ? Hash : bt === 'date' ? Calendar : CaseSensitive;

/** Имена типов документов, ссылающихся на данный тип поля (primitive/enum) полем с этим typeId
 *  (по своим схемам). Единственная причина занятости типа поля = ссылки из схем, и она полностью
 *  выводима на клиенте из уже загруженных схем — тот же критерий, что и backend-guard удаления. */
function findReferencingTypeNames(typeId: string, kind: 'primitive' | 'enum', allDocTypes: DocumentType[]): string[] {
  return allDocTypes
    .filter(dt => parseSchemaFields(dt.schema).some(f => f.type === kind && f.typeId === typeId))
    .map(dt => dt.name);
}

/** Проактивный контент «Удаление невозможно» (issue #275): типы документов, использующие тип поля.
 *  Пусто → undefined (диалог в обычном режиме подтверждения). */
function usageBlockedNode(usedByNames: string[]): React.ReactNode | undefined {
  if (usedByNames.length === 0) return undefined;
  return (
    <div>
      <p className="mb-1.5 font-medium">Тип используется в схемах — сначала уберите поля этого типа:</p>
      <ul className="list-disc pl-4 space-y-0.5">
        {usedByNames.map(n => <li key={n}>{n}</li>)}
      </ul>
    </div>
  );
}

/** Уникальный код на базе исходного: base2, base3 … (для дублирования типа, issue #210 Этап 2). */
export function uniqueCode(base: string, existing: Set<string>): string {
  if (!existing.has(base)) return base;
  let i = 2; while (existing.has(`${base}${i}`)) i++;
  return `${base}${i}`;
}

/** Человекочитаемое превью ограничений для строки списка. Regex не «переводим» (нельзя надёжно) —
 *  fallback: показываем короткий паттерн (issue #210, рекомендация Дизайнера). */
function humanConstraintPreview(baseType: string, c: FieldConstraints): string {
  const parts: string[] = [];
  if (baseType === 'string') {
    if (c.minLength != null && c.maxLength != null) parts.push(`${c.minLength}–${c.maxLength} симв.`);
    else if (c.minLength != null) parts.push(`от ${c.minLength} симв.`);
    else if (c.maxLength != null) parts.push(`до ${c.maxLength} симв.`);
    if (c.pattern) parts.push(c.pattern.length <= 28 ? c.pattern : c.pattern.slice(0, 26) + '…');
    return parts.length ? parts.join(' · ') : 'без ограничений';
  }
  if (baseType === 'number') {
    if (c.integer) parts.push('только целые');
    if (c.min != null && c.max != null) parts.push(`${c.min}…${c.max}`);
    else if (c.min != null) parts.push(`≥ ${c.min}`);
    else if (c.max != null) parts.push(`≤ ${c.max}`);
    return parts.length ? parts.join(' · ') : 'любое число';
  }
  const prec = c.datePrecision ?? 'day';
  if (prec !== 'day') parts.push(DATE_PRECISION_LABEL[prec]);
  if (c.minDate) parts.push(`от ${formatDateRu(c.minDate, prec)}`);
  if (c.maxDate) parts.push(`до ${formatDateRu(c.maxDate, prec)}`);
  return parts.length ? parts.join(' · ') : 'любая дата';
}

// ─── Constraint editor (per base type) ──────────────────────────────────────────

function ConstraintEditor({ baseType, constraints, onChange }: {
  baseType: 'string' | 'number' | 'date';
  constraints: FieldConstraints;
  onChange: (c: FieldConstraints) => void;
}) {
  function set<K extends keyof FieldConstraints>(key: K, val: FieldConstraints[K]) {
    onChange({ ...constraints, [key]: val });
  }
  function unset(key: keyof FieldConstraints) {
    const next = { ...constraints };
    delete next[key];
    onChange(next);
  }
  const inputCls = 'w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm';

  if (baseType === 'string') {
    return (
      <div className="space-y-3">
        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">Шаблон (regex)</label>
          <input type="text" className={`${inputCls} font-mono`} placeholder="например: ^[\w.]+@[\w]+\.\w+$"
            value={constraints.pattern ?? ''}
            onChange={e => e.target.value ? set('pattern', e.target.value) : unset('pattern')} />
        </div>
        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">Сообщение об ошибке</label>
          <input type="text" className={inputCls} placeholder="например: Введите корректный email"
            value={constraints.patternMessage ?? ''}
            onChange={e => e.target.value ? set('patternMessage', e.target.value) : unset('patternMessage')} />
        </div>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className="block text-sm font-medium text-fg1 mb-1">Мин. длина</label>
            <input type="number" min={0} className={inputCls} value={constraints.minLength ?? ''}
              onChange={e => e.target.value ? set('minLength', Number(e.target.value)) : unset('minLength')} />
          </div>
          <div>
            <label className="block text-sm font-medium text-fg1 mb-1">Макс. длина</label>
            <input type="number" min={0} className={inputCls} value={constraints.maxLength ?? ''}
              onChange={e => e.target.value ? set('maxLength', Number(e.target.value)) : unset('maxLength')} />
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
            <input type="number" className={inputCls} value={constraints.min ?? ''}
              onChange={e => e.target.value ? set('min', Number(e.target.value)) : unset('min')} />
          </div>
          <div>
            <label className="block text-sm font-medium text-fg1 mb-1">Максимум</label>
            <input type="number" className={inputCls} value={constraints.max ?? ''}
              onChange={e => e.target.value ? set('max', Number(e.target.value)) : unset('max')} />
          </div>
        </div>
        <label className="flex items-center gap-2 cursor-pointer">
          <input type="checkbox" className="w-4 h-4 rounded accent-brand"
            checked={constraints.integer ?? false}
            onChange={e => e.target.checked ? set('integer', true) : unset('integer')} />
          <span className="text-sm text-fg1">Только целые числа</span>
        </label>
      </div>
    );
  }
  const precision = constraints.datePrecision ?? 'day';
  return (
    <div className="space-y-3">
      <div>
        <label className="block text-sm font-medium text-fg1 mb-1">Точность</label>
        <div className="inline-flex rounded-full border border-stroke-strong overflow-hidden">
          {DATE_PRECISIONS.map(p => (
            <button key={p.value} type="button"
              onClick={() => p.value === 'day' ? unset('datePrecision') : set('datePrecision', p.value)}
              className={`px-3 py-1.5 text-sm transition-colors ${
                precision === p.value ? 'bg-brand-subtle text-brand-hover font-medium' : 'text-fg2 hover:bg-muted'}`}>
              {p.label}
            </button>
          ))}
        </div>
        <p className="text-xs text-fg3 mt-1">Управляет форматом ввода/отображения. Значение хранится как полная дата.</p>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">Минимальная дата</label>
          <DateInput value={constraints.minDate ?? ''} onChange={v => v ? set('minDate', v) : unset('minDate')}
            precision={precision} className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm" />
        </div>
        <div>
          <label className="block text-sm font-medium text-fg1 mb-1">Максимальная дата</label>
          <DateInput value={constraints.maxDate ?? ''} onChange={v => v ? set('maxDate', v) : unset('maxDate')}
            precision={precision} className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-surface text-sm" />
        </div>
      </div>
    </div>
  );
}

// ─── Constraint tester (живая проверка образца) ──────────────────────────────────

/** Проверка образца по текущим ограничениям — через ТУ ЖЕ validateConstraint, что и рантайм форм
 *  документов (issue #210, требование Дизайнера: не дублировать валидацию). Состояние эфемерное. */
function ConstraintTester({ def }: { def: PrimitiveTypeDef }) {
  const [sample, setSample] = useState('');
  const has = sample.trim() !== '';
  const err = has ? validateConstraint(sample, def) : null;
  const ok = has && !err;
  const placeholder = def.baseType === 'date' ? 'ДД.ММ.ГГГГ' : def.baseType === 'number' ? 'например: 42' : 'введите образец';
  const StatusIcon = ok ? CheckCircle2 : AlertCircle;
  const tone = !has ? 'text-fg4' : ok ? 'text-success' : 'text-danger';
  return (
    <div>
      <label className="block text-sm font-medium text-fg1 mb-1">Проверка образца</label>
      <div className="flex items-center gap-2 rounded-lg bg-base border border-stroke px-3 py-2">
        <StatusIcon size={16} className={`shrink-0 ${tone}`} />
        <input value={sample} onChange={e => setSample(e.target.value)} placeholder={placeholder} spellCheck={false}
          className="flex-1 min-w-0 bg-transparent text-sm font-mono outline-none text-fg1 placeholder:text-fg4" />
        <span className={`text-xs shrink-0 max-w-[45%] truncate ${tone}`} title={has && !ok ? (err ?? '') : undefined}>
          {!has ? 'по ограничениям' : ok ? 'соответствует' : err}
        </span>
      </div>
    </div>
  );
}

// ─── Segmented base-type control ────────────────────────────────────────────────

function BaseTypeSegmented({ value, onChange, disabled }: {
  value: 'string' | 'number' | 'date'; onChange: (v: 'string' | 'number' | 'date') => void; disabled?: boolean;
}) {
  return (
    <div className={`inline-flex rounded-full border border-stroke-strong overflow-hidden ${disabled ? 'opacity-60' : ''}`}>
      {BASE_TYPES.map(bt => {
        const Icon = baseTypeIcon(bt.value);
        const on = value === bt.value;
        return (
          <button key={bt.value} type="button" disabled={disabled} onClick={() => onChange(bt.value)}
            className={`flex items-center gap-1.5 px-4 py-1.5 text-sm transition-colors disabled:cursor-not-allowed ${
              on ? 'bg-brand-subtle text-brand-hover font-medium' : 'text-fg2 hover:bg-muted'}`}>
            <Icon size={15} className="shrink-0" /> {bt.label}
          </button>
        );
      })}
    </div>
  );
}

// ─── Card wrapper ────────────────────────────────────────────────────────────────

function Card({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="border border-stroke rounded-xl bg-surface p-4 space-y-3">
      <p className="text-xs font-medium text-fg3 uppercase tracking-wide">{title}</p>
      {children}
    </div>
  );
}

// ─── Primitive create form (в модалке) ──────────────────────────────────────────

function PrimitiveCreateForm({ onSaved, onCancel }: { onSaved: () => void; onCancel: () => void }) {
  const [name, setName] = useState('');
  const [code, setCode] = useState('');
  const [baseType, setBaseType] = useState<'string' | 'number' | 'date'>('string');
  const [description, setDescription] = useState('');
  const [constraints, setConstraints] = useState<FieldConstraints>({});
  const [error, setError] = useState('');
  const create = useCreatePrimitiveType();

  async function handleSave() {
    if (!name.trim()) { setError('Укажите название'); return; }
    if (!code.trim()) { setError('Укажите код'); return; }
    if (!/^[a-zA-Z][a-zA-Z0-9_]*$/.test(code.trim())) { setError('Код: только латинские буквы, цифры и _'); return; }
    setError('');
    try {
      await create.mutateAsync(buildPrimitiveTypeDto(name.trim(), code.trim(), baseType, description.trim() || undefined, constraints, []));
      onSaved();
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Ошибка сохранения');
    }
  }

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-3">
        <TextField label="Название" value={name} onChange={e => setName(e.target.value)} />
        <TextField label="Код" value={code} onChange={e => setCode(e.target.value)} className="font-mono" />
      </div>
      <div>
        <label className="block text-sm font-medium text-fg1 mb-1">Базовый тип</label>
        <BaseTypeSegmented value={baseType} onChange={bt => { setBaseType(bt); setConstraints({}); }} />
      </div>
      <TextField label="Описание" value={description} onChange={e => setDescription(e.target.value)} />
      <div className="border-t border-stroke pt-3">
        <p className="text-sm font-medium text-fg1 mb-3">Ограничения</p>
        <ConstraintEditor baseType={baseType} constraints={constraints} onChange={setConstraints} />
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

// ─── Detail header (доменные heading/actions поверх общего DetailHeader) ──────────

function TypeDetailHeader({ name, code, chip, usedBy, dirty, saving, onSaveAll, onRevert, onDuplicate, allGroups, group, onGroup, onDelete }: {
  name: string; code: string; chip: string; usedBy: number;
  dirty: boolean; saving: boolean; onSaveAll: () => Promise<void>; onRevert: () => void; onDuplicate: () => void;
  allGroups: string[]; group: string | null; onGroup: (g: string | null) => void;
  onDelete: () => void;
}) {
  const badge = 'text-xs px-2 py-0.5 rounded-full font-medium';
  return (
    <DetailHeader dirty={dirty} saving={saving} onSaveAll={onSaveAll} onRevert={onRevert}
      heading={
        <>
          <div className="flex items-center gap-2 flex-wrap">
            <h2 className="text-xl font-normal text-fg1 truncate">{name || '(без названия)'}</h2>
            <span className={`${badge} bg-muted text-fg3`}>{chip}</span>
            {usedBy > 0 && <span className={`${badge} bg-brand-subtle text-brand`}>используется: {usedBy}</span>}
          </div>
          <span className="text-xs text-fg4 font-mono">{code}</span>
        </>
      }
      actions={
        <>
          <GroupPicker groups={allGroups} value={group} onChange={onGroup} />
          <RowActionsMenu ariaLabel="Действия типа" actions={[
            { key: 'dup', label: 'Дублировать', icon: <Copy size={14} />, onSelect: onDuplicate },
            { key: 'del', label: 'Удалить', danger: true, icon: <Trash2 size={14} />, onSelect: onDelete },
          ]} />
        </>
      } />
  );
}

// ─── Primitive detail ────────────────────────────────────────────────────────────

function PrimitiveTypeDetail({ type, allGroups, usedByNames, dirty, saving, onSaveAll, onRevert, onDuplicate, onDeleted }: {
  type: PrimitiveTypeDef; allGroups: string[]; usedByNames: string[];
  dirty: boolean; saving: boolean; onSaveAll: () => Promise<void>; onRevert: () => void; onDuplicate: () => void; onDeleted: () => void;
}) {
  const [name, setName] = useState(type.name);
  const [description, setDescription] = useState(type.description ?? '');
  const [constraints, setConstraints] = useState<FieldConstraints>(type.constraints ?? {});
  const [allowedTags, setAllowedTags] = useState<string[]>(type.allowedTags ?? []);
  const [error, setError] = useState('');
  const [confirmDelete, setConfirmDelete] = useState(false);

  const update = useUpdatePrimitiveType(type.id);
  const del = useDeletePrimitiveType();
  const groupMutation = useSetPrimitiveTypeGroup();
  const { data: tagRegistry } = useTagRegistry();
  const applicableTags = fieldTags(tagRegistry, type.baseType);

  const localDirty = name !== type.name
    || description !== (type.description ?? '')
    || JSON.stringify(constraints) !== JSON.stringify(type.constraints ?? {})
    || JSON.stringify([...allowedTags].sort()) !== JSON.stringify([...(type.allowedTags ?? [])].sort());

  async function save() {
    if (!name.trim()) { setError('Укажите название'); throw new Error('validation'); }
    setError('');
    try {
      await update.mutateAsync(buildPrimitiveTypeDto(name.trim(), type.code, type.baseType, description.trim() || undefined, constraints, allowedTags));
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Ошибка сохранения'); throw e;
    }
  }
  const reset = () => { setName(type.name); setDescription(type.description ?? ''); setConstraints(type.constraints ?? {}); setAllowedTags(type.allowedTags ?? []); setError(''); };
  useRegisterEditor('primitive', localDirty, save, reset);

  return (
    <div className="flex flex-col min-h-0 flex-1">
      <TypeDetailHeader name={name} code={type.code} chip={BASE_TYPE_LABEL[type.baseType] ?? type.baseType} usedBy={usedByNames.length}
        dirty={dirty} saving={saving} onSaveAll={onSaveAll} onRevert={onRevert} onDuplicate={onDuplicate}
        allGroups={allGroups} group={type.group} onGroup={g => groupMutation.mutate({ id: type.id, group: g })}
        onDelete={() => setConfirmDelete(true)} />
      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-5">
        <div className="mx-auto max-w-3xl space-y-4">
          <Card title="Параметры">
            <div className="grid grid-cols-2 gap-3">
              <TextField label="Название" value={name} onChange={e => setName(e.target.value)} required />
              <TextField label="Код" value={type.code} onChange={() => {}} disabled className="font-mono" />
            </div>
            <div>
              <label className="block text-sm font-medium text-fg1 mb-1">Базовый тип</label>
              <BaseTypeSegmented value={type.baseType} onChange={() => {}} disabled />
              <p className="text-xs text-fg3 mt-1">Базовый тип нельзя изменить после создания.</p>
            </div>
            <TextField label="Описание" value={description} onChange={e => setDescription(e.target.value)} />
          </Card>

          <Card title="Ограничения">
            <ConstraintEditor baseType={type.baseType} constraints={constraints} onChange={setConstraints} />
            <ConstraintTester def={{ ...type, constraints }} />
          </Card>

          <Card title="Применимые функциональные тэги">
            <p className="text-xs text-fg3 -mt-1">Какие тэги можно назначать полям этого типа — показываются в редакторе схемы.</p>
            {applicableTags.length === 0 ? (
              <p className="text-xs text-fg4">Для базового типа «{BASE_TYPE_LABEL[type.baseType]}» подходящих тэгов нет.</p>
            ) : (
              <div className="flex flex-wrap gap-1.5">
                {applicableTags.map(t => {
                  const on = allowedTags.includes(t.code);
                  return (
                    <button key={t.code} type="button" title={t.description}
                      onClick={() => setAllowedTags(prev => on ? prev.filter(c => c !== t.code) : [...prev, t.code])}
                      className={`px-2.5 py-1 rounded-full text-xs border transition-colors ${
                        on ? 'bg-purple-500/15 border-purple-400 text-purple-700' : 'border-stroke text-fg3 hover:border-stroke-strong hover:text-fg1'}`}>
                      {t.label}
                    </button>
                  );
                })}
              </div>
            )}
          </Card>

          {error && <p className="text-sm text-danger">{error}</p>}
        </div>
      </div>
      <ConfirmDialog open={confirmDelete} onOpenChange={setConfirmDelete}
        title={`Удалить тип «${type.name}»?`}
        description={<p>Поля документов, использующие этот тип, перестанут валидироваться. Действие необратимо.</p>}
        confirmLabel={`Удалить «${type.name}»`}
        blocked={usageBlockedNode(usedByNames)}
        onConfirm={() => del.mutateAsync(type.id).then(onDeleted)} />
    </div>
  );
}

// ─── Enum detail ──────────────────────────────────────────────────────────────────

function EnumTypeDetail({ type, allGroups, usedByNames, dirty, saving, onSaveAll, onRevert, onDuplicate, onDeleted }: {
  type: EnumTypeDef; allGroups: string[]; usedByNames: string[];
  dirty: boolean; saving: boolean; onSaveAll: () => Promise<void>; onRevert: () => void; onDuplicate: () => void; onDeleted: () => void;
}) {
  const [name, setName] = useState(type.name);
  const [description, setDescription] = useState(type.description ?? '');
  const [values, setValues] = useState<EnumOptionDef[]>(type.values ?? []);
  const [error, setError] = useState('');
  const [confirmDelete, setConfirmDelete] = useState(false);

  const update = useUpdateEnumType(type.id);
  const del = useDeleteEnumType();
  const groupMutation = useSetEnumTypeGroup();

  const localDirty = name !== type.name
    || description !== (type.description ?? '')
    || JSON.stringify(values) !== JSON.stringify(type.values ?? []);

  async function save() {
    if (!name.trim()) { setError('Укажите название'); throw new Error('validation'); }
    const cleaned = values.map(v => ({ code: v.code.trim(), label: v.label.trim() })).filter(v => v.code && v.label);
    if (cleaned.length === 0) { setError('Добавьте хотя бы один вариант'); throw new Error('validation'); }
    const codes = new Set<string>();
    for (const v of cleaned) {
      if (codes.has(v.code)) { setError(`Код «${v.code}» повторяется`); throw new Error('validation'); }
      codes.add(v.code);
    }
    setError('');
    try {
      await update.mutateAsync(buildEnumTypeDto(name.trim(), type.code, description.trim() || undefined, cleaned));
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Ошибка сохранения'); throw e;
    }
  }
  const reset = () => { setName(type.name); setDescription(type.description ?? ''); setValues(type.values ?? []); setError(''); };
  useRegisterEditor('enum', localDirty, save, reset);

  return (
    <div className="flex flex-col min-h-0 flex-1">
      <TypeDetailHeader name={name} code={type.code} chip="Перечисление" usedBy={usedByNames.length}
        dirty={dirty} saving={saving} onSaveAll={onSaveAll} onRevert={onRevert} onDuplicate={onDuplicate}
        allGroups={allGroups} group={type.group} onGroup={g => groupMutation.mutate({ id: type.id, group: g })}
        onDelete={() => setConfirmDelete(true)} />
      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-5">
        <div className="mx-auto max-w-3xl space-y-4">
          <Card title="Параметры">
            <div className="grid grid-cols-2 gap-3">
              <TextField label="Название" value={name} onChange={e => setName(e.target.value)} required />
              <TextField label="Код" value={type.code} onChange={() => {}} disabled className="font-mono" />
            </div>
            <TextField label="Описание" value={description} onChange={e => setDescription(e.target.value)} />
          </Card>

          <Card title={`Варианты · ${values.length}`}>
            <ValuesEditor values={values} onChange={setValues} />
          </Card>

          {error && <p className="text-sm text-danger">{error}</p>}
        </div>
      </div>
      <ConfirmDialog open={confirmDelete} onOpenChange={setConfirmDelete}
        title={`Удалить тип «${type.name}»?`}
        description={<p>Поля документов, использующие это перечисление, перестанут резолвить варианты. Действие необратимо.</p>}
        confirmLabel={`Удалить «${type.name}»`}
        blocked={usageBlockedNode(usedByNames)}
        onConfirm={() => del.mutateAsync(type.id).then(onDeleted)} />
    </div>
  );
}

// ─── Left panel (tabs + search + list) ───────────────────────────────────────────

type Mode = 'primitive' | 'enum';

function FieldTypeListPanel({ mode, onMode, primitives, enums, selectedId, onSelect, query, onQuery }: {
  mode: Mode; onMode: (m: Mode) => void;
  primitives: PrimitiveTypeDef[]; enums: EnumTypeDef[];
  selectedId: string | null; onSelect: (id: string) => void;
  query: string; onQuery: (q: string) => void;
}) {
  const q = query.trim().toLowerCase();
  const items: { id: string; name: string; code: string; chip: string; preview: string; icon: React.ComponentType<{ size?: number; className?: string }> }[] =
    mode === 'primitive'
      ? primitives.filter(t => !q || `${t.name} ${t.code}`.toLowerCase().includes(q))
          .map(t => ({ id: t.id, name: t.name, code: t.code, chip: BASE_TYPE_LABEL[t.baseType] ?? t.baseType, preview: humanConstraintPreview(t.baseType, t.constraints), icon: baseTypeIcon(t.baseType) }))
      : enums.filter(t => !q || `${t.name} ${t.code}`.toLowerCase().includes(q))
          .map(t => ({ id: t.id, name: t.name, code: t.code, chip: 'Перечисление', preview: humanEnumPreview(t.values), icon: ListIcon }));

  const tab = (m: Mode, label: string) => (
    <button type="button" onClick={() => onMode(m)}
      className={`relative flex-1 h-12 text-sm transition-colors ${mode === m ? 'text-brand font-medium' : 'text-fg3 hover:text-fg2'}`}>
      {label}
      {mode === m && <span className="absolute left-0 right-0 bottom-0 h-0.5 bg-brand rounded-t" />}
    </button>
  );

  return (
    <>
      <div className="flex border-b border-stroke shrink-0">
        {tab('primitive', 'Примитивные')}
        {tab('enum', 'Перечисления')}
      </div>
      <NavSearchInput value={query} onChange={onQuery} placeholder="Поиск типа…" />
      <div className="flex-1 overflow-y-auto px-2 pb-3 space-y-0.5">
        {items.length === 0 && <p className="px-3 py-6 text-center text-sm text-fg4">Ничего не найдено</p>}
        {items.map(t => {
          const active = t.id === selectedId;
          const Icon = t.icon;
          return (
            <button key={t.id} type="button" onClick={() => onSelect(t.id)} aria-current={active ? 'true' : undefined}
              className={`w-full flex items-center gap-2.5 px-3 py-2 rounded-xl text-left transition-colors ${
                active ? 'bg-brand-subtle text-brand-hover' : 'hover:bg-muted'}`}>
              <Icon size={17} className={`shrink-0 ${active ? 'text-brand-hover' : 'text-fg4'}`} />
              <span className="min-w-0 flex-1">
                <span className="flex items-center gap-1.5">
                  <span className={`text-sm font-medium truncate ${active ? 'text-brand-hover' : 'text-fg1'}`}>{t.name}</span>
                  <span className="text-[11px] text-fg4 font-mono shrink-0">{t.code}</span>
                </span>
                <span className="block text-xs text-fg4 truncate">{t.preview}</span>
              </span>
              <span className="text-[11px] px-1.5 py-0.5 rounded-full bg-muted text-fg3 shrink-0">{t.chip}</span>
            </button>
          );
        })}
      </div>
    </>
  );
}

// ─── Page ─────────────────────────────────────────────────────────────────────────

export function PrimitiveTypesPage() {
  const [mode, setMode] = useState<Mode>('primitive');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [createOpen, setCreateOpen] = useState(false);

  const { data: primitives = [] } = useListPrimitiveTypes();
  const { data: enums = [] } = useListEnumTypes();
  const { data: allDocTypes = [] } = useListDocumentTypes();

  const sortedPrim = [...primitives].sort((a, b) => a.name.localeCompare(b.name, 'ru'));
  const sortedEnum = [...enums].sort((a, b) => a.name.localeCompare(b.name, 'ru'));
  const allGroups = [...new Set([...sortedPrim, ...sortedEnum].map(t => t.group).filter((g): g is string => !!g))]
    .sort((a, b) => a.localeCompare(b, 'ru'));

  const { registry, anyDirty, saving, saveAll, resetAll } = useTypeEditorRegistry();

  // Общий гард несохранённых изменений (ключ = {mode,id}) — issue #210 Этап 1 (ListDetailShell).
  const { request, dialogProps } = useDirtyGuard<{ mode: Mode; id: string | null }>({
    isDirty: anyDirty, saving, saveAll,
    onCommit: ({ mode: m, id }) => { setMode(m); setSelectedId(id); },
  });
  const requestSelect = (id: string) => { if (id !== selectedId) request({ mode, id }); };
  const requestMode = (m: Mode) => { if (m !== mode) request({ mode: m, id: null }); };

  const selectedPrim = mode === 'primitive' ? (sortedPrim.find(t => t.id === selectedId) ?? sortedPrim[0]) : undefined;
  const selectedEnum = mode === 'enum' ? (sortedEnum.find(t => t.id === selectedId) ?? sortedEnum[0]) : undefined;
  const addLabel = mode === 'primitive' ? 'Добавить тип' : 'Добавить перечисление';

  // Дублирование типа (клиентский клон, issue #210 Этап 2): имя «Копия …», код с суффиксом.
  const createPrim = useCreatePrimitiveType();
  const createEnum = useCreateEnumType();
  const duplicatePrim = (t: PrimitiveTypeDef) => createPrim.mutate(
    buildPrimitiveTypeDto(`Копия ${t.name}`, uniqueCode(t.code, new Set(sortedPrim.map(x => x.code))), t.baseType, t.description, t.constraints, t.allowedTags));
  const duplicateEnum = (t: EnumTypeDef) => createEnum.mutate(
    buildEnumTypeDto(`Копия ${t.name}`, uniqueCode(t.code, new Set(sortedEnum.map(x => x.code))), t.description, t.values));

  const detail = mode === 'primitive' && selectedPrim ? (
    <PrimitiveTypeDetail key={selectedPrim.id} type={selectedPrim} allGroups={allGroups}
      usedByNames={findReferencingTypeNames(selectedPrim.id, 'primitive', allDocTypes)}
      dirty={anyDirty} saving={saving} onSaveAll={saveAll} onRevert={resetAll}
      onDuplicate={() => duplicatePrim(selectedPrim)} onDeleted={() => setSelectedId(null)} />
  ) : mode === 'enum' && selectedEnum ? (
    <EnumTypeDetail key={selectedEnum.id} type={selectedEnum} allGroups={allGroups}
      usedByNames={findReferencingTypeNames(selectedEnum.id, 'enum', allDocTypes)}
      dirty={anyDirty} saving={saving} onSaveAll={saveAll} onRevert={resetAll}
      onDuplicate={() => duplicateEnum(selectedEnum)} onDeleted={() => setSelectedId(null)} />
  ) : (
    <div className="flex-1 flex items-center justify-center text-fg4 text-sm">
      {mode === 'primitive' ? 'Типов полей ещё нет' : 'Перечислений ещё нет'} — создайте первый.
    </div>
  );

  return (
    <>
      <TypeEditorProvider value={registry}>
        <ListDetailShell
          title="Типы полей"
          subtitle="Пользовательские типы реквизитов (строка/число/дата) и перечисления"
          headerAction={<Button variant="filled" icon={<Plus size={16} />} onClick={() => setCreateOpen(true)}>{addLabel}</Button>}
          nav={<FieldTypeListPanel mode={mode} onMode={requestMode}
            primitives={sortedPrim} enums={sortedEnum}
            selectedId={mode === 'primitive' ? (selectedPrim?.id ?? null) : (selectedEnum?.id ?? null)}
            onSelect={requestSelect} query={query} onQuery={setQuery} />}
          detail={detail} />
      </TypeEditorProvider>

      <LeaveGuardDialog {...dialogProps} />

      <Modal open={createOpen} onOpenChange={setCreateOpen}
        title={mode === 'primitive' ? 'Новый тип поля' : 'Новый тип перечисления'}>
        {createOpen && (mode === 'primitive'
          ? <PrimitiveCreateForm onSaved={() => setCreateOpen(false)} onCancel={() => setCreateOpen(false)} />
          : <EnumForm onSaved={() => setCreateOpen(false)} onCancel={() => setCreateOpen(false)} />)}
      </Modal>
    </>
  );
}
