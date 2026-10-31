import { Fragment, useMemo, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Add, Delete, DocumentScanner, LockIcon, RestartAlt, VisibilityOff, Warning } from '@/shared/ui/icons';
import { MoveButtons } from '@/shared/ui/MoveButtons';
import { Button, IconButton } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { Modal } from '@/shared/ui/Modal';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { moveItem } from '@/shared/utils/moveItem';
import { useRememberedSelection } from '@/shared/hooks/useRememberedSelection';
import { useServerForm } from '@/shared/hooks/useServerForm';
import { rowKey, withRowUid, withRowUids } from '@/shared/utils/rowIdentity';
import { useToast } from '@/shared/ui/Toast';
import { ListDetailShell, NavSearchInput, NavSection, DetailHeader, DetailTitle } from '@/shared/ui/ListDetailShell';
import { useListDetailView } from '@/shared/ui/listDetailView';
import {
  useListRecognitionProfiles, useRecognitionKinds, useCreateRecognitionProfile,
  useUpdateRecognitionProfile, useResetRecognitionProfile, useDeleteRecognitionProfile,
  useHiddenRecognitionProfiles, profileSummary,
  type RecognitionProfile, type RecognitionProfileField, type RecognitionKindInfo, type RecognitionTableShape,
} from '@/shared/api/recognitionProfiles';
import { groupByModule, hiddenSummary, type HiddenRecognitionProfile } from '@/shared/api/recognitionProfileGroups';
import { Select, SelectGroup, SelectItem } from '@/shared/ui/Select';
import { Tooltip } from '@/shared/ui/Tooltip';
import { Checkbox } from '@/shared/ui/Checkbox';

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
// Модульная константа, а не `= []` в месте чтения: новый массив на каждый рендер.
const EMPTY_HIDDEN: HiddenRecognitionProfile[] = [];
const EMPTY_PROFILES: RecognitionProfile[] = [];

// ─── Редактор списка полей/колонок ─────────────────────────────────────────────

/** Значение как есть — для кусков формы, которые не надо переводить (имя, форма таблицы). */
const asIs = <T,>(x: T): T => x;

/** Ошибка сохранения относится к ТОМУ профилю, при котором случилась: пришёл другой — она снята. */
const noError = () => '';

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
      <div className={`${cols} text-body-small text-on-surface-variant px-0.5`}>
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
              <Tooltip content={locked ? 'Обязательное поле — на нём завязано разбиение документов' : undefined}>
                <input value={f.name} onChange={e => update(i, { name: e.target.value })}
                  readOnly={locked} placeholder="Позиция"
                  className={`w-full border border-outline rounded-xs px-2 py-1.5 text-body-medium font-mono focus:outline-none focus-visible:ring-1 focus-visible:ring-primary bg-surface ${
                    locked ? 'pr-7 text-on-surface-variant cursor-not-allowed' : ''}`} />
              </Tooltip>
              {locked && (
                <Icon icon={LockIcon} size={18} className="absolute right-2 top-1/2 -translate-y-1/2 text-on-surface-variant"
                  aria-label="Обязательное поле" />
              )}
            </div>
            <input value={f.description ?? ''} onChange={e => update(i, { description: e.target.value })}
              placeholder="Что это за колонка — своими словами"
              className="border border-outline rounded-xs px-2 py-1.5 text-body-medium focus:outline-none focus-visible:ring-1 focus-visible:ring-primary bg-surface" />
            <Select value={f.type || 'string'} onValueChange={type => update(i, { type })}
              aria-label={`Тип: ${f.name.trim() || `строка ${i + 1}`}`}>
              {FIELD_TYPES.map(t => <SelectItem key={t.value} value={t.value}>{t.label}</SelectItem>)}
            </Select>
            <span className="flex items-center gap-0.5">
              <MoveButtons onUp={() => move(i, i - 1)} onDown={() => move(i, i + 1)}
                isFirst={i === 0} isLast={i === fields.length - 1} />
              <IconButton label="Удалить"
                title={locked ? 'Обязательное поле — удалить нельзя' : undefined}
                danger
                onClick={() => remove(i)}
                disabled={locked} icon={Delete} />
            </span>
          </div>
        );
      })}
      <button type="button" onClick={() => onChange([...fields, withRowUid({ name: '', description: '', type: 'string' })])}
        className="flex items-center gap-1 text-body-medium text-primary state-layer pt-0.5">
        <Icon icon={Add} size={18} /> {addLabel}
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
        <Checkbox key={f.key} checked={shape[f.key]} onChange={v => onChange({ ...shape, [f.key]: v })}
          label={f.label} description={f.hint} />
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

  /**
   * Форма поверх ответа сервера (issue #858) — но каждый кусок поверх СВОЕГО куска ответа, а не
   * поверх профиля целиком.
   *
   * <p>Это и есть замена прежнему `keepIfSame`. Личности строк (issue #517) заводятся при переливе
   * списка в форму, а новые личности означают новые ключи React: строки пересоздаются, роняя
   * каретку. Пока сервер отдаёт ТОТ ЖЕ массив полей (React Query бережёт ссылку у неизменившихся
   * кусков), перелива не происходит вовсе — даже если в профиле поменялось имя. Прежний эффект
   * сбрасывал всё сразу и от каретки спасался сравнением JSON.</p>
   */
  const [name, setName] = useServerForm(profile.name, asIs);
  const [fields, setFields] = useServerForm(profile.fields, withRowUids);
  const [rowColumns, setRowColumns] = useServerForm(profile.rowColumns, withRowUids);
  const [shape, setShape] = useServerForm(profile.shape ?? EMPTY_SHAPE, asIs);
  const [error, setError] = useServerForm(profile, noError);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [confirmReset, setConfirmReset] = useState(false);

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
          <span className="flex flex-wrap items-center gap-x-2 gap-y-1 min-w-0">
            <DetailTitle>{profile.name}</DetailTitle>
            <span className="text-body-small font-normal text-on-surface-variant">{info.label}</span>
            {profile.isBuiltIn && (
              <span className="text-label-small px-1.5 py-0.5 rounded-xs bg-surface-container text-on-surface-variant">встроенный</span>
            )}
            {profile.isModified && (
              <span className="text-label-small px-1.5 py-0.5 rounded-xs bg-primary-container text-on-primary-container">изменён</span>
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
              <Button variant="text" icon={<Icon icon={RestartAlt} size={18} />}
                disabled={!profile.isModified || reset.isPending}
                onClick={() => setConfirmReset(true)}>Сбросить к заводским</Button>
            ) : (
              <Button variant="text" icon={<Icon icon={Delete} size={18} />}
                onClick={() => setConfirmDelete(true)}>Удалить</Button>
            )}
          </>
        } />

      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4 space-y-5">
        {profile.builtInOutdated && (
          <div className="flex gap-2 rounded-md border border-outline-variant bg-warning-container p-3 text-body-medium text-on-warning-container">
            <Icon icon={Warning} size={18} className="shrink-0 mt-0.5" />
            <span>
              Заводская версия этого профиля обновилась в новой версии системы, но ваши правки сохранены —
              обновление к вам не применялось. «Сбросить к заводским» вернёт актуальный набор параметров.
            </span>
          </div>
        )}

        <TextField label="Название" value={name} onChange={e => setName(e.target.value)} />

        {info.hasScalarFields && (
          <section>
            <p className="text-body-medium font-medium text-on-surface mb-1">Поля документа</p>
            <p className="text-body-small text-on-surface-variant mb-2">
              Отдельные значения, которые модель извлекает из документа. Описание — главная подсказка:
              по нему модель понимает, что искать.
            </p>
            <FieldsEditor fields={fields} onChange={setFields}
              systemNames={info.systemFieldNames} addLabel="Добавить поле" />
          </section>
        )}

        {info.isTabular && (
          <section>
            <p className="text-body-medium font-medium text-on-surface mb-1">Колонки таблицы</p>
            <p className="text-body-small text-on-surface-variant mb-2">
              Модель вернёт по одной строке на каждую строку таблицы с этими колонками.
            </p>
            <FieldsEditor fields={rowColumns} onChange={setRowColumns}
              systemNames={[]} addLabel="Добавить колонку" />
          </section>
        )}

        {info.supportsShape && (
          <section>
            <p className="text-body-medium font-medium text-on-surface mb-1">Форма таблицы</p>
            <p className="text-body-small text-on-surface-variant mb-2">
              То, что набором колонок не выразить. Текст подсказки формируем мы — вы выбираете признаки.
            </p>
            <ShapeEditor shape={shape} onChange={setShape} />
          </section>
        )}

        {error && <p className="text-body-medium text-error">{error}</p>}
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
  // Виды — по модулям (issue #1075): профиль получит владельцем владельца вида, и это видно до
  // создания, а не после — по группе, в которой он окажется.
  const kindGroups = groupByModule(kinds);

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
        <Select label="Вид" value={kind} onValueChange={setKind}>
          {kindGroups.map(g => (
            <SelectGroup key={g.module} label={g.title}>
              {g.items.map(k => <SelectItem key={k.kind} value={k.kind}>{k.label}</SelectItem>)}
            </SelectGroup>
          ))}
        </Select>
        <p className="text-body-small text-on-surface-variant mt-1">
          Вид определяет, какой промпт применяется, и после создания не меняется.
        </p>
      </div>
      {error && <p className="text-body-medium text-error">{error}</p>}
      <div className="flex justify-end gap-2 border-t border-outline-variant pt-3">
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
  const { data: loaded, isLoading, isError: profilesFailed } = useListRecognitionProfiles();
  const profiles = loaded ?? EMPTY_PROFILES;
  const { data: kinds } = useRecognitionKinds();
  const { data: hidden = EMPTY_HIDDEN } = useHiddenRecognitionProfiles();
  // «Видов нет» — это ОТВЕТ сервера: ни ожидание, ни отказ запроса им не считаются. Пока ответа
  // нет, кнопка создания просто недоступна, а причину «нет модулей» называем, только получив пустой.
  const noKinds = kinds?.length === 0;
  const hiddenLine = hiddenSummary(hidden);
  // Удалённый id страхует `?? filtered[0]` ниже — восстановление молча уходит на первый профиль.
  // Выбранный ищется по всем профилям, а не по отфильтрованным: не прошедший поиск профиль
  // остаётся открытым и показывается в рейле отдельной строкой (issue #792, см. `outsideFilter`).
  const { values, remember } = useRememberedSelection(PROFILES_LAST_KEY, SELECTION_KEYS);
  const selectedId = values.profile || null;
  const setSelectedId = (id: string | null) => remember({ profile: id ?? '' });
  const ld = useListDetailView();
  const [query, setQuery] = useState('');
  const [createOpen, setCreateOpen] = useState(false);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return q
      ? profiles.filter(p => p.name.toLowerCase().includes(q) || p.kindInfo.label.toLowerCase().includes(q)
          || (p.moduleTitle ?? '').toLowerCase().includes(q))
      : profiles;
  }, [profiles, query]);

  // Группы — по модулю-владельцу, а не «Встроенные / Свои» (issue #1075): модулей несколько, и
  // вопрос «чей это профиль» стал первым. Заголовок стоит и при единственной группе — иначе
  // появление второго модуля меняло бы устройство списка, а не добавляло строку.
  const groups = groupByModule(filtered);
  // Умолчание — ВЕРХНЯЯ строка рейла, а не первая в ответе сервера: рейл сложен по модулям, и
  // первый по имени профиль может стоять в середине списка.
  const selected = profiles.find(p => p.id === selectedId) ?? groups[0]?.items[0];
  // Открытый профиль, не прошедший поиск, показываем отдельной строкой (issue #792): иначе он
  // остаётся в детали, но пропадает из рейла — ни строки, ни подсветки, и снять выбор неоткуда.
  const outsideFilter = selected && !filtered.some(p => p.id === selected.id) ? selected : null;

  const row = (p: RecognitionProfile) => (
    <button key={p.id} type="button" onClick={() => { ld.showDetail(); setSelectedId(p.id); }}
      className={`state-layer w-full text-left px-3 py-2 rounded-md ${
        selected?.id === p.id ? 'bg-primary-container text-on-primary-container' : 'text-on-surface-variant'}`}>
      <span className="flex items-center gap-1.5">
        <span className="text-body-medium truncate flex-1 min-w-0">{p.name}</span>
        {p.builtInOutdated && <Icon icon={Warning} size={18} className="text-warning shrink-0" aria-label="Заводской профиль обновился" />}
        {p.isModified && <Tooltip content="Изменён"><span className="w-1.5 h-1.5 rounded-full bg-primary shrink-0" /></Tooltip>}
      </span>
      <span className="block text-body-small text-on-surface-variant truncate">
        {p.kindInfo.label} · {profileSummary(p)}{p.isBuiltIn ? '' : ' · свой'}
      </span>
    </button>
  );

  return (
    <>
      <ListDetailShell view={ld}
        title="Профили распознавания"
        subtitle="Параметры к промптам распознавания: какие поля и колонки извлекать из документа"
        icon={<Icon icon={DocumentScanner} size={20} className="text-on-surface-variant" />}
        actions={
          <span className="flex items-center gap-3">
            {/* Причина — текстом рядом, а не подсказкой на кнопке: выключенная кнопка событий
                мыши не получает, и подсказка на ней не показалась бы никогда. */}
            {noKinds && (
              <span className="text-body-small text-on-surface-variant">Ни один включённый модуль не объявляет видов профилей</span>
            )}
            <Button variant="filled" icon={<Icon icon={Add} size={18} />} onClick={() => setCreateOpen(true)}
              disabled={!kinds || noKinds}>
              Добавить профиль
            </Button>
          </span>
        }
        overlay={isLoading ? <div className="flex-1 flex items-center justify-center text-on-surface-variant text-body-medium">Загрузка…</div> : undefined}
        nav={
          <div className="flex flex-col min-h-0">
            <div className="p-2"><NavSearchInput value={query} onChange={setQuery} placeholder="Профиль, вид или модуль…" /></div>
            <div className="flex-1 min-h-0 overflow-y-auto px-2 pb-2 space-y-0.5">
              {outsideFilter && (
                <>
                  <NavSection label="Открыт, вне поиска" />
                  {row(outsideFilter)}
                </>
              )}
              {groups.map(g => (
                <Fragment key={g.module}>
                  <NavSection label={g.title} />
                  {g.items.map(row)}
                </Fragment>
              ))}
              {filtered.length === 0 && (
                <p className="text-body-medium text-on-surface-variant px-3 py-2">
                  {profilesFailed
                    ? 'Не удалось получить список профилей'
                    : emptyText(loaded?.length === 0, !!outsideFilter)}
                </p>
              )}
            </div>
            {/* Профили выключенных модулей лежат в базе, но не предлагаются. Строка говорит, что
                они есть и почему их не видно, — иначе «у меня был профиль» выглядит потерей. */}
            {hiddenLine && (
              <Tooltip content={hidden.map(h => h.name).join('\n')}>
                <p className="flex items-start gap-1.5 border-t border-outline-variant px-3 py-2 text-body-small text-on-surface-variant">
                  <Icon icon={VisibilityOff} size={18} className="shrink-0 mt-0.5" />
                  <span>{hiddenLine}</span>
                </p>
              </Tooltip>
            )}
          </div>
        }
        detail={selected
          ? <ProfileDetail key={selected.id} profile={selected} />
          : <div className="flex-1 flex items-center justify-center text-on-surface-variant text-body-medium">Выберите профиль</div>} />

      <Modal open={createOpen} onOpenChange={setCreateOpen} title="Новый профиль распознавания">
        <CreateProfileForm kinds={kinds ?? []}
          onSaved={id => { setSelectedId(id); setCreateOpen(false); }}
          onCancel={() => setCreateOpen(false)} />
      </Modal>
    </>
  );
}

// ─── Вспомогательное ───────────────────────────────────────────────────────────

/** Почему список пуст: искали и не нашли — или профилей на этом экземпляре нет вовсе. */
function emptyText(noProfiles: boolean, hasOutside: boolean): string {
  if (noProfiles) return 'Ни один включённый модуль не объявляет профили';
  return hasOutside ? 'Больше ничего не найдено' : 'Ничего не найдено';
}

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
