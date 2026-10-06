import { useState, type ReactNode } from 'react';
import { useSearchParams } from 'react-router';
import { Plus, Search, ChevronDown, ChevronUp, Database, FileText, Layers, X, Building2, Pencil, Copy, Check } from 'lucide-react';
import { toggleInSet } from '@/shared/utils/toggleInSet';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { SearchInput } from '@/shared/ui/SearchInput';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import {
  useListCommonData, useDeleteCommonDataEntry, useCommonDataForScope, useCommonDataEntry, archiveOffered,
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

  // Выбранный тип — в URL (?type=id), deep-link/back-forward. Пустой = «Все записи».
  const [searchParams, setSearchParams] = useSearchParams();
  const filterTypeId = searchParams.get('type') ?? '';
  const setFilterTypeId = (id: string) => setSearchParams(prev => {
    const next = new URLSearchParams(prev);
    if (id) next.set('type', id); else next.delete('type');
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

  const compositeTypes = allDocTypes.filter(dt => dt.kind === 'Composite');
  const documentTypes = allDocTypes.filter(dt => dt.kind === 'Document' && !dt.isAbstract);
  const allSelectableTypes = [...compositeTypes, ...documentTypes];
  const isDocType = (id: string) => documentTypes.some(dt => dt.id === id);
  // Архив (issue #1185): действие — тем, кто ведёт общие данные, и только записям ядра.
  const canEdit = useCan().permission('core.catalog.edit');
  // Типы — ВСЕ, а не только выбираемые: у записи абстрактного или чужого типа владелец тот же вопрос.
  const archive = useRecordArchive(allDocTypes);
  const archiveAction = (e: CommonDataEntry) =>
    canEdit && archive.allowed(e) ? (target: CommonDataEntry) => void archive.act(target, !target.archived) : undefined;

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

  // При поиске группы раскрыты все (ревью PR #1226): совпадение в свёрнутой группе — тем более
  // архивное, которого нет и в её счётчике, — читалось бы как «такой записи нет».
  const searching = !!search.trim();
  const row = (entry: CommonDataEntry, siblings: CommonDataEntry[], border: boolean) => (
    <ObjectRow key={entry.id} entry={entry} siblings={siblings} resolvePool={scopeChain}
      onEdit={setEditEntry} onDelete={setDeleteTarget} deleteDisabled={deleteMutation.isPending}
      onArchive={archiveAction(entry)} archiveDisabled={archive.pending}
      showPreview className={border ? 'border-t border-muted' : ''} />
  );
  // Список одной группы: действующие, под ними — свёрнутое «В архиве: N». При поиске архив раскрыт:
  // совпавшая архивная запись обязана быть на виду, иначе её заведут второй раз.
  const rows = (items: CommonDataEntry[]) => {
    const live = items.filter(isLive);
    const gone = items.filter(e => e.archived);
    return (
      <>
        {live.length === 0 && <p className="px-4 py-3 text-sm text-fg4">Действующих записей нет.</p>}
        {live.map((e, idx) => row(e, items, idx > 0))}
        <ArchivedRows count={gone.length} forceOpen={searching}>
          {gone.map(e => row(e, items, true))}
        </ArchivedRows>
      </>
    );
  };
  // Отказ в удалении: есть ли у человека другой путь. Занятую запись предлагаем отправить в архив —
  // по полю ответа; у записи, которая уже там, выхода нет и не нужно — так и говорим.
  const refusalExit = (e: unknown, target: CommonDataEntry) => {
    if (!isConflict(e)) return null;
    if (target.archived)
      return { note: 'Запись уже в архиве: в списках выбора её нет. Больше ничего делать не нужно.' };
    if (!canEdit || !archiveOffered(e)) return null;
    return {
      note: 'Запись можно отправить в архив: из списков выбора она пропадёт, а в уже сохранённых документах и счетах останется.',
      action: {
        label: 'Отправить в архив', errorTitle: 'Не удалось отправить в архив',
        onConfirm: () => archive.set(target, true),
      },
    };
  };

  return (
    <div className="flex gap-5 items-start">
      {/* Рейл типов */}
      <aside className="w-48 shrink-0 sticky top-0 self-start space-y-0.5">
        {rail.groups.length > TYPE_SEARCH_THRESHOLD && (
          <div className="relative mb-1">
            <Search size={13} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg4 pointer-events-none" />
            <input value={typeSearch} onChange={e => setTypeSearch(e.target.value)} placeholder="Тип…" aria-label="Поиск типа"
              className="w-full h-8 pl-7 pr-6 rounded-md text-xs bg-surface border border-stroke text-fg1 outline-none focus-visible:ring-2 focus-visible:ring-brand placeholder:text-fg4" />
            {typeSearch && (
              <button onClick={() => setTypeSearch('')} aria-label="Очистить" className="absolute right-1.5 top-1/2 -translate-y-1/2 text-fg4 hover:text-fg2">
                <X size={12} />
              </button>
            )}
          </div>
        )}
        {profileType && (
          <TypeNavItem icon={<Building2 size={15} />} label={`Данные: ${SCOPE_LABELS[scope]}`}
            count={undefined} active={filterTypeId === PROFILE} profile
            onClick={() => setFilterTypeId(PROFILE)} />
        )}
        <TypeNavItem icon={<Layers size={15} />} label="Все записи" count={normalEntries.filter(isLive).length}
          active={!filterTypeId} onClick={() => setFilterTypeId('')} />
        {railGroups.map(({ type: t, items }) => (
          <TypeNavItem key={t.id}
            icon={isDocType(t.id) ? <FileText size={15} /> : <Database size={15} />}
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
          <span className="flex-1" />
          <Button variant="filled" size="sm" icon={<Plus size={16} />} onClick={() => setAddOpen(true)}>Добавить запись</Button>
        </div>

        {isLoading ? (
          <div className="text-center py-12 text-fg4 text-sm">Загрузка...</div>
        ) : entries.length === 0 ? (
          <EmptyState icon={<Database size={30} />} title="Каталог пуст"
            description="Добавьте справочные данные (организации, лица, объекты) этого уровня."
            action={<Button variant="filled" icon={<Plus size={16} />} onClick={() => setAddOpen(true)}>Добавить запись</Button>} />
        ) : filtered.length === 0 ? (
          <div className="text-center py-10 text-fg4 text-sm">Ничего не найдено</div>
        ) : filterTypeId ? (
          // Выбран один тип — плоский список без аккордеона (тип уже виден в рейле).
          <div className="border border-stroke rounded-xl overflow-hidden">
            <div className="flex items-center gap-2 px-4 py-3 bg-surface border-b border-stroke">
              <span className="flex-1 text-sm font-medium text-fg2">{selectedType?.name ?? 'Без типа'}</span>
              {selectedType && isDocType(selectedType.id) && (
                <span className="text-xs bg-warning-subtle text-warning border border-warning-border px-1.5 py-0.5 rounded-full">внеш. документ</span>
              )}
              <span className="text-xs text-fg4">{filtered.filter(isLive).length}</span>
            </div>
            <div>{rows(filtered)}</div>
          </div>
        ) : (
          // «Все записи» — группы-аккордеоны по типу.
          <div className="space-y-2">
            {groups.map(({ type: t, items }) => {
              const isOpen = searching || expandedTypes.has(t.id);
              return (
                <div key={t.id} className="border border-stroke rounded-xl overflow-hidden">
                  <button type="button" onClick={() => toggleType(t.id)} aria-expanded={isOpen}
                    className="w-full flex items-center gap-2 px-4 py-3 bg-surface hover:bg-base transition-colors text-left">
                    {isOpen ? <ChevronUp size={14} className="text-fg4 shrink-0" /> : <ChevronDown size={14} className="text-fg4 shrink-0" />}
                    <span className="flex-1 text-sm font-medium text-fg2">{t.name}</span>
                    {isDocType(t.id) && (
                      <span className="text-xs bg-warning-subtle text-warning border border-warning-border px-1.5 py-0.5 rounded-full">внеш. документ</span>
                    )}
                    <span className="text-xs text-fg4 font-mono">{t.code}</span>
                    <span className="text-xs text-fg4 ml-1">{items.filter(isLive).length}</span>
                  </button>
                  {isOpen && <div className="border-t border-stroke">{rows(items)}</div>}
                </div>
              );
            })}
            {noType.length > 0 && (() => {
              const isOpen = searching || expandedTypes.has(NO_TYPE);
              return (
                <div className="border border-stroke rounded-xl overflow-hidden">
                  <button type="button" onClick={() => toggleType(NO_TYPE)} aria-expanded={isOpen}
                    className="w-full flex items-center gap-2 px-4 py-3 bg-surface hover:bg-base transition-colors text-left">
                    {isOpen ? <ChevronUp size={14} className="text-fg4 shrink-0" /> : <ChevronDown size={14} className="text-fg4 shrink-0" />}
                    <span className="flex-1 text-sm font-medium text-fg3 italic">Без типа</span>
                    <span className="text-xs text-fg4">{noType.filter(isLive).length}</span>
                  </button>
                  {isOpen && <div className="border-t border-stroke">{rows(noType)}</div>}
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
      <Modal open={!!editEntry} onOpenChange={o => { if (!o) setEditEntry(null); }} title="Редактировать запись" wide flushBody>
        {editEntry && (
          <EditEntryForm id={editEntry.id} compositeTypes={compositeTypes} documentTypes={documentTypes}
            allDocTypes={allDocTypes} scope={scope} scopeId={scopeId} onClose={() => setEditEntry(null)}
            employee={archive.isEmployee(editEntry)} archiveBusy={archive.pending}
            onUnarchive={archiveAction(editEntry)} />
        )}
      </Modal>
      <ConfirmDialog open={!!deleteTarget} onOpenChange={o => { if (!o) setDeleteTarget(null); }}
        title={`Удалить «${deleteTarget?.displayName ?? ''}»?`} confirmLabel="Удалить"
        onConfirm={() => { if (deleteTarget) return deleteMutation.mutateAsync(deleteTarget.id); }}
        onRefused={e => (deleteTarget ? refusalExit(e, deleteTarget) : null)} />
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
      className={`w-full flex items-center gap-2 px-2.5 h-9 rounded-lg text-left transition-colors ${
        active ? 'bg-brand-subtle text-brand-hover font-medium'
          : profile ? 'text-brand-hover hover:bg-brand-subtle' : 'text-fg2 hover:bg-muted'}`}>
      {icon && <span className={`shrink-0 ${active || profile ? 'text-brand-hover' : doc ? 'text-warning' : 'text-fg4'}`}>{icon}</span>}
      <span className={`flex-1 truncate text-sm ${muted ? 'italic text-fg3' : ''}`}>{label}</span>
      {count != null && <span className="text-xs text-fg4 tabular-nums shrink-0">{count}</span>}
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
  const { data: entry, isLoading } = useCommonDataEntry(id);
  if (isLoading || !entry) {
    return <div className="p-8 text-center text-sm text-fg4">Загрузка записи…</div>;
  }
  // Архивную запись править можно; плашка объясняет состояние и даёт вернуть (issue #1185).
  return (
    <>
      {entry.archived && (
        <ArchivedBanner employee={employee} busy={archiveBusy}
          onReturn={onUnarchive && (() => onUnarchive(entry))} />
      )}
      {/* Пометки «в архиве» у плиток ссылок — по сохранённым данным записи (issue #1185). */}
      <ArchivedRefsProvider data={entry.data}>
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
        <span className="shrink-0 w-10 h-10 rounded-lg bg-brand-subtle text-brand-hover flex items-center justify-center">
          <Building2 size={18} />
        </span>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2">
            <h2 className="text-base font-semibold text-fg1">Данные: {SCOPE_LABELS[scope]}</h2>
            <span className="text-xs bg-brand-subtle text-brand-hover px-1.5 py-0.5 rounded-full">уровень</span>
          </div>
          <p className="text-xs text-fg3 mt-0.5">Тип: {type.name} · единственная запись уровня.</p>
        </div>
        <Button variant="outlined" size="sm" icon={<Pencil size={14} />} onClick={onEdit} disabled={!object}>
          Редактировать
        </Button>
      </div>

      <div className="rounded-lg border border-stroke bg-surface p-3 mb-3">
        <p className="text-xs text-fg2">
          Эти данные доступны <span className="font-medium">во всех документах уровня</span> — в шаблоне через ключ:
        </p>
        <div className="flex items-center gap-2 mt-1.5">
          <code className="text-xs font-mono bg-muted text-fg1 px-2 py-1 rounded">data.{key}.*</code>
          <button type="button" onClick={copy} title="Скопировать ключ"
            className="text-fg4 hover:text-brand transition-colors">
            {copied ? <Check size={14} className="text-success" /> : <Copy size={14} />}
          </button>
        </div>
      </div>

      {preview.length > 0 ? (
        <div className="rounded-lg border border-stroke divide-y divide-muted">
          {preview.map(([k, v]) => (
            <div key={k} className="flex items-center gap-3 px-3 py-2 text-sm">
              <span className="text-fg3 min-w-[10rem] shrink-0">{k}</span>
              <span className="text-fg1 truncate">{String(v)}</span>
            </div>
          ))}
        </div>
      ) : (
        <p className="text-sm text-fg4 px-1 py-4">
          Профиль пуст. Нажмите «Редактировать», чтобы заполнить данные уровня.
        </p>
      )}
    </div>
  );
}
