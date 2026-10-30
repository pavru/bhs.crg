import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Add, Delete, DragIndicator, FunctionIcon, KeyboardArrowDown, KeyboardArrowUp, LinkIcon, LockIcon, Memory, Warning } from '@/shared/ui/icons';
import { MoveButtons } from '@/shared/ui/MoveButtons';
import { DateInput } from '@/shared/ui/DateInput';
import { Button, IconButton } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { TypePicker } from '@/shared/ui/TypePicker';
import type { DocumentType, PrimitiveTypeDef, EnumTypeDef } from '@/shared/api/types';
import { FIELD_UID, withFieldUid, type SchemaField, type FieldGroup } from '@/shared/api/schema';
import { toCamelKey, nextAutoKey, nextSavedKey } from './schemaConstants';
import { buildFieldTypeOptions, decodeFieldType, fieldTypeSummary, type FieldRegistries } from './fieldTypeOptions';
import { useAccess } from '@/shared/api/access';
import { ModuleFieldBadge } from './ModuleFieldBadge';
import { SuggestFlag, SuggestOffBadge } from './SuggestFlag';
import { similarKeyOf } from './schemaKeyChecks';
import { useTagRegistry, fieldTags, findTagEntry, hasTag, offeredInRoles, tagCode, withTagOrder, unknownTagCodes } from '@/shared/api/tags';
import { chipTagEntries, tagsForKind, withoutFlagTag } from '@/shared/api/suggestable';
import { FunctionalTagBadge, FunctionalTagChip } from './FunctionalTagChip';
import { ComputedFieldEditor } from './ComputedFieldEditor';
import { moveItem } from '@/shared/utils/moveItem';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Tooltip } from '@/shared/ui/Tooltip';
import { Checkbox } from '@/shared/ui/Checkbox';
// ─── JSON preview ──────────────────────────────────────────────────────────────

export function JsonPreview({
  fields, groups, excludedFields, fieldOverrides,
}: {
  fields: SchemaField[];
  groups: FieldGroup[];
  excludedFields: string[];
  fieldOverrides: Record<string, { required?: boolean; defaultValue?: unknown }>;
}) {
  const json = JSON.stringify(
    {
      fields,
      ...(groups.length ? { groups } : {}),
      ...(excludedFields.length ? { excludedFields } : {}),
      ...(Object.keys(fieldOverrides).length ? { fieldOverrides } : {}),
    },
    null, 2,
  );
  return (
    // Подложка и текст — теми же классами, что у остальных блоков кода в клиенте
    // (`BugReportsPage`, `ErrorBoundary`): тема здесь такая же, как везде, а не прибитая
    // тёмная литералами (issue #1065).
    <pre className="rounded-md px-4 py-3 text-body-small font-mono overflow-x-auto whitespace-pre leading-relaxed select-all bg-surface-container text-on-surface-variant">
      {json}
    </pre>
  );
}

// ─── Field card (single own field, collapsible) ────────────────────────────────

interface FieldCardProps {
  field: SchemaField;
  reg: FieldRegistries;
  keyConflict: boolean;
  /** Ключи полей из СОХРАНЁННОЙ схемы (issue #355). Не передан — все поля считаются новыми. */
  persistedKeys?: Set<string>;
  /** Соседние поля (для пикера ссылок + детекции циклов расчётного поля, issue #368) — без самого себя. */
  siblingFields?: { key: string; title: string; type: string; computed?: boolean; expression?: string }[];
  /** Ключи ОСТАЛЬНЫХ полей типа (свои + унаследованные) — для проверки на опечатку (issue #639). */
  otherKeys?: readonly string[];
  /** Смена ключа СОХРАНЁННОГО поля (issue #357) — для предложения миграции данных на сохранении схемы. */
  onKeyRename?: (from: string, to: string) => void;
  /** Поле только что добавлено (issue #526): курсор ставится в «Название», чтобы можно было сразу печатать. */
  autoFocus?: boolean;
  /** Курсор поставлен — контейнеру пора забыть о добавлении (иначе фокус вернётся при перерисовке). */
  onAutoFocused?: () => void;
  open: boolean;
  onToggleOpen: () => void;
  onChange: (patch: Partial<SchemaField>) => void;
  onRemove: () => void;
  onMoveUp?: () => void;
  onMoveDown?: () => void;
  isFirst?: boolean;
  isLast?: boolean;
  // Drag&drop (управляется контейнером): dragging — эта карточка перетаскивается.
  dragging?: boolean;
  onDragStart?: () => void;
  onDragEnd?: () => void;
  onDragOver?: (e: React.DragEvent) => void;
  onDrop?: (e: React.DragEvent) => void;
}

// Типы, у которых допустимо расчётное поле (issue #368) — скалярный результат формулы.
const COMPUTABLE_TYPES = new Set(['string', 'text', 'number', 'date', 'boolean']);
function SchemaFieldKinds_isComputable(type: string): boolean { return COMPUTABLE_TYPES.has(type); }

/** Одно СВОЁ поле: свёрнутая шапка (drag-ручка + сводка + chip типа + chevron) → раскрытый
 *  редактор (все ветки типов/опций/тэгов). Индекс-независима — переиспользуется в плоском и
 *  группированном представлении (issue #197 Фаза C). */
export function FieldCard({
  field, reg, keyConflict, persistedKeys, siblingFields, otherKeys, onKeyRename, autoFocus, onAutoFocused,
  open, onToggleOpen, onChange, onRemove,
  onMoveUp, onMoveDown, isFirst, isLast, dragging, onDragStart, onDragEnd, onDragOver, onDrop,
}: FieldCardProps) {
  const { primitiveTypes, enumTypes, tagRegistry } = reg;
  const tags = field.tags ?? [];
  // Чипы — без `input.noSuggest`, пока его показывает флажок (issue #1533); правка идёт по `tags`.
  const chips = chipTagEntries(field);
  // Тэги вне реестра этого экземпляра — их не предлагают, но и не прячут (issue #959).
  const unknownTags = unknownTagCodes(field.tags, tagRegistry);
  const [pickerOpen, setPickerOpen] = useState(false);
  // Курсор в «Название» сразу после добавления поля (issue #526): иначе после нажатия «Добавить поле»
  // приходится ещё раз целиться мышью в развернувшуюся карточку.
  const titleRef = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (!autoFocus) return;
    titleRef.current?.focus();
    onAutoFocused?.();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [autoFocus]);
  // Стабильность ключа (issue #355): у сохранённого поля ключ заморожен (read-only + замок), меняется
  // только явной разблокировкой с предупреждением о дрейфе данных.
  const [keyUnlocked, setKeyUnlocked] = useState(false);
  const [confirmUnlock, setConfirmUnlock] = useState(false);

  // savedKey — ключ, под которым поле лежит в СОХРАНЁННОЙ схеме, или null для ещё не сохранённого.
  // Это база сравнения, и она обязана быть СОБСТВЕННЫМ состоянием карточки (issue #527). Прежде
  // «новизна» выводилась из ТЕКУЩЕГО ключа (`!persistedKeys.has(field.key)`), а база фиксировалась на
  // монтировании — и то и другое ломалось ровно наоборот:
  //   • новое поле после сохранения становилось «сохранённым», а база оставалась пустой строкой с
  //     момента добавления → карточка вечно показывала «ключ изменён с «»» (сам issue #527);
  //   • у сохранённого поля первое же нажатие клавиши уводило ключ из persistedKeys, поле начинало
  //     считаться новым, и onKeyRename переставал вызываться со второго символа — в renamesRef
  //     оседал обрезок вроде «Печат», не совпадавший с итоговым ключом, и перенос данных при
  //     переименовании молча НЕ предлагался.
  const [savedKey, setSavedKey] = useState<string | null>(() => nextSavedKey(null, field.key, persistedKeys));
  const prevPersisted = useRef(persistedKeys);
  useEffect(() => {
    setSavedKey(k => nextSavedKey(k, field.key, persistedKeys, prevPersisted.current));
    prevPersisted.current = persistedKeys;
    // Намеренно только по persistedKeys: набор сохранённых ключей меняется после записи схемы, а
    // печать в поле ключа базу сравнения сдвигать не должна — иначе предупреждение исчезнет ровно
    // тогда, когда оно и нужно.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [persistedKeys]);

  const isNew = savedKey === null;
  const keyLocked = !isNew && !keyUnlocked;
  const keyChanged = savedKey !== null && field.key !== savedKey;
  // Опечатка в ключе (issue #639): рядом с «ДатаДокумента» годами жил «ДатаДокумнета» — две
  // переставленные буквы. Считаем прямо в рендере: ключей у типа десятки, а сравнение обрывается
  // на первом же лишнем различии.
  const similarKey = similarKeyOf(field.key, otherKeys ?? []);
  const keyAutoNew = isNew && !!field.title.trim() && field.key === toCamelKey(field.title);
  const { data: access } = useAccess();
  const pickTypes = useMemo(() => buildFieldTypeOptions(reg, access),
    [reg.compositeTypes, reg.primitiveTypes, reg.enumTypes, reg.allDocTypes, access]); // eslint-disable-line react-hooks/exhaustive-deps

  // Тэг снимаем по КОДУ: в схеме он мог быть записан с параметром («identity:2», issue #583), и
  // сравнение всей записи со строкой кода не сняло бы его вовсе.
  const toggleTag = (code: string) => {
    const next = hasTag(tags, code) ? tags.filter(c => tagCode(c) !== code) : [...tags, code];
    onChange({ tags: next.length ? next : undefined });
  };
  /**
   * Номер компонента у уже проставленного тэга: пусто — без номера (поле идёт после нумерованных).
   *
   * Ввод ТЕКСТОВЫЙ с фильтром цифр, а не type="number": последний на промежуточно негодном вводе
   * («1e») отдаёт пустую строку, и номер снимался бы молча — а снятый номер у identity меняет ключи
   * всех материалов разом. Здесь пусто означает пусто: пользователь стёр цифры сам.
   */
  const setTagOrder = (code: string, raw: string) => {
    const digits = raw.replace(/\D/g, '');
    onChange({ tags: withTagOrder(tags, code, digits.length ? Number(digits) : null) });
  };
  // Для поля type="primitive" применимые тэги берём из самого типа поля (allowedTags),
  // для встроенных типов — из реестра по типу поля. `input.noSuggest` при показанном флажке — не чип (#1533).
  const applicableTags = withoutFlagTag(field, (() => {
    if (field.type === 'primitive') {
      const pt = primitiveTypes.find(p => p.id === field.typeId);
      const codes = new Set(pt?.allowedTags ?? []);
      return offeredInRoles((tagRegistry ?? []).filter(t => t.scope === 'Field' && codes.has(t.code)), reg.roles, tags);
    }
    return fieldTags(tagRegistry, field.type, reg.roles, tags);
  })());
  // Легаси enum-поле (issue #59): options инлайн, typeId не выбран.
  const isLegacyEnum = field.type === 'enum' && !field.typeId && (field.options?.length ?? 0) > 0;
  const enumTypeDef = field.type === 'enum' ? enumTypes.find(et => et.id === field.typeId) : undefined;
  // Ключ следует за именем только у НОВОГО поля, пока не тронут вручную (issue #355). У сохранённого —
  // заморожен: переименование НЕ меняет ключ (иначе данные документов осиротеют).
  const updateTitle = (title: string) => {
    const key = nextAutoKey(field.key, field.title, title, isNew);
    onChange({ title, ...(key !== field.key ? { key } : {}) });
  };

  return (
    <div
      className={`border rounded-md overflow-hidden bg-surface transition-colors ${dragging ? 'border-primary' : 'border-outline-variant'}`}
      onDragOver={onDragOver}
      onDrop={onDrop}>
      {/* Свёрнутая шапка карточки: drag handle + сводка + chip типа + chevron */}
      <div className="flex items-center gap-2 pl-2 pr-3 py-2 state-layer"
        draggable onDragStart={onDragStart} onDragEnd={onDragEnd}>
        <Icon icon={DragIndicator} size={18} className="text-on-surface-variant shrink-0 cursor-grab" />
        <button type="button" onClick={onToggleOpen}
          className="flex-1 min-w-0 flex items-center gap-2 text-left">
          <span className="min-w-0 flex-1">
            <span className="flex items-center gap-1.5">
              <span className="text-body-medium font-medium text-on-surface truncate">{field.title || '(без названия)'}</span>
              {field.required && <span className="text-label-small text-error shrink-0">обяз.</span>}
              {keyConflict && <span className="text-label-small text-error shrink-0">! ключ занят</span>}
            </span>
            <span className="block text-body-small text-on-surface-variant font-mono truncate">{field.key || '—'}</span>
          </span>
          <ModuleFieldBadge field={field} /><SuggestOffBadge field={field} />
          {chips.slice(0, 2).map(tc => <FunctionalTagBadge key={tc} registry={tagRegistry} entry={tc} />)}
          {chips.length > 2 && <span className="text-label-small text-on-surface-variant shrink-0">+{chips.length - 2}</span>}
          <span className="text-body-small px-2 py-0.5 rounded-full bg-surface-container text-on-surface-variant shrink-0">{fieldTypeSummary(field, reg)}</span>
          {open ? <Icon icon={KeyboardArrowUp} size={18} className="text-on-surface-variant shrink-0" /> : <Icon icon={KeyboardArrowDown} size={18} className="text-on-surface-variant shrink-0" />}
        </button>
      </div>
      {open && (
      <div className="px-3 pb-3 pt-2 border-t border-outline-variant space-y-2">
      <div className="grid grid-cols-[1fr_1fr_160px_72px_76px] gap-2 items-center">
        {/* Title */}
        <input
          ref={titleRef}
          value={field.title}
          onChange={e => updateTitle(e.target.value)}
          placeholder="Название"
          className="border border-outline rounded-sm px-3 py-1.5 text-body-medium focus:outline-none focus-visible:ring-2 focus-visible:ring-primary bg-surface"
        />
        {/* Key — стабильный идентификатор хранения (issue #355) */}
        <div className="relative">
          <Tooltip content={keyLocked ? 'Идентификатор хранения — заморожен. Нажмите замок, чтобы изменить.' : undefined}>
            <input
              value={field.key}
              onChange={e => { onChange({ key: e.target.value }); if (savedKey !== null) onKeyRename?.(savedKey, e.target.value); }}
              readOnly={keyLocked}
              placeholder="КлючПоля"
              spellCheck={false}
              className={`w-full border rounded-sm pl-3 pr-9 py-1.5 text-body-medium font-mono focus:outline-none focus-visible:ring-2 ${
                keyConflict ? 'border-error focus-visible:ring-error' : 'border-outline focus-visible:ring-primary'
              } ${keyLocked ? 'bg-surface-container/60 text-on-surface-variant cursor-default' : 'bg-surface'}`}
            />
          </Tooltip>
          {keyConflict ? (
            <span className="absolute right-2 top-1/2 -translate-y-1/2 text-body-small text-error">!</span>
          ) : keyLocked ? (
            <IconButton label="Изменить ключ (осторожно: осиротит данные документов)"
              size="sm"
              className="absolute right-0 top-1/2 -translate-y-1/2"
              onClick={() => setConfirmUnlock(true)}>
              <Icon icon={LockIcon} size={18} />
            </IconButton>
          ) : keyAutoNew ? (
            <Tooltip content="Ключ авто-генерируется из названия">
              <span className="absolute right-2 top-1/2 -translate-y-1/2 text-on-surface-variant pointer-events-none">
                <Icon icon={LinkIcon} size={18} />
              </span>
            </Tooltip>
          ) : null}
        </div>
        {/* Type — открывает searchable TypePicker (issue #197) */}
        <Tooltip content="Выбрать тип поля">
          <button type="button" onClick={() => setPickerOpen(true)}
            className="flex items-center justify-between gap-1 border border-outline rounded-sm px-2 py-1.5 text-body-medium bg-surface state-layer focus:outline-none focus-visible:ring-2 focus-visible:ring-primary">
            <span className="truncate text-left">{fieldTypeSummary(field, reg)}</span>
            <Icon icon={KeyboardArrowDown} size={18} className="text-on-surface-variant shrink-0" />
          </button>
        </Tooltip>
        {/* Required (у расчётного поля неприменимо — заменяется меткой ƒ) */}
        {field.computed ? (
          <Tooltip content="Расчётное поле — значение вычисляется формулой">
            <span className="flex items-center justify-center gap-1 text-label-small text-primary">
              <Icon icon={FunctionIcon} size={18} /> вычисл.
            </span>
          </Tooltip>
        ) : (
          <label className="flex items-center justify-center gap-1.5 cursor-pointer">
            <Checkbox size="sm" checked={field.required} onChange={required => onChange({ required })} />
            <span className="text-body-small text-on-surface-variant">да</span>
          </label>
        )}
        {/* Actions */}
        <div className="flex items-center justify-end gap-0.5">
          <MoveButtons onUp={() => onMoveUp?.()} onDown={() => onMoveDown?.()}
            isFirst={!!isFirst} isLast={!!isLast} />
          <IconButton label={`Удалить поле «${field.title || field.key || 'без названия'}»`} size="sm" danger onClick={onRemove}>
            <Icon icon={Delete} size={18} />
          </IconButton>
        </div>
      </div>
      {/* Предупреждение о дрейфе данных при изменённом ключе существующего поля (issue #355). */}
      {keyChanged && (
        <p className="text-body-small text-error flex items-start gap-1.5">
          <Icon icon={LockIcon} size={18} className="shrink-0 mt-0.5" />
          <span>Ключ изменён с «<span className="font-mono">{savedKey}</span>» — данные существующих
            документов останутся под старым ключом (осиротеют). Перенос старый→новый — через «Аудит»
            (переименование осиротевшего ключа в поле схемы).</span>
        </p>
      )}
      {/* Похожий ключ (issue #639) — предупреждение, не запрет: законно похожие ключи бывают.
          При конфликте молчим: там уже красное «ключ занят», и второе сообщение о том же поле
          спорило бы с первым. */}
      {similarKey && !keyConflict && (
        <p className="text-body-small text-warning flex items-start gap-1.5">
          <Icon icon={Warning} size={18} className="shrink-0 mt-0.5" />
          <span>Ключ похож на «<span className="font-mono">{similarKey}</span>» — опечатка? Два почти
            одинаковых ключа дают в форме два поля, а в данных — две несвязанные ячейки.</span>
        </p>
      )}
      <ConfirmDialog
        open={confirmUnlock}
        onOpenChange={setConfirmUnlock}
        title="Изменить ключ поля?"
        description={<p>Ключ — идентификатор хранения. После смены данные всех существующих документов
          этого типа осиротеют по старому ключу «<span className="font-mono">{savedKey}</span>».
          Перенести их на новый ключ можно через «Аудит» (переименование осиротевшего ключа). Продолжить?</p>}
        confirmLabel="Изменить ключ"
        onConfirm={() => { setConfirmUnlock(false); setKeyUnlocked(true); }}
      />
      {/* Расчётное поле (issue #368): switch «Вычисляемое» — мода, не тип. Значение считается
          формулой при генерации; «обязательное»/«по умолчанию» скрываются. */}
      {SchemaFieldKinds_isComputable(field.type) && (
        <>
          <label className="flex items-center gap-2 cursor-pointer w-fit">
            <Checkbox size="sm" checked={!!field.computed}
              onChange={on => onChange(on
                ? { computed: true, required: false, defaultValue: undefined }
                : { computed: undefined, expression: undefined })} />
            <span className="text-body-small text-on-surface-variant flex items-center gap-1">
              <Icon icon={FunctionIcon} size={18} className="text-primary" /> Вычисляемое (формула)
            </span>
          </label>
          {/* Подсказка по введённому ранее (issue #1533): флажок, причина вместо него или ничего. */}
          <SuggestFlag field={field} onChange={onChange} />
          {field.computed && (
            <ComputedFieldEditor field={field} siblingFields={siblingFields ?? []} onChange={onChange} />
          )}
        </>
      )}
      {/* Default value editor */}
      {!field.computed && field.type !== 'complex' && field.type !== 'array' && field.type !== 'primitive'
        && field.type !== 'doc-ref' && field.type !== 'doc-array' && (
        <div className="ml-[calc(33%+0.5rem)] mr-[calc(5rem)] flex items-center gap-2">
          <span className="text-body-small text-on-surface-variant shrink-0 w-28">Значение по умолч.:</span>
          {field.type === 'boolean' ? (
            <label className="flex items-center gap-1.5 cursor-pointer">
              <Checkbox size="sm" checked={!!field.defaultValue}
                onChange={on => onChange({ defaultValue: on ? true : undefined })} />
              <span className="text-body-small text-on-surface-variant">{field.defaultValue ? 'true' : 'не задано'}</span>
            </label>
          ) : field.type === 'enum' ? (
            <Select size="sm" className="flex-1" aria-label="Значение по умолчанию"
              value={String(field.defaultValue ?? '')}
              onValueChange={v => onChange({ defaultValue: v || undefined })}
              emptyItem="— не задано —">
              {enumTypeDef
                ? enumTypeDef.values.map(v => <SelectItem key={v.code} value={v.code}>{v.label}</SelectItem>)
                : (field.options ?? []).filter(o => o).map(opt => (
                    <SelectItem key={opt} value={opt}>{opt}</SelectItem>
                  ))}
            </Select>
          ) : field.type === 'date' ? (
            <DateInput label="Значение по умолчанию"
              value={field.defaultValue != null ? String(field.defaultValue) : ''}
              onChange={v => onChange({ defaultValue: v || undefined })}
              size="sm" className="flex-1"
            />
          ) : (
            <input
              type={field.type === 'number' ? 'number' : 'text'}
              placeholder="не задано"
              value={field.defaultValue != null ? String(field.defaultValue) : ''}
              onChange={e => {
                const v = e.target.value;
                onChange({
                  defaultValue: v === '' ? undefined
                    : field.type === 'number' ? Number(v) : v,
                });
              }}
              className="flex-1 border border-outline-variant rounded-xs px-2 py-1 text-body-small bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-primary"
            />
          )}
        </div>
      )}
      {/* Enum options editor — только легаси (options инлайн, без typeId, issue #59) */}
      {isLegacyEnum && (
        <div className="ml-[calc(33%+0.5rem)] mr-[calc(5rem)] space-y-1.5">
          {(field.options ?? []).map((opt, oi) => (
            <div key={oi} className="flex items-center gap-1.5">
              <input
                value={opt}
                onChange={e => {
                  const opts = [...(field.options ?? [])];
                  opts[oi] = e.target.value;
                  onChange({ options: opts });
                }}
                placeholder={`Вариант ${oi + 1}`}
                className="flex-1 border border-outline rounded-xs px-2 py-1 text-body-small focus:outline-none focus-visible:ring-1 focus-visible:ring-primary bg-surface"
              />
              <IconButton label={`Удалить вариант ${oi + 1}`}
                size="sm"
                danger
                onClick={() => {
                  const opts = (field.options ?? []).filter((_, j) => j !== oi);
                  onChange({ options: opts });
                }}>
                <Icon icon={Delete} size={18} />
              </IconButton>
            </div>
          ))}
          <button type="button"
            onClick={() => onChange({ options: [...(field.options ?? []), ''] })}
            className="flex items-center gap-1 text-body-small text-primary state-layer">
            <Icon icon={Add} size={18} /> Добавить вариант
          </button>
        </div>
      )}
      {/* Тэги, которых в реестре этого экземпляра нет, — обычно тэги выключенного модуля (#959).
          Показываем отдельно и без действия: снять их можно только там, где их читают, а спрятать
          значило бы оставить поле с невидимой меткой, которая оживёт при включении модуля. */}
      {unknownTags.length > 0 && (
        <div className="ml-[calc(33%+0.5rem)] mr-[calc(5rem)] flex items-start gap-2">
          <Icon icon={Memory} size={18} className="mt-1 text-on-surface-variant" />
          <span className="text-body-small text-on-surface-variant shrink-0 w-28 mt-1">Тэги модуля:</span>
          <div className="flex flex-wrap gap-1.5">
            {unknownTags.map(code => (
              <Tooltip key={code} content="Тэг модуля, выключенного на этом экземпляре. Значение остаётся в схеме и заработает, когда модуль включат.">
                <span
                  className="inline-flex items-center gap-1 rounded-full border border-dashed border-outline-variant px-2 py-0.5 text-label-small text-on-surface-variant">
                  <Icon icon={LockIcon} size={18} />{code}
                </span>
              </Tooltip>
            ))}
          </div>
        </div>
      )}
      {/* Функциональные тэги поля (для primitive — из типа поля, иначе из реестра) */}
      {applicableTags.length > 0 && (
        <div className="ml-[calc(33%+0.5rem)] mr-[calc(5rem)] flex items-start gap-2">
          <Icon icon={Memory} size={18} className={`mt-1 ${chips.length ? 'text-tertiary' : 'text-on-surface-variant'}`} />
          <span className="text-body-small text-on-surface-variant shrink-0 w-28 mt-1">Функц. тэги:</span>
          <div className="flex flex-wrap gap-1.5">
            {applicableTags.map(t => {
              return (
                <FunctionalTagChip
                  key={t.code}
                  tag={t}
                  entry={findTagEntry(tags, t.code)}
                  onToggle={() => toggleTag(t.code)}
                  onOrderChange={raw => setTagOrder(t.code, raw)}
                />
              );
            })}
          </div>
        </div>
      )}
      </div>
      )}
      {/* Searchable-пикер типа поля (issue #197): один список — базовые + реестры + составные/ссылки */}
      <TypePicker
        open={pickerOpen}
        onOpenChange={setPickerOpen}
        types={pickTypes}
        recentKey="field-type"
        title="Тип поля"
        onSelect={id => {
          const dec = decodeFieldType(id);
          const changed = dec.type !== field.type || dec.typeId !== field.typeId;
          // `input.noSuggest` — только у строки: ушедшее с неё поле его теряет (issue #1533).
          onChange(changed ? { ...dec, defaultValue: undefined, options: undefined, tags: tagsForKind(field.tags, dec.type) } : dec);
        }}
      />
    </div>
  );
}

// ─── Field builder (own fields, flat list) ─────────────────────────────────────

interface FieldBuilderProps {
  fields: SchemaField[];
  onChange: (fields: SchemaField[]) => void;
  disabledKeys?: Set<string>;
  /** Ключи полей из СОХРАНЁННОЙ схемы (issue #355): их ключи заморожены. Пусто (по умолч.) = все поля
   *  новые (форма создания типа) → ключ авто-следует за именем. */
  persistedKeys?: Set<string>;
  compositeTypes: DocumentType[];
  primitiveTypes: PrimitiveTypeDef[];
  enumTypes: EnumTypeDef[];
  allDocTypes: DocumentType[];
}

export function FieldBuilder({ fields, onChange, disabledKeys, persistedKeys, compositeTypes, primitiveTypes, enumTypes, allDocTypes }: FieldBuilderProps) {
  const uid = useId();
  const { data: tagRegistry } = useTagRegistry();
  const reg: FieldRegistries = { compositeTypes, primitiveTypes, enumTypes, allDocTypes, tagRegistry };

  const [justAdded, setJustAdded] = useState<string | null>(null);
  const add = () => {
    const field = withFieldUid({ key: '', title: '', type: 'string', required: false });
    onChange([...fields, field]);
    setOpenIndex(fields.length);          // без раскрытой карточки ставить курсор некуда
    setJustAdded(field[FIELD_UID]);
  };
  // Раскрытие карточки (одна за раз) и drag-and-drop переупорядочивания (issue #197 Фаза B).
  const [openIndex, setOpenIndex] = useState<number | null>(null);
  const [dragIndex, setDragIndex] = useState<number | null>(null);
  const move = (from: number, to: number) => {
    const next = moveItem(fields, from, to);
    if (next === fields) return;
    onChange(next);
    // Раскрытая карточка едет вместе со своим полем. Пересчитывать надо ВСЕ индексы между from и to,
    // а не только сам перемещаемый: перетаскивание здесь через несколько позиций, и соседи сдвигаются
    // (issue #518). Прежнее «o === from ? to : o» было верно лишь для обмена соседей: перетащив A
    // на место D при раскрытой B, пользователь видел раскрытой уже C.
    setOpenIndex(o => {
      if (o === null) return o;
      if (o === from) return to;
      if (from < o && o <= to) return o - 1;
      if (to <= o && o < from) return o + 1;
      return o;
    });
  };

  return (
    <div className="space-y-2">
      {fields.map((field, i) => (
        <FieldCard
          key={field[FIELD_UID] ?? `${uid}-${i}`}
          field={field}
          reg={reg}
          keyConflict={!!field.key && !!disabledKeys?.has(field.key.trim())}
          persistedKeys={persistedKeys}
          autoFocus={!!justAdded && field[FIELD_UID] === justAdded}
          onAutoFocused={() => setJustAdded(null)}
          siblingFields={fields.filter((_, idx) => idx !== i).map(f => ({ key: f.key, title: f.title, type: f.type, computed: f.computed, expression: f.expression }))}
          otherKeys={[...(disabledKeys ?? []), ...fields.filter((_, idx) => idx !== i).map(f => f.key)]}
          open={openIndex === i}
          onToggleOpen={() => setOpenIndex(o => o === i ? null : i)}
          onChange={patch => onChange(fields.map((f, idx) => idx === i ? { ...f, ...patch } : f))}
          onRemove={() => onChange(fields.filter((_, idx) => idx !== i))}
          onMoveUp={() => move(i, i - 1)}
          onMoveDown={() => move(i, i + 1)}
          isFirst={i === 0}
          isLast={i === fields.length - 1}
          dragging={dragIndex === i}
          onDragStart={() => setDragIndex(i)}
          onDragEnd={() => setDragIndex(null)}
          onDragOver={dragIndex !== null ? e => e.preventDefault() : undefined}
          onDrop={dragIndex !== null ? () => { move(dragIndex, i); setDragIndex(null); } : undefined}
        />
      ))}
      <Button type="button" variant="tonal" onClick={add} icon={<Icon icon={Add} size={18} />} className="w-full justify-center">
        Добавить поле
      </Button>
    </div>
  );
}


// ─── Default value cell (module-level to avoid remount on each render) ────────

export function DefaultValueCell({ field, override, enumTypes, onOverrideDefaultValue }: {
  field: SchemaField;
  override?: { required?: boolean; defaultValue?: unknown };
  enumTypes: EnumTypeDef[];
  onOverrideDefaultValue: (key: string, value: unknown) => void;
}) {
  const isPrimitive = field.type !== 'complex' && field.type !== 'array' && field.type !== 'primitive';
  if (!isPrimitive) return <span className="text-body-small text-on-surface-variant">—</span>;

  const cur = override?.defaultValue;
  const hasDv = cur !== undefined;
  const parentDv = field.defaultValue;
  const inputCls = 'w-full border rounded-xs px-1.5 py-0.5 text-body-small bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-primary ' + (hasDv ? 'border-primary-container' : 'border-outline-variant');
  // Своя рамка у переопределённого значения сильнее рамки Select: иначе победил бы порядок правил в CSS.
  const selectCls = hasDv ? 'border-primary-container!' : '';

  if (field.type === 'boolean') {
    return (
      <Select size="sm" className={selectCls} aria-label={`По умолчанию: ${field.title || field.key}`}
        value={hasDv ? String(cur) : ''} onValueChange={v => onOverrideDefaultValue(field.key, v === '' ? undefined : v === 'true')}
        emptyItem={parentDv !== undefined ? String(parentDv) : 'не задано'}>
        <SelectItem value="true">true</SelectItem>
        <SelectItem value="false">false</SelectItem>
      </Select>
    );
  }
  if (field.type === 'enum') {
    const enumTypeDef = field.typeId ? enumTypes.find(et => et.id === field.typeId) : undefined;
    if (field.typeId && !enumTypeDef) return <span className="text-body-small text-on-surface-variant">—</span>;
    return (
      <Select size="sm" className={selectCls} aria-label={`По умолчанию: ${field.title || field.key}`}
        value={hasDv ? String(cur) : ''} onValueChange={v => onOverrideDefaultValue(field.key, v || undefined)}
        emptyItem={parentDv !== undefined ? String(parentDv) : 'не задано'}>
        {enumTypeDef
          ? enumTypeDef.values.map(v => <SelectItem key={v.code} value={v.code}>{v.label}</SelectItem>)
          : (field.options ?? []).filter(o => o).map(o => <SelectItem key={o} value={o}>{o}</SelectItem>)}
      </Select>
    );
  }
  if (field.type === 'date') {
    return (
      <DateInput label={`По умолчанию: ${field.title || field.key}`}
        value={hasDv ? String(cur) : ''}
        onChange={v => onOverrideDefaultValue(field.key, v || undefined)}
        size="sm" className={`w-full ${selectCls}`}
      />
    );
  }
  return (
    <input
      type={field.type === 'number' ? 'number' : 'text'}
      value={hasDv ? String(cur) : ''}
      placeholder={parentDv !== undefined ? String(parentDv) : 'от родит.'}
      onChange={e => {
        const v = e.target.value;
        onOverrideDefaultValue(field.key,
          v === '' ? undefined
          : field.type === 'number' ? Number(v) : v,
        );
      }}
      className={inputCls}
    />
  );
}

// ─── Inherited fields panel ────────────────────────────────────────────────────


