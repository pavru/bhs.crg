import { useState, type ReactNode } from 'react';
import { useSearchParams } from 'react-router';
import { Icon } from '@/shared/ui/Icon';
import { Add, Apartment, Check, Close, ContentCopy, Database, Description, Edit, KeyboardArrowDown, KeyboardArrowUp, Layers, Search } from '@/shared/ui/icons';
import { toggleInSet } from '@/shared/utils/toggleInSet';
import { Modal } from '@/shared/ui/Modal';
import { Button, IconButton } from '@/shared/ui/Button';
import { SearchInput } from '@/shared/ui/SearchInput';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ConfirmDialog, type RefusalExit } from '@/shared/ui/ConfirmDialog';
import { purgeOffered, type PurgeOffer } from '@/shared/api/recordPurge';
import { PurgeRecordDialog } from './PurgeRecordDialog';
import { PickRow } from './PickRow';
import { usePurgeBatchControls } from './usePurgeBatchControls';
import {
  useListCommonData, useDeleteCommonDataEntry, useCommonDataForScope, useCommonDataEntry, archiveOffered,
  ownDoorCode, recordWritePermission,
} from '@/shared/api/commonData';
import { useCan } from '@/shared/api/access';
import { isConflict } from '@/shared/utils/apiError';
import type { CommonDataEntry, CatalogScope, DocumentType } from '@/shared/api/types';
import { SCOPE_LABELS } from '@/shared/api/types';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import { CatalogEntryForm } from './index';
import { ArchivedRefsProvider } from '../fields';
import { ObjectRow } from './ObjectsByTypeList';
import { ArchivedRows } from '@/shared/ui/ArchivedRows';
import { ArchivedBanner } from './ArchiveParts';
import { useRecordArchive } from './useRecordArchive';
import { groupObjectsByType, entryMatchesQuery } from './objectsByType';
import { Tooltip } from '@/shared/ui/Tooltip';
import { CATALOG_RECORD } from '@/shared/ui/recordRoutes';
import { StatusBadge } from '@/shared/ui/StatusBadge';

const NO_TYPE = '__no_type__';
const PROFILE = '__profile__';
/** Тэг профиль-типа и ключ в шаблоне (data.уровень.<key>) по уровню (issue #258). System — нет профиля. */
const PROFILE_TAG: Partial<Record<CatalogScope, string>> = {
  Construction: FUNCTIONAL_TAG.profileConstruction,
  Section: FUNCTIONAL_TAG.profileSection,
  Set: FUNCTIONAL_TAG.profileSet,
};
const PROFILE_KEY: Partial<Record<CatalogScope, string>> = {
  Construction: 'стройка', Section: 'раздел', Set: 'комплект',
};
/** Порог, с которого над списком типов появляется мини-поиск (NN/g: фасеты с поиском при большом числе). */
const TYPE_SEARCH_THRESHOLD = 12;
const isLive = (e: CommonDataEntry) => !e.archived;

/**
 * Богатый браузер каталога общих данных для ЛЮБОГО scope (issue #210, ось видимости): слева — вертикальный
 * рейл типов со счётчиками («Все записи» + типы, мини-поиск при большом числе; вариант B — рейл живёт ВНУТРИ
 * компонента, один на всех 4 уровнях), справа — поиск + записи (все группами / выбранный тип плоско).
 * Выбранный тип — в URL (`?type=id`, deep-link). Область выражается положением, заголовок даёт вызывающий.
 */
export function CatalogResource({ scope, scopeId, allDocTypes }: {
  scope: CatalogScope; scopeId: string | null; allDocTypes: DocumentType[];
}) {
  const [addOpen, setAddOpen] = useState(false);
  const [editEntry, setEditEntry] = useState<CommonDataEntry | null>(null);
  const [search, setSearch] = useState('');
  const [typeSearch, setTypeSearch] = useState('');
  const [expandedTypes, setExpandedTypes] = useState<Set<string>>(new Set());
  const [deleteTarget, setDeleteTarget] = useState<CommonDataEntry | null>(null);
  const [purging, setPurging] = useState<{ target: CommonDataEntry; offer: PurgeOffer } | null>(null);

  // Выбранный тип — в URL (?type=id), deep-link/back-forward. Пустой = «Все записи».
  const [searchParams, setSearchParams] = useSearchParams();
  const filterTypeId = searchParams.get('type') ?? '';
  const setFilterTypeId = (id: string) => setSearchParams(prev => {
    const next = new URLSearchParams(prev);
    if (id) next.set('type', id); else next.delete('type');
    return next;
  }, { replace: true });

  // Запись, названная адресом (`?entry=id`, issue #1517): её открывает «Открыть в справочнике» из карточки
  // позиции в окне выбора. Закрыли окно — параметр снят, иначе обновление страницы открыло бы её снова.
  const linkedId = searchParams.get(CATALOG_RECORD.param);
  const unlink = () => setSearchParams(prev => {
    const next = new URLSearchParams(prev);
    next.delete(CATALOG_RECORD.param);
    return next;
  }, { replace: true });

  function toggleType(id: string) {
    setExpandedTypes(prev => toggleInSet(prev, id));
  }

  // Страница справочника — показ: архивные записи на месте (issue #1185).
  const { data: entries = [], isLoading } = useListCommonData({ purpose: 'display', scope, scopeId: scopeId ?? undefined });
  // Пул для резолва прокси-цели: вся scope-цепочка (текущий уровень + предки) — кросс-scope прокси (#89).
  // Показ: цель уже стоящей ссылки обязана найтись и тогда, когда она в архиве.
  const { data: scopeChain = [] } = useCommonDataForScope({ scope, scopeId, purpose: 'display' });
  const deleteMutation = useDeleteCommonDataEntry();
  const linked = linkedId ? entries.find(e => e.id === linkedId) ?? null : null;
  const shownEntry = editEntry ?? linked;

  const compositeTypes = allDocTypes.filter(dt => dt.kind === 'Composite');
  const documentTypes = allDocTypes.filter(dt => dt.kind === 'Document' && !dt.isAbstract);
  const allSelectableTypes = [...compositeTypes, ...documentTypes];
  const isDocType = (id: string) => documentTypes.some(dt => dt.id === id);
  // Архив (issue #1185): действие — тем, кто ведёт записи этого типа, и только записям ядра. Право — по
  // типу записи: виды работ ведут правом на классификатор, а не правкой общих данных (WORK-59).
  const can = useCan();
  const doorOf = (e: CommonDataEntry) => ownDoorCode(e.compositeTypeId, allDocTypes);
  const canEdit = (e: CommonDataEntry) => can.permission(recordWritePermission(doorOf(e)));
  // Типы — ВСЕ, а не только выбираемые: у записи абстрактного или чужого типа владелец тот же вопрос.
  const archive = useRecordArchive(allDocTypes);
  const archiveAction = (e: CommonDataEntry) =>
    canEdit(e) && archive.allowed(e) ? (target: CommonDataEntry) => void archive.act(target, !target.archived) : undefined;

  // Профиль уровня (issue #258): составной тип, помеченный тэгом profile-* для этого scope, — его
  // единственный объект здесь несёт «данные уровня», амбиентно попадающие в шаблон (data.уровень.<key>).
  const profileTag = PROFILE_TAG[scope];
  const profileKey = PROFILE_KEY[scope];
  const profileType = profileTag
    ? compositeTypes.find(t => (((t.schema as { tags?: string[] }).tags) ?? []).includes(profileTag))
    : undefined;
  // Только действующая (ревью PR #1226): архивная запись профиль-типа профилем не назначается, и
  // взять её здесь значило бы показывать и править под «Данные уровня» не тот объект.
  const profileObject = profileType ? entries.find(e => e.compositeTypeId === profileType.id && isLive(e)) : undefined;
  // Профиль не смешиваем с обычными записями (рейл/список/«Все записи»).
  const normalEntries = profileObject ? entries.filter(e => e.id !== profileObject.id) : entries;

  // Рейл типов: счётчики по всем записям уровня (независимо от текстового поиска и выбранного типа).
  const rail = groupObjectsByType(normalEntries, allSelectableTypes);
  const tq = typeSearch.trim().toLowerCase();
  const railGroups = tq ? rail.groups.filter(g => g.type.name.toLowerCase().includes(tq)) : rail.groups;

  const selectedType = filterTypeId && filterTypeId !== NO_TYPE
    ? allSelectableTypes.find(t => t.id === filterTypeId) : undefined;

  // Записи справа: фильтр по тексту + выбранному типу.
  const matchType = (e: CommonDataEntry) =>
    !filterTypeId ? true
      : filterTypeId === NO_TYPE ? !allSelectableTypes.some(t => t.id === e.compositeTypeId)
        : e.compositeTypeId === filterTypeId;
  // Текстовый матч комплексный (issue #249): имя записи + имя типа + значения скалярных полей — см.
  // entryMatchesQuery. Раньше искали только по displayName (искали «орга» → тип «Организация» не находился).
  const matchText = (e: CommonDataEntry) =>
    entryMatchesQuery(e, allSelectableTypes.find(t => t.id === e.compositeTypeId)?.name, search);
  const filtered = normalEntries.filter(e => matchText(e) && matchType(e))
    .sort((a, b) => {
      const typeCmp = (allSelectableTypes.find(t => t.id === a.compositeTypeId)?.name ?? '').localeCompare(
        allSelectableTypes.find(t => t.id === b.compositeTypeId)?.name ?? '');
      return typeCmp !== 0 ? typeCmp : a.displayName.localeCompare(b.displayName);
    });
  const { groups, noType } = groupObjectsByType(filtered, allSelectableTypes);

  // Пакетная очистка (issue #1245): меню типа, режим выбора и диалог — всё в своём файле.
  const batch = usePurgeBatchControls({ scope, scopeId, type: selectedType });

  // При поиске группы раскрыты все (ревью PR #1226): совпадение в свёрнутой группе — тем более
  // архивное, которого нет и в её счётчике, — читалось бы как «такой записи нет».
  const searching = !!search.trim();
  const row = (entry: CommonDataEntry, siblings: CommonDataEntry[], border: boolean) => batch.picked ? (
    <PickRow key={entry.id} entry={entry} checked={batch.picked.has(entry.id)}
      onToggle={() => batch.toggle(entry.id)} className={border ? 'border-t border-surface-container' : ''} />
  ) : (
    <ObjectRow key={entry.id} entry={entry} siblings={siblings} resolvePool={scopeChain}
      onEdit={setEditEntry} onDelete={canEdit(entry) ? setDeleteTarget : undefined} deleteDisabled={deleteMutation.isPending}
      onArchive={archiveAction(entry)} archiveDisabled={archive.pending}
      showPreview className={border ? 'border-t border-surface-container' : ''} />
  );
  // Список одной группы: действующие, под ними — свёрнутое «В архиве: N». При поиске архив раскрыт:
  // совпавшая архивная запись обязана быть на виду, иначе её заведут второй раз.
  const rows = (items: CommonDataEntry[]) => {
    const live = items.filter(isLive);
    const gone = items.filter(e => e.archived);
    return (
      <>
        {live.length === 0 && <p className="px-4 py-3 text-body-medium text-on-surface-variant">Действующих записей нет.</p>}
        {live.map((e, idx) => row(e, items, idx > 0))}
        <ArchivedRows count={gone.length} forceOpen={searching || !!batch.picked}>
          {gone.map(e => row(e, items, true))}
        </ArchivedRows>
      </>
    );
  };
  // Отказ в удалении: есть ли у человека другой путь. Занятую запись предлагаем отправить в архив —
  // по полю ответа; у записи, которая уже там, выхода нет и не нужно — так и говорим.
  const refusalExit = (e: unknown, target: CommonDataEntry): RefusalExit | null => {
    if (!isConflict(e)) return null;
    const secondary = purgeExit(e, target);
    if (target.archived)
      return { note: 'Запись уже в архиве: в списках выбора её нет. Больше ничего делать не нужно.', secondary };
    if (!canEdit(target) || !archiveOffered(e)) return secondary ? { note: null, secondary } : null;
    return {
      note: 'Запись можно отправить в архив: из списков выбора она пропадёт, а в уже сохранённых документах и счетах останется.',
      action: {
        label: 'Отправить в архив', errorTitle: 'Не удалось отправить в архив',
        onConfirm: () => archive.set(target, true),
      },
      secondary,
    };
  };
  // Жёсткий выход (issue #1187): запись держат только данные выключенного модуля, и убрать ссылку
  // негде. По ПОЛЮ ответа, как и архив. Права нет — строка без кнопки: выход есть, и молчать о нём
  // нельзя, а кнопка привела бы к отказу.
  const purgeExit = (e: unknown, target: CommonDataEntry): RefusalExit['secondary'] => {
    const offer = purgeOffered(e);
    if (!offer) return undefined;
    if (!offer.allowed) return { note: 'Удалить запись, потеряв ссылки, может администратор.' };
    return {
      note: 'Запись можно удалить совсем — ссылки будут потеряны.',
      action: {
        label: 'Удалить, потеряв ссылки…',
        // Не диалог поверх диалога: отказ закрывается, у жёсткого выхода своё окно и своё подтверждение.
        onClick: () => { setDeleteTarget(null); setPurging({ target, offer }); },
      },
    };
  };

  return (
    <div className="flex gap-5 items-start">
      {/* Рейл типов */}
      <aside className="w-48 shrink-0 sticky top-0 self-start space-y-0.5">
        {rail.groups.length > TYPE_SEARCH_THRESHOLD && (
          <div className="relative mb-1">
            <Icon icon={Search} size={18} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-on-surface-variant pointer-events-none" />
            <input value={typeSearch} onChange={e => setTypeSearch(e.target.value)} placeholder="Тип…" aria-label="Поиск типа"
              className="w-full h-8 pl-7 pr-9 rounded-sm text-body-small bg-surface border border-outline-variant text-on-surface outline-none focus-visible:ring-2 focus-visible:ring-primary placeholder:text-on-surface-variant" />
            {typeSearch && (
              <IconButton label="Очистить"
                className="absolute right-0 top-1/2 -translate-y-1/2"
                onClick={() => setTypeSearch('')} icon={Close} />
            )}
          </div>
        )}
        {profileType && (
          <TypeNavItem icon={<Icon icon={Apartment} size={18} />} label={`Данные: ${SCOPE_LABELS[scope]}`}
            count={undefined} active={filterTypeId === PROFILE} profile
            onClick={() => setFilterTypeId(PROFILE)} />
        )}
        <TypeNavItem icon={<Icon icon={Layers} size={18} />} label="Все записи" count={normalEntries.filter(isLive).length}
          active={!filterTypeId} onClick={() => setFilterTypeId('')} />
        {railGroups.map(({ type: t, items }) => (
          <TypeNavItem key={t.id}
            icon={isDocType(t.id) ? <Icon icon={Description} size={18} /> : <Icon icon={Database} size={18} />}
            label={t.name} count={items.filter(isLive).length} doc={isDocType(t.id)}
            active={filterTypeId === t.id} onClick={() => setFilterTypeId(t.id)} />
        ))}
        {rail.noType.length > 0 && !tq && (
          <TypeNavItem label="Без типа" count={rail.noType.filter(isLive).length} muted
            active={filterTypeId === NO_TYPE} onClick={() => setFilterTypeId(NO_TYPE)} />
        )}
      </aside>

      {/* Записи / профиль уровня */}
      <div className="flex-1 min-w-0">
        {filterTypeId === PROFILE && profileType ? (
          <ProfileDetail scope={scope} type={profileType} object={profileObject} templateKey={profileKey!}
            onEdit={() => profileObject && setEditEntry(profileObject)} />
        ) : (
        <>
        <div className="flex items-center gap-3 mb-4">
          <div className="flex-1 max-w-sm">
            <SearchInput value={search} onChange={setSearch}
              placeholder={selectedType ? `Поиск: ${selectedType.name}…` : 'Поиск по каталогу…'} />
          </div>
          {batch.picked ? batch.bar(filtered.map(e => e.id)) : (
            <>
              <span className="flex-1" />
              <Button variant="filled" icon={<Icon icon={Add} size={18} />} onClick={() => setAddOpen(true)}>Добавить запись</Button>
            </>
          )}
        </div>

        {/* Ссылка вела на запись, которой здесь нет, — сказать, а не молча показать список. */}
        {linkedId && !isLoading && !linked && (
          <p className="mb-3 text-body-small text-warning">
            Записи из ссылки на этом уровне нет: её могли удалить.{' '}
            <button type="button" className="underline" onClick={unlink}>Скрыть</button>
          </p>
        )}

        {isLoading ? (
          <div className="text-center py-12 text-on-surface-variant text-body-medium">Загрузка...</div>
        ) : entries.length === 0 ? (
          <EmptyState icon={<Icon icon={Database} size={40} />} title="Каталог пуст"
            description="Добавьте справочные данные (организации, лица, объекты) этого уровня."
            action={<Button variant="filled" icon={<Icon icon={Add} size={18} />} onClick={() => setAddOpen(true)}>Добавить запись</Button>} />
        ) : filtered.length === 0 ? (
          <div className="text-center py-10 text-on-surface-variant text-body-medium">Ничего не найдено</div>
        ) : filterTypeId ? (
          // Выбран один тип — плоский список без аккордеона (тип уже виден в рейле).
          <div className="border border-outline-variant rounded-lg overflow-hidden">
            <div className="flex items-center gap-2 px-4 py-3 bg-surface border-b border-outline-variant">
              <span className="flex-1 text-body-medium font-medium text-on-surface-variant">{selectedType?.name ?? 'Без типа'}</span>
              {selectedType && isDocType(selectedType.id) && (
                <StatusBadge>внеш. документ</StatusBadge>
              )}
              <span className="text-body-small text-on-surface-variant">{filtered.filter(isLive).length}</span>
              {batch.menu}
            </div>
            <div>{rows(filtered)}</div>
          </div>
        ) : (
          // «Все записи» — группы-аккордеоны по типу.
          <div className="space-y-2">
            {groups.map(({ type: t, items }) => {
              const isOpen = searching || expandedTypes.has(t.id);
              return (
                <div key={t.id} className="border border-outline-variant rounded-lg overflow-hidden">
                  <button type="button" onClick={() => toggleType(t.id)} aria-expanded={isOpen}
                    className="w-full flex items-center gap-2 px-4 py-3 bg-surface state-layer text-left">
                    {isOpen ? <Icon icon={KeyboardArrowUp} size={18} className="text-on-surface-variant shrink-0" /> : <Icon icon={KeyboardArrowDown} size={18} className="text-on-surface-variant shrink-0" />}
                    <span className="flex-1 text-body-medium font-medium text-on-surface-variant">{t.name}</span>
                    {isDocType(t.id) && (
                      <StatusBadge>внеш. документ</StatusBadge>
                    )}
                    <span className="text-body-small text-on-surface-variant font-mono">{t.code}</span>
                    <span className="text-body-small text-on-surface-variant ml-1">{items.filter(isLive).length}</span>
                  </button>
                  {isOpen && <div className="border-t border-outline-variant">{rows(items)}</div>}
                </div>
              );
            })}
            {noType.length > 0 && (() => {
              const isOpen = searching || expandedTypes.has(NO_TYPE);
              return (
                <div className="border border-outline-variant rounded-lg overflow-hidden">
                  <button type="button" onClick={() => toggleType(NO_TYPE)} aria-expanded={isOpen}
                    className="w-full flex items-center gap-2 px-4 py-3 bg-surface state-layer text-left">
                    {isOpen ? <Icon icon={KeyboardArrowUp} size={18} className="text-on-surface-variant shrink-0" /> : <Icon icon={KeyboardArrowDown} size={18} className="text-on-surface-variant shrink-0" />}
                    <span className="flex-1 text-body-medium font-medium text-on-surface-variant italic">Без типа</span>
                    <span className="text-body-small text-on-surface-variant">{noType.filter(isLive).length}</span>
                  </button>
                  {isOpen && <div className="border-t border-outline-variant">{rows(noType)}</div>}
                </div>
              );
            })()}
          </div>
        )}
        </>
        )}
      </div>

      <Modal open={addOpen} onOpenChange={setAddOpen} title="Новая запись" wide flushBody>
        {addOpen && (
          <CatalogEntryForm entry={null} compositeTypes={compositeTypes} documentTypes={documentTypes}
            allDocTypes={allDocTypes} scope={scope} scopeId={scopeId} onClose={() => setAddOpen(false)} />
        )}
      </Modal>
      <Modal open={!!shownEntry} onOpenChange={o => { if (!o) { setEditEntry(null); unlink(); } }} title="Редактировать запись" wide flushBody>
        {shownEntry && (
          <EditEntryForm id={shownEntry.id} compositeTypes={compositeTypes} documentTypes={documentTypes}
            allDocTypes={allDocTypes} scope={scope} scopeId={scopeId} onClose={() => { setEditEntry(null); unlink(); }}
            employee={archive.isEmployee(shownEntry)} archiveBusy={archive.pending}
            onUnarchive={archiveAction(shownEntry)} />
        )}
      </Modal>
      <ConfirmDialog open={!!deleteTarget} onOpenChange={o => { if (!o) setDeleteTarget(null); }}
        title={`Удалить «${deleteTarget?.displayName ?? ''}»?`} confirmLabel="Удалить"
        onConfirm={() => {
          if (deleteTarget)
            return deleteMutation.mutateAsync({ id: deleteTarget.id, doorCode: doorOf(deleteTarget), version: deleteTarget.version });
        }}
        onRefused={e => (deleteTarget ? refusalExit(e, deleteTarget) : null)} />
      {batch.dialog}
      {purging && (
        <PurgeRecordDialog target={purging.target} offer={purging.offer} onClose={() => setPurging(null)} />
      )}
    </div>
  );
}

/** Пункт вертикального рейла типов (issue #210, вариант B). Активный — заливка brand-subtle;
 *  документные типы (внешний каталог) — приглушённая иконка; счётчик tabular. */
function TypeNavItem({ icon, label, count, active, doc, muted, profile, onClick }: {
  icon?: ReactNode; label: string; count?: number; active?: boolean; doc?: boolean; muted?: boolean;
  profile?: boolean; onClick: () => void;
}) {
  return (
    <button type="button" onClick={onClick} aria-current={active ? 'true' : undefined}
      className={`w-full flex items-center gap-2 px-2.5 h-9 rounded-md text-left ${
        active ? 'bg-primary-container text-on-primary-container font-medium'
          : profile ? 'text-primary hover:bg-primary-container' : 'text-on-surface-variant state-layer'}`}>
      {icon && <span className={`shrink-0 ${active || profile ? 'text-primary' : doc ? 'text-warning' : 'text-on-surface-variant'}`}>{icon}</span>}
      <span className={`flex-1 truncate text-body-medium ${muted ? 'italic text-on-surface-variant' : ''}`}>{label}</span>
      {count != null && <span className="text-body-small text-on-surface-variant tabular-nums shrink-0">{count}</span>}
    </button>
  );
}

/** Detail профиля уровня (issue #258): бейдж «уровень», объяснитель «данные во всех документах»,
 *  ключ доступа в шаблоне со «Скопировать», превью полей + «Редактировать» (открывает форму записи). */
/**
 * Редактирование записи — по ПОЛНОЙ записи из `/{id}`, а не по той, что пришла со списком
 * (issue #520). Списочный ответ отдаётся без тяжёлой полезной нагрузки картинок, а форма засевает
 * свои значения из `entry.data` и отправляет их обратно при сохранении: открой мы её на списочной
 * копии — сохранение стёрло бы печати и факсимиле.
 */
function EditEntryForm({ id, onClose, employee, archiveBusy, onUnarchive, ...rest }: {
  id: string;
  employee: boolean;
  archiveBusy: boolean;
  onUnarchive?: (e: CommonDataEntry) => void;
  compositeTypes: DocumentType[];
  documentTypes: DocumentType[];
  allDocTypes: DocumentType[];
  scope: CatalogScope;
  scopeId: string | null;
  onClose: () => void;
}) {
  // Форма собирается только по ответу, пришедшему ПОСЛЕ открытия (ревью PR #1231): копия из кэша
  // живёт минуту и отдаётся сразу, а форма засевает из неё и значения, и версию. Собранная по
  // устаревшей копии, она показывала бы прежнее содержимое и на первое же сохранение получала
  // отказ «запись изменили» — хотя одновременной правки не было.
  const { data: entry, isLoading, isFetchedAfterMount } = useCommonDataEntry(id, true);
  if (isLoading || !entry || !isFetchedAfterMount) {
    return <div className="p-8 text-center text-body-medium text-on-surface-variant">Загрузка записи…</div>;
  }
  // Архивную запись править можно; плашка объясняет состояние и даёт вернуть (issue #1185).
  return (
    <>
      {entry.archived && (
        <ArchivedBanner employee={employee} busy={archiveBusy}
          onReturn={onUnarchive && (() => onUnarchive(entry))} />
      )}
      {/* Пометки «в архиве» у плиток ссылок — по сохранённым данным записи (issue #1185). */}
      <ArchivedRefsProvider data={entry.data} ownerId={entry.id}>
        <CatalogEntryForm entry={entry} onClose={onClose} {...rest} />
      </ArchivedRefsProvider>
    </>
  );
}

function ProfileDetail({ scope, type, object, templateKey, onEdit }: {
  scope: CatalogScope; type: DocumentType; object?: CommonDataEntry; templateKey: string; onEdit: () => void;
}) {
  const [copied, setCopied] = useState(false);
  const key = `уровень.${templateKey}`;
  const copy = () => {
    navigator.clipboard?.writeText(key);
    setCopied(true); setTimeout(() => setCopied(false), 1500);
  };
  const preview = object ? Object.entries(object.data)
    .filter(([k, v]) => !k.startsWith('_') && v != null && v !== '' && typeof v !== 'object')
    .slice(0, 12) : [];
  return (
    <div className="max-w-2xl">
      <div className="flex items-start gap-3 mb-4">
        <span className="shrink-0 w-10 h-10 rounded-md bg-primary-container text-on-primary-container flex items-center justify-center">
          <Icon icon={Apartment} size={18} />
        </span>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2">
            <h2 className="text-body-large font-semibold text-on-surface">Данные: {SCOPE_LABELS[scope]}</h2>
            <StatusBadge tone="brand">уровень</StatusBadge>
          </div>
          <p className="text-body-small text-on-surface-variant mt-0.5">Тип: {type.name} · единственная запись уровня.</p>
        </div>
        <Button variant="outlined" icon={<Icon icon={Edit} size={18} />} onClick={onEdit} disabled={!object}>
          Редактировать
        </Button>
      </div>

      <div className="rounded-md border border-outline-variant bg-surface p-3 mb-3">
        <p className="text-body-small text-on-surface-variant">
          Эти данные доступны <span className="font-medium">во всех документах уровня</span> — в шаблоне через ключ:
        </p>
        <div className="flex items-center gap-2 mt-1.5">
          <code className="text-body-small font-mono bg-surface-container text-on-surface px-2 py-1 rounded-xs">data.{key}.*</code>
          <Tooltip content="Скопировать ключ" name>
            <button type="button" onClick={copy}
              className="text-on-surface-variant hover:text-primary transition-colors">
              {copied ? <Icon icon={Check} size={18} className="text-success" /> : <Icon icon={ContentCopy} size={18} />}
            </button>
          </Tooltip>
        </div>
      </div>

      {preview.length > 0 ? (
        <div className="rounded-md border border-outline-variant divide-y divide-surface-container">
          {preview.map(([k, v]) => (
            <div key={k} className="flex items-center gap-3 px-3 py-2 text-body-medium">
              <span className="text-on-surface-variant min-w-[10rem] shrink-0">{k}</span>
              <span className="text-on-surface truncate">{String(v)}</span>
            </div>
          ))}
        </div>
      ) : (
        <p className="text-body-medium text-on-surface-variant px-1 py-4">
          Профиль пуст. Нажмите «Редактировать», чтобы заполнить данные уровня.
        </p>
      )}
    </div>
  );
}
