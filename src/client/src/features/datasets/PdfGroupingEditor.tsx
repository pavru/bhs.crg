import { useMemo, useRef, useState } from 'react';
import { useNavigate, useParams, useLocation } from 'react-router';
import { Icon } from '@/shared/ui/Icon';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { Add, ArrowBack, Delete, Edit, KeyboardArrowDown, Refresh, Save, Table, Warning } from '@/shared/ui/icons';
import {
  useFilePages, useApplyGrouping, useRecognizeDocumentTable, useRecognizeDocument, useSetDocumentProfile,
  recognitionRefusal, type RecognitionRefusal,
} from '@/shared/api/datasets';
import { useHiddenRecognitionProfiles, useListRecognitionProfiles } from '@/shared/api/recognitionProfiles';
import { isHiddenBound, withBoundOption, type HiddenRecognitionProfile, type ProfileOption } from '@/shared/api/recognitionProfileGroups';
import type { GostGroupingGroup, GostGroupKind } from '@/shared/api/types';
import { Button, IconButton } from '@/shared/ui/Button';
import { ruCount } from '@/shared/utils/pluralize';
import { RecognitionBlockedDialog } from './RecognitionBlockedDialog';
import { GroupingSaveError, MissingPartsNotice } from './PdfGroupingNotices';
import { PageTile, PageViewer } from './PdfPageTiles';
import { newLocalId } from '@/shared/utils/localId';
import { PageHeader } from '@/shared/ui/PageHeader';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Tooltip } from '@/shared/ui/Tooltip';
import { Menu, MenuItem, MenuNote, MenuSeparator } from '@/shared/ui/Menu';
import { Chip } from '@/shared/ui/Chip';
import { Card } from '@/shared/ui/Card';
import { TextField } from '@/shared/ui/TextField';

const DEFAULT_CODE = '(без шифра)';
/** Тэги типа таблицы документа (спецификация / кабельный журнал) — распознаются и выгружаются. */
const TABLE_TAGS: { code: string; label: string }[] = [
  { code: 'gostDoc.specification', label: 'Спецификация / ведомость' },
  { code: 'gostDoc.cableJournal', label: 'Кабельный журнал' },
];
const KIND_LABEL: Record<Exclude<GostGroupKind, 'Document'>, string> = {
  Cover: 'Обложка',
  TitlePage: 'Титульный лист',
};

interface EditableGroup {
  id: string;
  kind: GostGroupKind;
  code: string;
  name: string | null;
  pageIndices: number[];
  tags: string[];
  /** Привязанный профиль распознавания (issue #410); правится точечным PUT, а не сохранением разбиения. */
  profileId: string | null;
}

/** Подпись группы для меню переноса и заголовка. */
function groupLabel(g: Pick<EditableGroup, 'kind' | 'code' | 'name'>): string {
  if (g.kind !== 'Document') return KIND_LABEL[g.kind];
  return g.name ?? g.code;
}

/** Подозрительны только группы-документы без шифра/имени; обложка/титул — никогда. */
function isSuspicious(g: Pick<EditableGroup, 'kind' | 'code' | 'name'>): boolean {
  return g.kind === 'Document' && (!g.code || g.code === DEFAULT_CODE || !g.name);
}

function makeGroups(groups: GostGroupingGroup[]): EditableGroup[] {
  const mapped = groups.map(g => ({
    id: newLocalId(),
    kind: g.kind,
    code: g.code ?? DEFAULT_CODE,
    name: g.name,
    pageIndices: [...g.pageIndices].sort((a, b) => a - b),
    tags: g.tags ?? [],
    profileId: g.profileId ?? null,
  }));
  // Обложка и титульный лист всегда присутствуют как группы (пустые — как цель для переноса).
  const ensure = (kind: Exclude<GostGroupKind, 'Document'>): EditableGroup[] =>
    mapped.some(g => g.kind === kind) ? [] : [{ id: newLocalId(), kind, code: DEFAULT_CODE, name: null, pageIndices: [], tags: [], profileId: null }];
  const cover = mapped.filter(g => g.kind === 'Cover');
  const title = mapped.filter(g => g.kind === 'TitlePage');
  const docs = mapped.filter(g => g.kind === 'Document');
  return [...cover, ...ensure('Cover'), ...title, ...ensure('TitlePage'), ...docs];
}

// ─── Панель действий над выделением (перенести в группу / отделить) ────────────────────────────

/**
 * Панель действий над выделенными страницами редактора разбиения: отделить в новый документ или
 * перенести в группу. «Перенести в группу» — меню Radix с порталом в body ([UI-51]): лента групп
 * прокручивается, и меню у нижней группы прежде раздвигало её, а не свисало поверх.
 */
export function SelectionActionBar({
  count, candidateGroups, onMoveSelected, onSplitSelected,
}: {
  count: number;
  candidateGroups: { id: string; label: string }[];
  onMoveSelected: (targetGroupId: string | 'new') => void;
  onSplitSelected: () => void;
}) {
  return (
    <div className="flex items-center gap-2 mt-2 pt-2 border-t border-outline-variant">
      <span className="text-body-small text-on-surface-variant">Выделено страниц: {count}</span>
      <Button variant="outlined" onClick={onSplitSelected}>Отделить в новый документ</Button>
      <Menu dense collisionPadding={8}
        trigger={
          <Button variant="outlined" trailingIcon={<Icon icon={KeyboardArrowDown} size={18} aria-hidden />}>
            Перенести в группу
          </Button>
        }>
        {candidateGroups.length === 0 && <MenuNote>Нет других групп</MenuNote>}
        {candidateGroups.map(g => (
          <MenuItem key={g.id} onSelect={() => onMoveSelected(g.id)}>{g.label}</MenuItem>
        ))}
        <MenuSeparator />
        <MenuItem icon={Add} onSelect={() => onMoveSelected('new')}>Новый документ</MenuItem>
      </Menu>
    </div>
  );
}

// ─── Группа (документ / обложка / титульный лист) ────────────────────────────────────────────────

/** Распознаётся СОХРАНЁННОЕ разбиение: при правках кнопка неактивна и предлагает их сохранить. На
 *  телефоне то же «Сохранить» стоит в шапке редактора. */
const unsaved = (save: () => void) => ({ title: 'Сначала сохраните разбиение',
  text: 'Распознаётся сохранённое разбиение, а у этого есть правки.', actions: [{ label: 'Сохранить', onClick: save }] });

function GroupSection({
  fileId, group, otherGroups, selected, suspiciousOnly, saveFirst, pagesWithoutAnswer,
  onToggle, onRename, onMoveSelected, onSplitSelected, onDisband, onView, onSetTag,
  onSetProfile, tableProfiles, hiddenProfiles, savingProfile,
  onRecognizeTable, onRecognizeDoc, tableBusyPage, docBusyPage,
}: {
  fileId: string;
  group: EditableGroup;
  otherGroups: EditableGroup[];
  selected: Set<number>;
  /** Листы без ответа — с сервера, один набор на весь редактор (issue #803). */
  pagesWithoutAnswer: Set<number>;
  suspiciousOnly: boolean;
  /** Есть несохранённые правки — их сохранение; `null` — правок нет. */
  saveFirst: (() => void) | null;
  onToggle: (pageIndex: number, e: React.MouseEvent) => void;
  onRename: (groupId: string, code: string, name: string | null) => void;
  onMoveSelected: (targetGroupId: string | 'new') => void;
  onSplitSelected: () => void;
  onDisband: (groupId: string) => void;
  onView: (pageIndex: number) => void;
  onSetTag: (groupId: string, tag: string) => void;
  onSetProfile: (firstPageIndex: number, profileId: string | null) => void;
  tableProfiles: ProfileOption[]; hiddenProfiles: HiddenRecognitionProfile[];
  savingProfile: boolean;
  onRecognizeTable: (firstPageIndex: number) => void;
  onRecognizeDoc: (firstPageIndex: number) => void;
  tableBusyPage: number | null;
  docBusyPage: number | null;
}) {
  const [editing, setEditing] = useState(false);
  const [codeVal, setCodeVal] = useState(group.code);
  const [nameVal, setNameVal] = useState(group.name ?? '');
  const isSpecial = group.kind !== 'Document';
  const suspicious = isSuspicious(group);
  const hasSelectionHere = group.pageIndices.some(p => selected.has(p));
  const firstPage = group.pageIndices[0];
  const currentTag = group.tags.find(t => TABLE_TAGS.some(x => x.code === t)) ?? '';

  // В режиме «только подозрительные» скрываем обложку/титул и полностью корректные документы.
  if (suspiciousOnly && (isSpecial || !suspicious)) return null;

  function commit() {
    onRename(group.id, codeVal.trim() || DEFAULT_CODE, nameVal.trim() || null);
    setEditing(false);
  }

  return (
    <Card flush variant={isSpecial ? 'filled' : 'outlined'} className="p-3">
      <div className="flex items-center justify-between gap-2 mb-2">
        {isSpecial ? (
          <span className="text-body-medium font-medium text-on-surface-variant">{groupLabel(group)}</span>
        ) : editing ? (
          // onBlur вешаем на контейнер и коммитим только когда фокус уходит ИЗ обоих полей
          // (relatedTarget вне контейнера). Иначе переход фокуса Шифр→Наименование закрывал бы
          // редактор до того, как фокус попадёт во второе поле — из-за этого имя было не отредактировать.
          <div className="flex items-center gap-2 flex-1"
            onBlur={e => { if (!e.currentTarget.contains(e.relatedTarget as Node | null)) commit(); }}>
            <TextField density="dense" label="Шифр" placeholder="Шифр" value={codeVal} onChange={e => setCodeVal(e.target.value)}
              autoFocus onKeyDown={e => { if (e.key === 'Enter') commit(); if (e.key === 'Escape') { e.stopPropagation(); setEditing(false); } }}
              containerClassName="w-32" />
            <TextField density="dense" label="Наименование документа" placeholder="Наименование документа"
              value={nameVal} onChange={e => setNameVal(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') commit(); if (e.key === 'Escape') { e.stopPropagation(); setEditing(false); } }}
              containerClassName="flex-1" />
          </div>
        ) : (
          <button onClick={() => setEditing(true)} className="flex items-center gap-1.5 text-left group/title min-w-0">
            {suspicious && <Icon icon={Warning} size={18} className="text-warning shrink-0" />}
            <span className="text-body-medium font-medium text-on-surface truncate">
              {group.name ?? <em className="text-on-surface-variant">без названия</em>}
            </span>
            <span className="text-body-small text-on-surface-variant shrink-0">· {group.code}</span>
            <Icon icon={Edit} size={18} className="text-on-surface-variant opacity-0 group-hover/title:opacity-100 shrink-0" />
          </button>
        )}
        <span className="text-body-small text-on-surface-variant shrink-0">{group.pageIndices.length} л.</span>
        {!isSpecial && (
          <IconButton label={`Расформировать группу ${group.code}${group.name ? ` «${group.name}»` : ''}`}
            title="Расформировать (страницы станут без группы)" danger onClick={() => onDisband(group.id)} icon={Delete} />
        )}
      </div>

      {/* Тэг типа таблицы + распознавание таблицы/документа — препроцессинг на уровне набора (issue #40).
          Таблица/перераспознавание работают по СОХРАНЁННОЙ группировке (первая страница), поэтому при
          несохранённых правках заблокированы с подсказкой. */}
      {!isSpecial && firstPage !== undefined && (
        <div className="flex items-center gap-2 mb-2 flex-wrap">
          <Select density="dense" className="w-auto max-w-[170px]" value={currentTag} onValueChange={tag => onSetTag(group.id, tag)}
            aria-label="Тип таблицы документа — распознаётся и выгружается (XLSX/CSV)" emptyItem="— тип таблицы">
            {TABLE_TAGS.map(t => <SelectItem key={t.code} value={t.code}>{t.label}</SelectItem>)}
          </Select>
          {/* Профиль распознавания (issue #410): для ПРОИЗВОЛЬНОЙ таблицы, у которой типа/тэга нет.
              Привязка снимает требование тэга — иначе такую таблицу не распознать вовсе. */}
          <Select density="dense" className="w-auto max-w-[190px]" value={group.profileId ?? ''}
            onValueChange={id => onSetProfile(firstPage, id || null)} disabled={savingProfile}
            aria-label="Профиль распознавания — набор колонок для произвольной таблицы (задаётся в «Профили распознавания»)"
            emptyItem="— профиль таблицы">
            {withBoundOption(tableProfiles, group.profileId, hiddenProfiles).map(p => <SelectItem key={p.id} value={p.id} disabled={p.disabled}>{p.name}</SelectItem>)}
          </Select>
          {(currentTag || group.profileId) && (
            // Подсказка — самой кнопки: у неактивной она и говорит, почему нельзя (ревью #1704), а при
            // правках ещё и предлагает их сохранить — богатой подсказкой ([UI-72]).
            <Button variant="outlined" onClick={() => onRecognizeTable(firstPage)}
              rich={saveFirst ? unsaved(saveFirst) : { text: isHiddenBound(group.profileId, hiddenProfiles) ? 'Профиль группы принадлежит выключенному модулю — выберите другой' : 'Распознать таблицу этого документа как отдельный источник данных' }}
              disabled={!!saveFirst || isHiddenBound(group.profileId, hiddenProfiles)} loading={tableBusyPage === firstPage}
              icon={<Icon icon={Table} size={18} />}>
              Таблица
            </Button>
          )}
          <Button variant="outlined" onClick={() => onRecognizeDoc(firstPage)} disabled={!!saveFirst}
            rich={saveFirst ? unsaved(saveFirst) : { text: 'Перераспознать только этот документ (не весь набор)' }}
            loading={docBusyPage === firstPage} icon={<Icon icon={Refresh} size={18} />}>
            Перераспознать
          </Button>
        </div>
      )}

      {group.pageIndices.length === 0 ? (
        <p className="text-body-small text-on-surface-variant italic">Нет страниц — перенесите сюда выделенные из других групп.</p>
      ) : (
        <div className="grid gap-2" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(72px, 1fr))' }}>
          {group.pageIndices.map(p => (
            <PageTile key={p} fileId={fileId} pageIndex={p} selected={selected.has(p)} suspicious={suspicious}
              noAnswer={pagesWithoutAnswer.has(p)}
              onToggle={onToggle} onView={onView} />
          ))}
        </div>
      )}

      {hasSelectionHere && (
        <SelectionActionBar
          count={[...selected].filter(p => group.pageIndices.includes(p)).length}
          candidateGroups={otherGroups.map(g => ({ id: g.id, label: groupLabel(g) }))}
          onMoveSelected={onMoveSelected}
          onSplitSelected={onSplitSelected}
        />
      )}
    </Card>
  );
}

// ─── Страница-редактор ──────────────────────────────────────────────────────────────────────────

export function PdfGroupingEditor() {
  const { fileId } = useParams<{ fileId: string }>();
  const location = useLocation() as { state?: { sourceName?: string } };
  const navigate = useNavigate();
  const { data, isLoading, error } = useFilePages(fileId ?? null);
  const applyMutation = useApplyGrouping(fileId!);
  const recognizeTable = useRecognizeDocumentTable(fileId!);
  const recognizeDoc = useRecognizeDocument(fileId!);
  // Привязка профиля — точечный PUT (issue #410): не проходит через сохранение разбиения, поэтому
  // не стирает уже распознанное сырьё таблицы и не требует предварительного сохранения.
  const setProfile = useSetDocumentProfile(fileId!);
  const { data: allProfiles = [] } = useListRecognitionProfiles();
  const { data: hiddenProfiles = [] } = useHiddenRecognitionProfiles();
  const tableProfiles = allProfiles.filter(p => p.kindInfo.isTabular).map(p => ({ id: p.id, name: p.name }));

  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [suspiciousOnly, setSuspiciousOnly] = useState(false);
  const [refusal, setRefusal] = useState<RecognitionRefusal | null>(null);

  // Кнопки таблицы и перераспознавания стоят внутри карточки документа, и место для строки ошибки
  // тут одно — под шапкой, далеко от нажатой кнопки. Поэтому отказ показываем диалогом, тем же, что
  // и на других входах: совет про настройки в нём появляется, только если отказ им и лечится.
  function showRecognizeError(err: unknown) {
    setRefusal(recognitionRefusal(err));
  }
  const [viewerPage, setViewerPage] = useState<number | null>(null);
  const lastClickedRef = useRef<number | null>(null);

  /**
   * Разбиение показываем как ответ сервера ПЛЮС локальную правку поверх него (issue #858).
   *
   * <p>Раньше правка была копией, которую эффект переливал из ответа «пока не грязно». Копия и
   * флаг «грязно» — два состояния об одном и том же, и рассинхронизировать их мог любой порядок
   * приходов: свежий ответ сервера в момент, когда правка уже началась, молча затирал бы её.
   * Оверрайд отвечает на оба вопроса разом: есть он — правка идёт и она же показывается, нет —
   * показываем серверное. Сохранение снимает оверрайд, и экран снова следует за сервером.</p>
   */
  const serverGroups = useMemo(() => (data ? makeGroups(data.groups) : null), [data]);
  const [edited, setEdited] = useState<EditableGroup[] | null>(null);
  const groups = edited ?? serverGroups;
  const dirty = edited !== null;

  const pageCount = data?.pageCount ?? 0;
  const documentCount = useMemo(() => (groups ?? []).filter(g => g.kind === 'Document').length, [groups]);
  const assignedPages = useMemo(() => new Set(groups?.flatMap(g => g.pageIndices) ?? []), [groups]);
  const unassignedPages = useMemo(
    () => Array.from({ length: pageCount }, (_, i) => i).filter(i => !assignedPages.has(i)),
    [pageCount, assignedPages],
  );
  // Листы без ответа считаем из ОТВЕТА СЕРВЕРА, а не из редактируемых групп. Признак принадлежит
  // листу, и локальная перегруппировка не меняет того, ответила ли по нему модель. Пока он лежал в
  // состоянии редактора, каждая мутация групп обязана была помнить про перенос отметок — и помнила
  // одна из трёх: перенос в существующую группу терял отметку у плитки, но не у счётчика (число
  // расходилось с картинкой), а расформирование теряло её молча.
  const pagesWithoutAnswer = useMemo(
    () => new Set(data?.groups.flatMap(g => g.pagesWithoutAnswer ?? []) ?? []),
    [data],
  );

  const suspiciousPageCount = useMemo(
    () => (groups ?? []).filter(isSuspicious).reduce((acc, g) => acc + g.pageIndices.length, 0) + unassignedPages.length,
    [groups, unassignedPages],
  );

  function toggle(pageIndex: number, e: React.MouseEvent) {
    setSelected(prev => {
      const next = new Set(prev);
      if (e.shiftKey && lastClickedRef.current !== null) {
        const [from, to] = [lastClickedRef.current, pageIndex].sort((a, b) => a - b);
        for (let i = from; i <= to; i++) next.add(i);
      } else if (e.ctrlKey || e.metaKey) {
        if (next.has(pageIndex)) next.delete(pageIndex);
        else next.add(pageIndex);
      } else {
        next.clear();
        next.add(pageIndex);
      }
      return next;
    });
    lastClickedRef.current = pageIndex;
  }

  function mutateGroups(fn: (prev: EditableGroup[]) => EditableGroup[]) {
    // База — предыдущая правка, а если её ещё нет, то ответ сервера. Функциональный апдейтер, а не
    // отрендеренное значение: два быстрых действия подряд обязаны сложиться, а не перебить друг друга.
    setEdited(prev => {
      const base = prev ?? serverGroups;
      return base ? fn(base) : prev;
    });
  }

  function removeFromAllGroups(groups: EditableGroup[], pages: Set<number>): EditableGroup[] {
    return groups.map(g => ({ ...g, pageIndices: g.pageIndices.filter(p => !pages.has(p)) }));
  }

  function handleMoveSelected(targetGroupId: string | 'new') {
    if (selected.size === 0) return;
    mutateGroups(prev => {
      const cleared = removeFromAllGroups(prev, selected);
      if (targetGroupId === 'new') {
        return [...cleared, { id: newLocalId(), kind: 'Document', code: DEFAULT_CODE, name: null, pageIndices: [...selected].sort((a, b) => a - b), tags: [], profileId: null }];
      }
      return cleared.map(g => g.id === targetGroupId
        ? { ...g, pageIndices: [...g.pageIndices, ...selected].sort((a, b) => a - b) }
        : g);
    });
    setSelected(new Set());
  }

  function handleSplitSelected() {
    handleMoveSelected('new');
  }

  function handleDisband(groupId: string) {
    // Только документы можно расформировать; обложку/титул убираем из вида, оставляя пустыми.
    mutateGroups(prev => prev.filter(g => g.id !== groupId));
  }

  function handleRename(groupId: string, code: string, name: string | null) {
    mutateGroups(prev => prev.map(g => g.id === groupId ? { ...g, code, name } : g));
  }

  // Тип таблицы документа — локальная правка, сохраняется вместе с разбиением («Сохранить»).
  function handleSetTag(groupId: string, tag: string) {
    mutateGroups(prev => prev.map(g => g.id === groupId ? { ...g, tags: tag ? [tag] : [] } : g));
  }

  async function handleSave() {
    if (!groups) return;
    const payload: GostGroupingGroup[] = groups
      .filter(g => g.pageIndices.length > 0)
      .map(g => ({
        kind: g.kind,
        code: g.kind === 'Document' ? g.code : null,
        name: g.kind === 'Document' ? g.name : null,
        pageIndices: g.pageIndices,
        tags: g.kind === 'Document' ? g.tags : [],
      }));
    await applyMutation.mutateAsync(payload);
    setEdited(null);   // снимаем оверрайд — дальше экран следует за ответом сервера
  }

  if (!fileId) return null;
  if (isLoading || !groups) {
    return <div className="p-6 flex items-center gap-2 text-body-medium text-on-surface-variant"><CircularProgress size={18} /> Загрузка...</div>;
  }
  if (error || !data) {
    return <div className="p-6 text-body-medium text-error">Не удалось загрузить страницы источника.</div>;
  }

  return (
    <div className="p-6 flex flex-col h-full">
      <PageHeader className="mb-4 shrink-0"
        icon={<IconButton label="Назад" onClick={() => navigate(-1)} icon={ArrowBack} />}
        title={`Разбиение${location.state?.sourceName ? ` — ${location.state.sourceName}` : ''}`}
        subtitle={<>
          {pageCount} стр. · {documentCount} документ{documentCount === 1 ? '' : 'ов'}
          {/* Третьим фактом в ту же строку, а не плашкой в ряду действий: плашка того же
              размера и формы рядом с кнопкой читается как кнопка, и первое, что с ней сделают, —
              нажмут. Фильтровать тут нечего: листы без ответа рассыпаны по группам, и, скрыв
              остальное, человек увидит обрубки документов. Где именно проблема — видно на
              плитках, там сигнал и громкий. */}
          {pagesWithoutAnswer.size > 0 && (
            <Tooltip content="По этим листам движок не ответил — их поля пусты не потому, что на листе ничего нет. Перераспознайте документ или смените модель.">
              <span className="text-error">
                {' · '}{ruCount(pagesWithoutAnswer.size, 'лист', 'листа', 'листов')} без ответа
              </span>
            </Tooltip>
          )}
        </>}
        actions={<>
          {suspiciousPageCount > 0 && (
            /* Отбор страниц — `Chip` ([UI-42], #1416): нажатый — отбор стоит, число — сколько под ним. */
            <Chip selected={suspiciousOnly} icon={Warning} iconTone="warning" count={suspiciousPageCount}
              onClick={() => setSuspiciousOnly(v => !v)}>
              Подозрительные
            </Chip>
          )}
          <Button variant="filled" onClick={handleSave} disabled={!dirty} loading={applyMutation.isPending}
            icon={<Icon icon={Save} size={18} />}>
            Сохранить
          </Button>
        </>} />

      {applyMutation.isError && <GroupingSaveError error={applyMutation.error} />}
      <MissingPartsNotice fileId={fileId!} missing={data?.missingParts ?? 0} dirty={dirty} />

      {/* Отказ распознавания таблицы/документа. Раньше эти две операции молчали при любой неудаче:
          кнопка переставала крутиться, и всё (issue #801). */}
      {refusal && (
        <RecognitionBlockedDialog message={refusal.message} configurable={refusal.configurable}
          onClose={() => setRefusal(null)} />
      )}

      <div className="flex-1 overflow-auto space-y-3 pb-4">
        {groups.map(g => (
          <GroupSection
            key={g.id} fileId={fileId} group={g} otherGroups={groups.filter(o => o.id !== g.id)}
            selected={selected} suspiciousOnly={suspiciousOnly} saveFirst={dirty ? () => void handleSave() : null}
            pagesWithoutAnswer={pagesWithoutAnswer}
            onToggle={toggle} onRename={handleRename} onMoveSelected={handleMoveSelected}
            onSplitSelected={handleSplitSelected} onDisband={handleDisband} onView={setViewerPage}
            onSetTag={handleSetTag}
            onSetProfile={(page, profileId) => setProfile.mutate({ firstPageIndex: page, profileId })}
            tableProfiles={tableProfiles} hiddenProfiles={hiddenProfiles}
            savingProfile={setProfile.isPending}
            onRecognizeTable={p => recognizeTable.mutate(p, { onError: showRecognizeError })}
            onRecognizeDoc={p => recognizeDoc.mutate(p, { onError: showRecognizeError })}
            tableBusyPage={recognizeTable.isPending ? (recognizeTable.variables ?? null) : null}
            docBusyPage={recognizeDoc.isPending ? (recognizeDoc.variables ?? null) : null}
          />
        ))}

        {(!suspiciousOnly || unassignedPages.length > 0) && unassignedPages.length > 0 && (
          <div className="rounded-md border border-dashed border-outline-variant p-3">
            <div className="flex items-center justify-between mb-2">
              <span className="text-body-medium font-medium text-on-surface-variant">Без группы</span>
              <span className="text-body-small text-on-surface-variant">{unassignedPages.length} л.</span>
            </div>
            <div className="grid gap-2" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(72px, 1fr))' }}>
              {unassignedPages.map(p => (
                <PageTile key={p} fileId={fileId} pageIndex={p} selected={selected.has(p)} suspicious={false}
                  noAnswer={pagesWithoutAnswer.has(p)} onToggle={toggle} onView={setViewerPage} />
              ))}
            </div>
            {unassignedPages.some(p => selected.has(p)) && (
              <SelectionActionBar
                count={[...selected].filter(p => unassignedPages.includes(p)).length}
                candidateGroups={groups.map(g => ({ id: g.id, label: groupLabel(g) }))}
                onMoveSelected={handleMoveSelected}
                onSplitSelected={() => handleMoveSelected('new')}
              />
            )}
          </div>
        )}
      </div>

      {viewerPage !== null && (
        <PageViewer key={viewerPage} fileId={fileId} pageIndex={viewerPage} onClose={() => setViewerPage(null)} />
      )}
    </div>
  );
}
