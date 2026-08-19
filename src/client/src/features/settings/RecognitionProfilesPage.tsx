import { useEffect, useMemo, useState } from 'react';
import { Plus, Trash2, Lock, RotateCcw, ScanText, AlertTriangle } from 'lucide-react';
import { MoveButtons } from '@/shared/ui/MoveButtons';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { Modal } from '@/shared/ui/Modal';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { moveItem } from '@/shared/utils/moveItem';
import { useRememberedSelection } from '@/shared/hooks/useRememberedSelection';
import { rowKey, withRowUid, withRowUids } from '@/shared/utils/rowIdentity';
import { useToast } from '@/shared/ui/Toast';
import { ListDetailShell, NavSearchInput, NavSection, DetailHeader } from '@/shared/ui/ListDetailShell';
import {
  useListRecognitionProfiles, useRecognitionKinds, useCreateRecognitionProfile,
  useUpdateRecognitionProfile, useResetRecognitionProfile, useDeleteRecognitionProfile,
  profileSummary,
  type RecognitionProfile, type RecognitionProfileField, type RecognitionKindInfo, type RecognitionTableShape,
} from '@/shared/api/recognitionProfiles';

/**
 * Библиотека профилей распознавания (issue #408). Промпты пишем мы — пользователь правит только
 * ПАРАМЕТРЫ: перечень полей/колонок и структурные флаги формы таблицы.
 *
 * Страница намеренно ничего не знает про конкретные виды: показывать ли редактор скаляров, колонок,
 * флагов формы и какие поля защищены — берётся из kindInfo, приходящего с сервера.
 */

const FIELD_TYPES = [
  { value: 'string', label: 'строка' },
  { value: 'number', label: 'число' },
  { value: 'date', label: 'дата' },
];

const EMPTY_SHAPE: RecognitionTableShape = { twoTierHeader: false, pairedSections: false, skipTotals: true };

// ─── Редактор списка полей/колонок ─────────────────────────────────────────────

/**
 * Состояние строк при синхронизации с сервером: если пришло то же самое, оставляем СВОИ строки.
 * Иначе каждое обновление профиля выдавало бы новые личности, а с ними — новые ключи React: строки
 * пересоздавались бы целиком, роняя каретку в поле, куда пользователь только что кликнул.
 */
function keepIfSame(prev: RecognitionProfileField[], incoming: RecognitionProfileField[]): RecognitionProfileField[] {
  return JSON.stringify(prev) === JSON.stringify(incoming) ? prev : withRowUids(incoming);
}

function FieldsEditor({ fields, onChange, systemNames, addLabel }: {
  fields: RecognitionProfileField[];
  onChange: (f: RecognitionProfileField[]) => void;
  /** Имена, которые нельзя удалить/переименовать — на них завязан код разбора документов. */
  systemNames: string[];
  addLabel: string;
}) {
  function update(i: number, patch: Partial<RecognitionProfileField>) {
    onChange(fields.map((f, fi) => fi === i ? { ...f, ...patch } : f));
  }
  function remove(i: number) { onChange(fields.filter((_, fi) => fi !== i)); }
  // Порядок значим: в этом порядке поля печатаются в промпт.
  function move(from: number, to: number) {
    const next = moveItem(fields, from, to);
    if (next !== fields) onChange(next);
  }

  const cols = 'grid grid-cols-[1fr_1.6fr_110px_auto] gap-1.5 items-center';
  return (
    <div className="space-y-1.5">
      <div className={`${cols} text-xs text-fg4 px-0.5`}>
        <span>Имя (ключ)</span>
        <span>Описание — подсказка модели</span>
        <span>Тип</span>
        <span />
      </div>
      {fields.map((f, i) => {
        const locked = systemNames.includes(f.name.trim());
        return (
          <div key={rowKey(f, i)} className={cols}>
            <div className="relative">
              <input value={f.name} onChange={e => update(i, { name: e.target.value })}
                readOnly={locked} placeholder="Позиция"
                title={locked ? 'Обязательное поле — на нём завязано разбиение документов' : undefined}
                className={`w-full border border-stroke-strong rounded px-2 py-1.5 text-sm font-mono focus:outline-none focus-visible:ring-1 focus-visible:ring-brand bg-surface ${
                  locked ? 'pr-7 text-fg3 cursor-not-allowed' : ''}`} />
              {locked && (
                <Lock size={12} className="absolute right-2 top-1/2 -translate-y-1/2 text-fg4"
                  aria-label="Обязательное поле" />
              )}
            </div>
            <input value={f.description ?? ''} onChange={e => update(i, { description: e.target.value })}
              placeholder="Что это за колонка — своими словами"
              className="border border-stroke-strong rounded px-2 py-1.5 text-sm focus:outline-none focus-visible:ring-1 focus-visible:ring-brand bg-surface" />
            <select value={f.type || 'string'} onChange={e => update(i, { type: e.target.value })}
              className="border border-stroke-strong rounded px-2 py-1.5 text-sm bg-surface text-fg1">
              {FIELD_TYPES.map(t => <option key={t.value} value={t.value}>{t.label}</option>)}
            </select>
            <span className="flex items-center gap-0.5">
              <MoveButtons onUp={() => move(i, i - 1)} onDown={() => move(i, i + 1)}
                isFirst={i === 0} isLast={i === fields.length - 1} />
              <button type="button" onClick={() => remove(i)} disabled={locked}
                className="p-0.5 text-fg4 hover:text-danger disabled:opacity-25"
                title={locked ? 'Обязательное поле — удалить нельзя' : 'Удалить'}><Trash2 size={13} /></button>
            </span>
          </div>
        );
      })}
      <button type="button" onClick={() => onChange([...fields, withRowUid({ name: '', description: '', type: 'string' })])}
        className="flex items-center gap-1 text-sm text-brand hover:text-brand-hover pt-0.5">
        <Plus size={13} /> {addLabel}
      </button>
    </div>
  );
}

// ─── Флаги формы таблицы ───────────────────────────────────────────────────────

function ShapeEditor({ shape, onChange }: { shape: RecognitionTableShape; onChange: (s: RecognitionTableShape) => void }) {
  const flags: { key: keyof RecognitionTableShape; label: string; hint: string }[] = [
    { key: 'twoTierHeader', label: 'Двухэтажная шапка', hint: 'Колонки сгруппированы под общими заголовками' },
    { key: 'pairedSections', label: 'Парные секции', hint: 'Повторяющиеся подколонки, напр. «по проекту» и «фактически»' },
    { key: 'skipTotals', label: 'Пропускать итоговые строки', hint: 'Итоги не попадут в данные' },
  ];
  return (
    <div className="space-y-1.5">
      {flags.map(f => (
        <label key={f.key} className="flex items-start gap-2 text-sm text-fg2 cursor-pointer">
          <input type="checkbox" checked={shape[f.key]} onChange={e => onChange({ ...shape, [f.key]: e.target.checked })}
            className="mt-0.5 accent-[var(--f-brand)]" />
          <span>
            {f.label}
            <span className="block text-xs text-fg4">{f.hint}</span>
          </span>
        </label>
      ))}
    </div>
  );
}

// ─── Детальная панель ──────────────────────────────────────────────────────────

function ProfileDetail({ profile }: { profile: RecognitionProfile }) {
  const toast = useToast();
  const update = useUpdateRecognitionProfile();
  const reset = useResetRecognitionProfile();
  const del = useDeleteRecognitionProfile();

  const [name, setName] = useState(profile.name);
  // Личности строк — здесь, у владельца состояния (issue #517).
  const [fields, setFields] = useState<RecognitionProfileField[]>(() => withRowUids(profile.fields));
  const [rowColumns, setRowColumns] = useState<RecognitionProfileField[]>(() => withRowUids(profile.rowColumns));
  const [shape, setShape] = useState<RecognitionTableShape>(profile.shape ?? EMPTY_SHAPE);
  const [error, setError] = useState('');
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [confirmReset, setConfirmReset] = useState(false);

  // Сервер — источник истины после сохранения/сброса: подхватываем его ответ.
  useEffect(() => {
    setName(profile.name);
    setFields(prev => keepIfSame(prev, profile.fields));
    setRowColumns(prev => keepIfSame(prev, profile.rowColumns));
    setShape(profile.shape ?? EMPTY_SHAPE);
    setError('');
  }, [profile]);

  const info = profile.kindInfo;
  const dirty =
    name !== profile.name
    || JSON.stringify(fields) !== JSON.stringify(profile.fields)
    || JSON.stringify(rowColumns) !== JSON.stringify(profile.rowColumns)
    || (info.supportsShape && JSON.stringify(shape) !== JSON.stringify(profile.shape ?? EMPTY_SHAPE));

  async function save() {
    setError('');
    try {
      await update.mutateAsync({
        id: profile.id, name: name.trim(),
        fields: clean(fields), rowColumns: clean(rowColumns),
        shape: info.supportsShape ? shape : null,
      });
      toast.success('Профиль сохранён');
    } catch (e) {
      setError(errText(e));
      throw e;
    }
  }

  return (
    <div className="flex flex-col min-h-0 flex-1">
      <DetailHeader
        heading={
          <span className="flex items-center gap-2">
            <span>{profile.name}</span>
            <span className="text-xs font-normal text-fg4">{info.label}</span>
            {profile.isBuiltIn && (
              <span className="text-[11px] px-1.5 py-0.5 rounded bg-muted text-fg3">встроенный</span>
            )}
            {profile.isModified && (
              <span className="text-[11px] px-1.5 py-0.5 rounded bg-brand-subtle text-brand-pressed">изменён</span>
            )}
          </span>
        }
        dirty={dirty} saving={update.isPending} onSaveAll={save}
        onRevert={() => {
          setName(profile.name); setFields(withRowUids(profile.fields));
          setRowColumns(withRowUids(profile.rowColumns)); setShape(profile.shape ?? EMPTY_SHAPE);
        }}
        actions={
          <>
            {profile.isBuiltIn ? (
              <Button variant="text" size="sm" icon={<RotateCcw size={14} />}
                disabled={!profile.isModified || reset.isPending}
                onClick={() => setConfirmReset(true)}>Сбросить к заводским</Button>
            ) : (
              <Button variant="text" size="sm" icon={<Trash2 size={14} />}
                onClick={() => setConfirmDelete(true)}>Удалить</Button>
            )}
          </>
        } />

      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4 space-y-5">
        {profile.builtInOutdated && (
          <div className="flex gap-2 rounded-lg border border-warning-border bg-warning-subtle p-3 text-sm text-warning">
            <AlertTriangle size={16} className="shrink-0 mt-0.5" />
            <span>
              Заводская версия этого профиля обновилась в новой версии системы, но ваши правки сохранены —
              обновление к вам не применялось. «Сбросить к заводским» вернёт актуальный набор параметров.
            </span>
          </div>
        )}

        <TextField label="Название" value={name} onChange={e => setName(e.target.value)} />

        {info.hasScalarFields && (
          <section>
            <p className="text-sm font-medium text-fg1 mb-1">Поля документа</p>
            <p className="text-xs text-fg4 mb-2">
              Отдельные значения, которые модель извлекает из документа. Описание — главная подсказка:
              по нему модель понимает, что искать.
            </p>
            <FieldsEditor fields={fields} onChange={setFields}
              systemNames={info.systemFieldNames} addLabel="Добавить поле" />
          </section>
        )}

        {info.isTabular && (
          <section>
            <p className="text-sm font-medium text-fg1 mb-1">Колонки таблицы</p>
            <p className="text-xs text-fg4 mb-2">
              Модель вернёт по одной строке на каждую строку таблицы с этими колонками.
            </p>
            <FieldsEditor fields={rowColumns} onChange={setRowColumns}
              systemNames={[]} addLabel="Добавить колонку" />
          </section>
        )}

        {info.supportsShape && (
          <section>
            <p className="text-sm font-medium text-fg1 mb-1">Форма таблицы</p>
            <p className="text-xs text-fg4 mb-2">
              То, что набором колонок не выразить. Текст подсказки формируем мы — вы выбираете признаки.
            </p>
            <ShapeEditor shape={shape} onChange={setShape} />
          </section>
        )}

        {error && <p className="text-sm text-danger">{error}</p>}
      </div>

      <ConfirmDialog
        open={confirmDelete} onOpenChange={setConfirmDelete}
        title="Удалить профиль?"
        description={`Профиль «${profile.name}» будет удалён. Группы листов, к которым он привязан, вернутся к параметрам по умолчанию.`}
        confirmLabel="Удалить"
        onConfirm={async () => { await del.mutateAsync({ id: profile.id }); toast.success('Профиль удалён'); }} />

      <ConfirmDialog
        open={confirmReset} onOpenChange={setConfirmReset}
        title="Сбросить к заводским?"
        description="Ваши правки этого профиля будут заменены заводским набором параметров."
        confirmLabel="Сбросить"
        onConfirm={async () => { await reset.mutateAsync({ id: profile.id }); toast.success('Профиль сброшен к заводским'); }} />
    </div>
  );
}

// ─── Создание профиля ──────────────────────────────────────────────────────────

function CreateProfileForm({ kinds, onSaved, onCancel }: {
  kinds: RecognitionKindInfo[];
  onSaved: (id: string) => void;
  onCancel: () => void;
}) {
  const [name, setName] = useState('');
  const [kind, setKind] = useState(kinds[0]?.kind ?? '');
  const [error, setError] = useState('');
  const create = useCreateRecognitionProfile();
  const info = kinds.find(k => k.kind === kind);

  async function save() {
    if (!name.trim()) { setError('Укажите название'); return; }
    setError('');
    try {
      // Заготовка: одна пустая строка в применимой части — чтобы было куда печатать.
      const created = await create.mutateAsync({
        name: name.trim(), kind,
        fields: info?.hasScalarFields ? [{ name: '', description: '', type: 'string' }] : [],
        rowColumns: info?.isTabular ? [{ name: '', description: '', type: 'string' }] : [],
        shape: info?.supportsShape ? EMPTY_SHAPE : null,
      });
      onSaved(created.id);
    } catch (e) { setError(errText(e)); }
  }

  return (
    <div className="space-y-4">
      <TextField label="Название" value={name} onChange={e => setName(e.target.value)} />
      <div>
        <label className="block text-sm font-medium text-fg1 mb-1">Вид</label>
        <select value={kind} onChange={e => setKind(e.target.value)}
          className="w-full border border-stroke rounded-md px-2 py-1.5 text-sm bg-surface text-fg1">
          {kinds.map(k => <option key={k.kind} value={k.kind}>{k.label}</option>)}
        </select>
        <p className="text-xs text-fg4 mt-1">
          Вид определяет, какой промпт применяется, и после создания не меняется.
        </p>
      </div>
      {error && <p className="text-sm text-danger">{error}</p>}
      <div className="flex justify-end gap-2 border-t border-stroke pt-3">
        <Button type="button" variant="text" onClick={onCancel}>Отмена</Button>
        <Button type="button" variant="filled" onClick={save} loading={create.isPending}>
          {create.isPending ? 'Создание…' : 'Создать'}
        </Button>
      </div>
    </div>
  );
}

// ─── Страница ──────────────────────────────────────────────────────────────────

// Выбор в URL + память последнего открытого (issue #787, общий хелпер).
const SELECTION_KEYS = ['profile'] as const;
const PROFILES_LAST_KEY = 'recognition-profiles-last';

export function RecognitionProfilesPage() {
  const { data: profiles = [], isLoading } = useListRecognitionProfiles();
  const { data: kinds = [] } = useRecognitionKinds();
  // Удалённый id страхует `?? filtered[0]` ниже — восстановление молча уходит на первый профиль.
  // Выбранный ищется по всем профилям, а не по отфильтрованным: не прошедший поиск профиль
  // остаётся открытым и показывается в рейле отдельной строкой (issue #792, см. `outsideFilter`).
  const { values, remember } = useRememberedSelection(PROFILES_LAST_KEY, SELECTION_KEYS);
  const selectedId = values.profile || null;
  const setSelectedId = (id: string | null) => remember({ profile: id ?? '' });
  const [query, setQuery] = useState('');
  const [createOpen, setCreateOpen] = useState(false);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return q
      ? profiles.filter(p => p.name.toLowerCase().includes(q) || p.kindInfo.label.toLowerCase().includes(q))
      : profiles;
  }, [profiles, query]);

  const builtIn = filtered.filter(p => p.isBuiltIn);
  const custom = filtered.filter(p => !p.isBuiltIn);
  const selected = profiles.find(p => p.id === selectedId) ?? filtered[0];
  // Открытый профиль, не прошедший поиск, показываем отдельной строкой (issue #792): иначе он
  // остаётся в детали, но пропадает из рейла — ни строки, ни подсветки, и снять выбор неоткуда.
  const outsideFilter = selected && !filtered.some(p => p.id === selected.id) ? selected : null;

  const row = (p: RecognitionProfile) => (
    <button key={p.id} type="button" onClick={() => setSelectedId(p.id)}
      className={`w-full text-left px-3 py-2 rounded-lg transition-colors ${
        selected?.id === p.id ? 'bg-brand-subtle text-brand-pressed' : 'text-fg2 hover:bg-base'}`}>
      <span className="flex items-center gap-1.5">
        <span className="text-sm truncate flex-1 min-w-0">{p.name}</span>
        {p.builtInOutdated && <AlertTriangle size={12} className="text-warning shrink-0" aria-label="Заводской профиль обновился" />}
        {p.isModified && <span className="w-1.5 h-1.5 rounded-full bg-brand shrink-0" title="Изменён" />}
      </span>
      <span className="block text-xs text-fg4 truncate">{p.kindInfo.label} · {profileSummary(p)}</span>
    </button>
  );

  return (
    <>
      <ListDetailShell
        title="Профили распознавания"
        subtitle="Параметры к промптам распознавания: какие поля и колонки извлекать из документа"
        titleIcon={<ScanText size={20} className="text-fg3" />}
        headerAction={
          <Button variant="filled" icon={<Plus size={16} />} onClick={() => setCreateOpen(true)}>
            Добавить профиль
          </Button>
        }
        overlay={isLoading ? <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Загрузка…</div> : undefined}
        nav={
          <div className="flex flex-col min-h-0">
            <div className="p-2"><NavSearchInput value={query} onChange={setQuery} placeholder="Поиск профиля…" /></div>
            <div className="flex-1 min-h-0 overflow-y-auto px-2 pb-2 space-y-0.5">
              {outsideFilter && (
                <>
                  <NavSection label="Открыт, вне поиска" />
                  {row(outsideFilter)}
                </>
              )}
              {builtIn.length > 0 && <NavSection label="Встроенные" />}
              {builtIn.map(row)}
              {custom.length > 0 && <NavSection label="Свои" />}
              {custom.map(row)}
              {filtered.length === 0 && (
                <p className="text-sm text-fg4 px-3 py-2">
                  {outsideFilter ? 'Больше ничего не найдено' : 'Ничего не найдено'}
                </p>
              )}
            </div>
          </div>
        }
        detail={selected
          ? <ProfileDetail key={selected.id} profile={selected} />
          : <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Выберите профиль</div>} />

      <Modal open={createOpen} onOpenChange={setCreateOpen} title="Новый профиль распознавания">
        <div className="px-6 py-4">
          <CreateProfileForm kinds={kinds}
            onSaved={id => { setSelectedId(id); setCreateOpen(false); }}
            onCancel={() => setCreateOpen(false)} />
        </div>
      </Modal>
    </>
  );
}

// ─── Вспомогательное ───────────────────────────────────────────────────────────

/** Пустые строки редактора в сохранение не уходят. */
function clean(fields: RecognitionProfileField[]): RecognitionProfileField[] {
  return fields
    .map(f => ({ ...f, name: f.name.trim(), description: f.description?.trim() || undefined }))
    .filter(f => f.name);
}

function errText(e: unknown): string {
  const resp = (e as { response?: { data?: { error?: string } } })?.response?.data?.error;
  return resp ?? (e instanceof Error ? e.message : 'Ошибка сохранения');
}
