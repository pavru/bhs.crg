import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate, useParams, useLocation } from 'react-router';
import { ArrowLeft, ChevronDown, Loader2, Pencil, Trash2, AlertTriangle, AlertCircle, Save, ZoomIn, Table2, RefreshCw } from 'lucide-react';
import {
  useFilePages, useApplyGrouping, useRecognizeDocumentTable, useRecognizeDocument, useSetDocumentProfile,
  loadPageThumbnailUrl, loadPageImageUrl, recognitionRefusal, type RecognitionRefusal,
} from '@/shared/api/datasets';
import { useListRecognitionProfiles } from '@/shared/api/recognitionProfiles';
import type { GostGroupingGroup, GostGroupKind } from '@/shared/api/types';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { ruCount } from '@/shared/utils/pluralize';
import { RecognitionBlockedDialog } from './RecognitionBlockedDialog';
import { newLocalId } from '@/shared/utils/localId';

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

// ─── Миниатюра страницы (ленивая загрузка, без OCR — только чтобы узнать документ глазами) ────

function PageThumbnail({ fileId, pageIndex }: { fileId: string; pageIndex: number }) {
  const [url, setUrl] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    let objectUrl: string | null = null;
    loadPageThumbnailUrl(fileId, pageIndex)
      .then(u => { if (!cancelled) { objectUrl = u; setUrl(u); } })
      .catch(() => { if (!cancelled) setFailed(true); });
    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [fileId, pageIndex]);

  return (
    <div className="w-full aspect-[210/297] rounded bg-base border border-stroke flex items-center justify-center overflow-hidden">
      {url ? (
        <img src={url} alt={`Страница ${pageIndex + 1}`} className="w-full h-full object-contain" />
      ) : failed ? (
        <span className="text-[10px] text-fg4 px-1 text-center">нет превью</span>
      ) : (
        <Loader2 size={14} className="animate-spin text-fg4" />
      )}
    </div>
  );
}

// ─── Одна миниатюра-страница со статусом выделения/подозрительности ────────────────────────────
// Внешний контейнер — div (не button): внутри лежит кнопка «просмотреть лист», а button-в-button
// — невалидный HTML. Клик по плитке переключает выделение, клик по лупе (stopPropagation) — открывает
// крупный просмотр листа.

function PageTile({
  fileId, pageIndex, selected, suspicious, noAnswer, onToggle, onView,
}: {
  fileId: string; pageIndex: number; selected: boolean; suspicious: boolean;
  /** Движок не ответил по этому листу — признак ЛИСТА, а не группы (issue #803). */
  noAnswer: boolean;
  onToggle: (pageIndex: number, e: React.MouseEvent) => void;
  onView: (pageIndex: number) => void;
}) {
  return (
    <div
      onClick={e => onToggle(pageIndex, e)}
      className={`group/tile relative rounded-md p-1 border-2 transition-colors text-left cursor-pointer ${
        selected ? 'border-brand bg-brand-subtle' : 'border-transparent hover:border-stroke'
      }`}
      title={noAnswer
        ? `Страница ${pageIndex + 1} — модель не ответила по этому листу, поля пусты`
        : `Страница ${pageIndex + 1}`}>
      <PageThumbnail fileId={fileId} pageIndex={pageIndex} />
      <button
        onClick={e => { e.stopPropagation(); onView(pageIndex); }}
        title="Просмотреть лист крупно"
        className="absolute top-1.5 right-1.5 p-1 rounded bg-surface/90 border border-stroke text-fg3 opacity-70 hover:text-brand hover:opacity-100 transition-opacity">
        <ZoomIn size={12} />
      </button>
      <div className="flex items-center justify-between mt-1 px-0.5">
        <span className="text-[10px] text-fg4">{pageIndex + 1}</span>
        {/* Лист без ответа виден отдельно от «подозрительной» группы: у первого поля пусты потому,
            что их неоткуда взять, у второй — потому что распозналось не то. Лечится это по-разному. */}
        {noAnswer
          ? <AlertCircle size={10} className="text-danger" />
          : suspicious && <AlertTriangle size={10} className="text-warning" />}
      </div>
    </div>
  );
}

// ─── Крупный просмотр одного листа (высокое DPI, клик — вписать ↔ 100%) ──────────────────────────

function PageViewer({ fileId, pageIndex, onClose }: { fileId: string; pageIndex: number; onClose: () => void }) {
  const [url, setUrl] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  const [zoomed, setZoomed] = useState(false);

  // Ни одного «сбросить перед загрузкой»: просмотрщик заводится заново на каждый лист (ключ по
  // номеру листа на месте вызова, issue #858), поэтому «ещё грузится» — это начальное состояние
  // свежего компонента, а не то, в которое его надо возвращать эффектом.
  useEffect(() => {
    let cancelled = false;
    let objectUrl: string | null = null;
    loadPageImageUrl(fileId, pageIndex)
      .then(u => { if (!cancelled) { objectUrl = u; setUrl(u); } })
      .catch(() => { if (!cancelled) setFailed(true); });
    return () => { cancelled = true; if (objectUrl) URL.revokeObjectURL(objectUrl); };
  }, [fileId, pageIndex]);

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title={`Лист ${pageIndex + 1}`} extraWide>
      {failed ? (
        <p className="text-sm text-danger py-10 text-center">Не удалось загрузить изображение листа.</p>
      ) : !url ? (
        <div className="flex items-center justify-center py-16 text-fg4"><Loader2 size={20} className="animate-spin" /></div>
      ) : (
        <>
          {/* Центрируем img через mx-auto (НЕ flex justify-center): при зуме, когда картинка шире/выше
              контейнера, auto-поля схлопываются в 0 → доступен скролл ко ВСЕМ краям (иначе левый/верхний
              край центрированного флекс-элемента недостижим и лист «обрезается»). */}
          <div className="overflow-auto max-h-[78vh] rounded border border-stroke bg-base">
            <img src={url} alt={`Лист ${pageIndex + 1}`}
              onClick={() => setZoomed(z => !z)}
              className={`block mx-auto ${zoomed ? 'max-w-none cursor-zoom-out' : 'max-w-full max-h-[78vh] object-contain cursor-zoom-in'}`} />
          </div>
          <p className="text-xs text-fg4 mt-2 text-center">Клик по изображению — переключить масштаб (вписать ↔ 100%).</p>
        </>
      )}
    </Modal>
  );
}

// ─── Панель действий над выделением (перенести в группу / отделить) ────────────────────────────

function SelectionActionBar({
  count, candidateGroups, onMoveSelected, onSplitSelected,
}: {
  count: number;
  candidateGroups: EditableGroup[];
  onMoveSelected: (targetGroupId: string | 'new') => void;
  onSplitSelected: () => void;
}) {
  const [menuOpen, setMenuOpen] = useState(false);
  return (
    <div className="flex items-center gap-2 mt-2 pt-2 border-t border-stroke">
      <span className="text-xs text-fg4">Выделено страниц: {count}</span>
      <button onClick={onSplitSelected}
        className="px-2 py-1 text-xs rounded-md border border-stroke text-fg2 hover:bg-base">
        Отделить в новый документ
      </button>
      <div className="relative">
        <button onClick={() => setMenuOpen(o => !o)}
          className="flex items-center gap-1 px-2 py-1 text-xs rounded-md border border-stroke text-fg2 hover:bg-base">
          Перенести в группу <ChevronDown size={11} />
        </button>
        {menuOpen && (
          <div className="absolute z-10 mt-1 w-52 rounded-md border border-stroke bg-surface shadow-lg py-1">
            {candidateGroups.length === 0 && (
              <p className="px-3 py-1.5 text-xs text-fg4">Нет других групп</p>
            )}
            {candidateGroups.map(g => (
              <button key={g.id} onClick={() => { onMoveSelected(g.id); setMenuOpen(false); }}
                className="block w-full text-left px-3 py-1.5 text-xs hover:bg-base truncate">
                {groupLabel(g)}
              </button>
            ))}
            <button onClick={() => { onMoveSelected('new'); setMenuOpen(false); }}
              className="block w-full text-left px-3 py-1.5 text-xs hover:bg-base text-brand">
              + Новый документ
            </button>
          </div>
        )}
      </div>
    </div>
  );
}

// ─── Группа (документ / обложка / титульный лист) ────────────────────────────────────────────────

function GroupSection({
  fileId, group, otherGroups, selected, suspiciousOnly, dirty, pagesWithoutAnswer,
  onToggle, onRename, onMoveSelected, onSplitSelected, onDisband, onView, onSetTag,
  onSetProfile, tableProfiles, savingProfile,
  onRecognizeTable, onRecognizeDoc, tableBusyPage, docBusyPage,
}: {
  fileId: string;
  group: EditableGroup;
  otherGroups: EditableGroup[];
  selected: Set<number>;
  /** Листы без ответа — с сервера, один набор на весь редактор (issue #803). */
  pagesWithoutAnswer: Set<number>;
  suspiciousOnly: boolean;
  dirty: boolean;
  onToggle: (pageIndex: number, e: React.MouseEvent) => void;
  onRename: (groupId: string, code: string, name: string | null) => void;
  onMoveSelected: (targetGroupId: string | 'new') => void;
  onSplitSelected: () => void;
  onDisband: (groupId: string) => void;
  onView: (pageIndex: number) => void;
  onSetTag: (groupId: string, tag: string) => void;
  onSetProfile: (firstPageIndex: number, profileId: string | null) => void;
  tableProfiles: { id: string; name: string }[];
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
    <div className={`rounded-lg border p-3 ${isSpecial ? 'border-stroke bg-base' : 'border-stroke bg-surface'}`}>
      <div className="flex items-center justify-between gap-2 mb-2">
        {isSpecial ? (
          <span className="text-sm font-medium text-fg2">{groupLabel(group)}</span>
        ) : editing ? (
          // onBlur вешаем на контейнер и коммитим только когда фокус уходит ИЗ обоих полей
          // (relatedTarget вне контейнера). Иначе переход фокуса Шифр→Наименование закрывал бы
          // редактор до того, как фокус попадёт во второе поле — из-за этого имя было не отредактировать.
          <div className="flex items-center gap-2 flex-1"
            onBlur={e => { if (!e.currentTarget.contains(e.relatedTarget as Node | null)) commit(); }}>
            <input value={codeVal} onChange={e => setCodeVal(e.target.value)} placeholder="Шифр"
              autoFocus onKeyDown={e => { if (e.key === 'Enter') commit(); if (e.key === 'Escape') setEditing(false); }}
              className="text-sm font-medium border-b border-brand bg-transparent outline-none w-32" />
            <input value={nameVal} onChange={e => setNameVal(e.target.value)} placeholder="Наименование документа"
              onKeyDown={e => { if (e.key === 'Enter') commit(); if (e.key === 'Escape') setEditing(false); }}
              className="text-sm border-b border-brand bg-transparent outline-none flex-1" />
          </div>
        ) : (
          <button onClick={() => setEditing(true)} className="flex items-center gap-1.5 text-left group/title min-w-0">
            {suspicious && <AlertTriangle size={13} className="text-warning shrink-0" />}
            <span className="text-sm font-medium text-fg1 truncate">
              {group.name ?? <em className="text-fg4">без названия</em>}
            </span>
            <span className="text-xs text-fg4 shrink-0">· {group.code}</span>
            <Pencil size={11} className="text-stroke-strong opacity-0 group-hover/title:opacity-100 shrink-0" />
          </button>
        )}
        <span className="text-xs text-fg4 shrink-0">{group.pageIndices.length} л.</span>
        {!isSpecial && (
          <button onClick={() => onDisband(group.id)} title="Расформировать (страницы станут без группы)"
            className="p-1 text-stroke-strong hover:text-danger shrink-0">
            <Trash2 size={13} />
          </button>
        )}
      </div>

      {/* Тэг типа таблицы + распознавание таблицы/документа — препроцессинг на уровне набора (issue #40).
          Таблица/перераспознавание работают по СОХРАНЁННОЙ группировке (первая страница), поэтому при
          несохранённых правках заблокированы с подсказкой. */}
      {!isSpecial && firstPage !== undefined && (
        <div className="flex items-center gap-2 mb-2 flex-wrap">
          <select value={currentTag} onChange={e => onSetTag(group.id, e.target.value)}
            title="Тип таблицы документа — распознаётся и выгружается (XLSX/CSV)"
            className="text-[11px] border border-stroke rounded px-1 py-0.5 bg-surface text-fg3 max-w-[170px] disabled:opacity-50">
            <option value="">— тип таблицы</option>
            {TABLE_TAGS.map(t => <option key={t.code} value={t.code}>{t.label}</option>)}
          </select>
          {/* Профиль распознавания (issue #410): для ПРОИЗВОЛЬНОЙ таблицы, у которой типа/тэга нет.
              Привязка снимает требование тэга — иначе такую таблицу не распознать вовсе. */}
          <select value={group.profileId ?? ''}
            onChange={e => onSetProfile(firstPage, e.target.value || null)}
            disabled={savingProfile}
            title="Профиль распознавания — набор колонок для произвольной таблицы (задаётся в «Профили распознавания»)"
            className="text-[11px] border border-stroke rounded px-1 py-0.5 bg-surface text-fg3 max-w-[190px] disabled:opacity-50">
            <option value="">— профиль таблицы</option>
            {tableProfiles.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
          </select>
          {(currentTag || group.profileId) && (
            <button onClick={() => onRecognizeTable(firstPage)} disabled={dirty || tableBusyPage === firstPage}
              title={dirty ? 'Сначала сохраните разбиение' : 'Распознать таблицу этого документа как отдельный источник данных'}
              className="flex items-center gap-1 px-2 py-1 text-xs rounded-md border border-stroke text-fg2 hover:bg-base disabled:opacity-50">
              {tableBusyPage === firstPage ? <Loader2 size={12} className="animate-spin" /> : <Table2 size={12} />}
              Таблица
            </button>
          )}
          <button onClick={() => onRecognizeDoc(firstPage)} disabled={dirty || docBusyPage === firstPage}
            title={dirty ? 'Сначала сохраните разбиение' : 'Перераспознать только этот документ (не весь набор)'}
            className="flex items-center gap-1 px-2 py-1 text-xs rounded-md border border-stroke text-fg2 hover:bg-base disabled:opacity-50">
            {docBusyPage === firstPage ? <Loader2 size={12} className="animate-spin" /> : <RefreshCw size={12} />}
            Перераспознать
          </button>
        </div>
      )}

      {group.pageIndices.length === 0 ? (
        <p className="text-xs text-fg4 italic">Нет страниц — перенесите сюда выделенные из других групп.</p>
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
          candidateGroups={otherGroups}
          onMoveSelected={onMoveSelected}
          onSplitSelected={onSplitSelected}
        />
      )}
    </div>
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
  const tableProfiles = allProfiles
    .filter(p => p.kindInfo.isTabular)
    .map(p => ({ id: p.id, name: p.name }));

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
    return <div className="p-6 flex items-center gap-2 text-sm text-fg4"><Loader2 size={16} className="animate-spin" /> Загрузка...</div>;
  }
  if (error || !data) {
    return <div className="p-6 text-sm text-danger">Не удалось загрузить страницы источника.</div>;
  }

  return (
    <div className="p-6 flex flex-col h-full">
      <div className="flex items-center justify-between mb-4 shrink-0">
        <div className="flex items-center gap-3">
          <button onClick={() => navigate(-1)} className="p-1.5 text-fg4 hover:text-fg1 rounded transition-colors">
            <ArrowLeft size={16} />
          </button>
          <div>
            <h1 className="text-lg font-semibold text-fg1">
              Разбиение{location.state?.sourceName ? ` — ${location.state.sourceName}` : ''}
            </h1>
            <p className="text-xs text-fg4">
              {pageCount} стр. · {documentCount} документ{documentCount === 1 ? '' : 'ов'}
              {/* Третьим фактом в ту же строку, а не плашкой в ряду действий: плашка того же
                  размера и формы рядом с кнопкой читается как кнопка, и первое, что с ней сделают, —
                  нажмут. Фильтровать тут нечего: листы без ответа рассыпаны по группам, и, скрыв
                  остальное, человек увидит обрубки документов. Где именно проблема — видно на
                  плитках, там сигнал и громкий. */}
              {pagesWithoutAnswer.size > 0 && (
                <span className="text-danger"
                  title="По этим листам движок не ответил — их поля пусты не потому, что на листе ничего нет. Перераспознайте документ или смените модель.">
                  {' · '}{ruCount(pagesWithoutAnswer.size, 'лист', 'листа', 'листов')} без ответа
                </span>
              )}
            </p>
          </div>
        </div>
        <div className="flex items-center gap-3">
          {suspiciousPageCount > 0 && (
            <button onClick={() => setSuspiciousOnly(v => !v)}
              className={`flex items-center gap-1.5 px-2.5 py-1.5 text-xs rounded-md border transition-colors ${
                suspiciousOnly ? 'border-warning bg-warning/10 text-warning' : 'border-stroke text-fg2 hover:bg-base'
              }`}>
              <AlertTriangle size={12} /> {suspiciousOnly ? 'Показать все' : `⚠ ${suspiciousPageCount} подозрительных`}
            </button>
          )}
          <Button variant="filled" onClick={handleSave} disabled={!dirty} loading={applyMutation.isPending}
            icon={<Save size={14} />}>
            Сохранить
          </Button>
        </div>
      </div>

      {applyMutation.isError && (
        <p className="text-sm text-danger mb-3">
          {(applyMutation.error as { response?: { data?: { error?: string } } })?.response?.data?.error ?? 'Не удалось сохранить'}
        </p>
      )}

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
            selected={selected} suspiciousOnly={suspiciousOnly} dirty={dirty}
            pagesWithoutAnswer={pagesWithoutAnswer}
            onToggle={toggle} onRename={handleRename} onMoveSelected={handleMoveSelected}
            onSplitSelected={handleSplitSelected} onDisband={handleDisband} onView={setViewerPage}
            onSetTag={handleSetTag}
            onSetProfile={(page, profileId) => setProfile.mutate({ firstPageIndex: page, profileId })}
            tableProfiles={tableProfiles}
            savingProfile={setProfile.isPending}
            onRecognizeTable={p => recognizeTable.mutate(p, { onError: showRecognizeError })}
            onRecognizeDoc={p => recognizeDoc.mutate(p, { onError: showRecognizeError })}
            tableBusyPage={recognizeTable.isPending ? (recognizeTable.variables ?? null) : null}
            docBusyPage={recognizeDoc.isPending ? (recognizeDoc.variables ?? null) : null}
          />
        ))}

        {(!suspiciousOnly || unassignedPages.length > 0) && unassignedPages.length > 0 && (
          <div className="rounded-lg border border-dashed border-stroke p-3">
            <div className="flex items-center justify-between mb-2">
              <span className="text-sm font-medium text-fg2">Без группы</span>
              <span className="text-xs text-fg4">{unassignedPages.length} л.</span>
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
                candidateGroups={groups}
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
