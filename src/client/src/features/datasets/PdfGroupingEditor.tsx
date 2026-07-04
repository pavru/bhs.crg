import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate, useParams, useLocation } from 'react-router-dom';
import { ArrowLeft, ChevronDown, Loader2, Pencil, Trash2, AlertTriangle, Save } from 'lucide-react';
import { useSourcePages, useApplyGrouping, loadPageThumbnailUrl } from '@/shared/api/datasets';
import type { GostGroupingDocument } from '@/shared/api/types';

const DEFAULT_CODE = '(без шифра)';

interface EditableGroup {
  id: string;
  code: string;
  name: string | null;
  pageIndices: number[];
}

function isSuspicious(g: Pick<EditableGroup, 'code' | 'name'>): boolean {
  return !g.code || g.code === DEFAULT_CODE || !g.name;
}

function makeGroups(documents: GostGroupingDocument[]): EditableGroup[] {
  return documents.map(d => ({
    id: crypto.randomUUID(),
    code: d.code,
    name: d.name,
    pageIndices: [...d.pageIndices].sort((a, b) => a - b),
  }));
}

// ─── Миниатюра страницы (ленивая загрузка, без OCR — только чтобы узнать документ глазами) ────

function PageThumbnail({ sourceId, pageIndex }: { sourceId: string; pageIndex: number }) {
  const [url, setUrl] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    let objectUrl: string | null = null;
    loadPageThumbnailUrl(sourceId, pageIndex)
      .then(u => { if (!cancelled) { objectUrl = u; setUrl(u); } })
      .catch(() => { if (!cancelled) setFailed(true); });
    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [sourceId, pageIndex]);

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

function PageTile({
  sourceId, pageIndex, selected, suspicious, onToggle,
}: {
  sourceId: string; pageIndex: number; selected: boolean; suspicious: boolean;
  onToggle: (pageIndex: number, e: React.MouseEvent) => void;
}) {
  return (
    <button
      onClick={e => onToggle(pageIndex, e)}
      className={`relative rounded-md p-1 border-2 transition-colors text-left ${
        selected ? 'border-brand bg-brand-subtle' : 'border-transparent hover:border-stroke'
      }`}
      title={`Страница ${pageIndex + 1}`}>
      <PageThumbnail sourceId={sourceId} pageIndex={pageIndex} />
      <div className="flex items-center justify-between mt-1 px-0.5">
        <span className="text-[10px] text-fg4">{pageIndex + 1}</span>
        {suspicious && <AlertTriangle size={10} className="text-warning" />}
      </div>
    </button>
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
        Отделить в новую группу
      </button>
      <div className="relative">
        <button onClick={() => setMenuOpen(o => !o)}
          className="flex items-center gap-1 px-2 py-1 text-xs rounded-md border border-stroke text-fg2 hover:bg-base">
          Перенести в группу <ChevronDown size={11} />
        </button>
        {menuOpen && (
          <div className="absolute z-10 mt-1 w-48 rounded-md border border-stroke bg-surface shadow-lg py-1">
            {candidateGroups.length === 0 && (
              <p className="px-3 py-1.5 text-xs text-fg4">Нет других групп</p>
            )}
            {candidateGroups.map(g => (
              <button key={g.id} onClick={() => { onMoveSelected(g.id); setMenuOpen(false); }}
                className="block w-full text-left px-3 py-1.5 text-xs hover:bg-base truncate">
                {g.name ?? g.code}
              </button>
            ))}
            <button onClick={() => { onMoveSelected('new'); setMenuOpen(false); }}
              className="block w-full text-left px-3 py-1.5 text-xs hover:bg-base text-brand">
              + Новая группа
            </button>
          </div>
        )}
      </div>
    </div>
  );
}

// ─── Группа документа ──────────────────────────────────────────────────────────────────────────

function GroupSection({
  sourceId, group, otherGroups, selected, suspiciousOnly,
  onToggle, onRename, onMoveSelected, onSplitSelected, onDisband,
}: {
  sourceId: string;
  group: EditableGroup;
  otherGroups: EditableGroup[];
  selected: Set<number>;
  suspiciousOnly: boolean;
  onToggle: (pageIndex: number, e: React.MouseEvent) => void;
  onRename: (groupId: string, code: string, name: string | null) => void;
  onMoveSelected: (targetGroupId: string | 'new') => void;
  onSplitSelected: () => void;
  onDisband: (groupId: string) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [codeVal, setCodeVal] = useState(group.code);
  const [nameVal, setNameVal] = useState(group.name ?? '');
  const suspicious = isSuspicious(group);
  const hasSelectionHere = group.pageIndices.some(p => selected.has(p));

  const pagesToShow = suspiciousOnly ? group.pageIndices.filter(() => suspicious) : group.pageIndices;
  if (suspiciousOnly && pagesToShow.length === 0) return null;

  function commit() {
    onRename(group.id, codeVal.trim() || DEFAULT_CODE, nameVal.trim() || null);
    setEditing(false);
  }

  return (
    <div className="rounded-lg border border-stroke bg-surface p-3">
      <div className="flex items-center justify-between gap-2 mb-2">
        {editing ? (
          <div className="flex items-center gap-2 flex-1">
            <input value={codeVal} onChange={e => setCodeVal(e.target.value)} placeholder="Шифр"
              autoFocus onBlur={commit} onKeyDown={e => { if (e.key === 'Enter') commit(); if (e.key === 'Escape') setEditing(false); }}
              className="text-sm font-medium border-b border-brand bg-transparent outline-none w-32" />
            <input value={nameVal} onChange={e => setNameVal(e.target.value)} placeholder="Наименование документа"
              onBlur={commit} onKeyDown={e => { if (e.key === 'Enter') commit(); if (e.key === 'Escape') setEditing(false); }}
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
        <button onClick={() => onDisband(group.id)} title="Расформировать (страницы станут без группы)"
          className="p-1 text-stroke-strong hover:text-danger shrink-0">
          <Trash2 size={13} />
        </button>
      </div>

      <div className="grid gap-2" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(72px, 1fr))' }}>
        {pagesToShow.map(p => (
          <PageTile key={p} sourceId={sourceId} pageIndex={p} selected={selected.has(p)} suspicious={suspicious}
            onToggle={onToggle} />
        ))}
      </div>

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
  const { sourceId } = useParams<{ sourceId: string }>();
  const location = useLocation() as { state?: { sourceName?: string } };
  const navigate = useNavigate();
  const { data, isLoading, error } = useSourcePages(sourceId ?? null);
  const applyMutation = useApplyGrouping(sourceId!);

  const [groups, setGroups] = useState<EditableGroup[] | null>(null);
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [suspiciousOnly, setSuspiciousOnly] = useState(false);
  const [dirty, setDirty] = useState(false);
  const lastClickedRef = useRef<number | null>(null);

  // Инициализируем локальное редактируемое состояние из ответа сервера — только один раз при
  // загрузке (и после успешного сохранения, когда сервер возвращает свежую группировку).
  useEffect(() => {
    if (data && !dirty) setGroups(makeGroups(data.documents));
  }, [data, dirty]);

  const pageCount = data?.pageCount ?? 0;
  const assignedPages = useMemo(() => new Set(groups?.flatMap(g => g.pageIndices) ?? []), [groups]);
  const unassignedPages = useMemo(
    () => Array.from({ length: pageCount }, (_, i) => i).filter(i => !assignedPages.has(i)),
    [pageCount, assignedPages],
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
        next.has(pageIndex) ? next.delete(pageIndex) : next.add(pageIndex);
      } else {
        next.clear();
        next.add(pageIndex);
      }
      return next;
    });
    lastClickedRef.current = pageIndex;
  }

  function mutateGroups(fn: (prev: EditableGroup[]) => EditableGroup[]) {
    setGroups(prev => (prev ? fn(prev) : prev));
    setDirty(true);
  }

  function removeFromAllGroups(groups: EditableGroup[], pages: Set<number>): EditableGroup[] {
    return groups.map(g => ({ ...g, pageIndices: g.pageIndices.filter(p => !pages.has(p)) }));
  }

  function handleMoveSelected(targetGroupId: string | 'new') {
    if (selected.size === 0) return;
    mutateGroups(prev => {
      const cleared = removeFromAllGroups(prev, selected);
      if (targetGroupId === 'new') {
        return [...cleared, { id: crypto.randomUUID(), code: DEFAULT_CODE, name: null, pageIndices: [...selected].sort((a, b) => a - b) }];
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
    mutateGroups(prev => prev.filter(g => g.id !== groupId));
  }

  function handleRename(groupId: string, code: string, name: string | null) {
    mutateGroups(prev => prev.map(g => g.id === groupId ? { ...g, code, name } : g));
  }

  async function handleSave() {
    if (!groups) return;
    const documents: GostGroupingDocument[] = groups
      .filter(g => g.pageIndices.length > 0)
      .map(g => ({ code: g.code, name: g.name, pageIndices: g.pageIndices }));
    await applyMutation.mutateAsync(documents);
    setDirty(false);
  }

  if (!sourceId) return null;
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
            <p className="text-xs text-fg4">{pageCount} стр. · {groups.length} документ{groups.length === 1 ? '' : 'ов'}</p>
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
          <button onClick={handleSave} disabled={!dirty || applyMutation.isPending}
            className="flex items-center gap-2 px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md transition-colors disabled:opacity-50">
            {applyMutation.isPending ? <Loader2 size={14} className="animate-spin" /> : <Save size={14} />}
            Сохранить
          </button>
        </div>
      </div>

      {applyMutation.isError && (
        <p className="text-sm text-danger mb-3">
          {(applyMutation.error as { response?: { data?: { error?: string } } })?.response?.data?.error ?? 'Не удалось сохранить'}
        </p>
      )}

      <div className="flex-1 overflow-auto space-y-3 pb-4">
        {groups.map(g => (
          <GroupSection
            key={g.id} sourceId={sourceId} group={g} otherGroups={groups.filter(o => o.id !== g.id)}
            selected={selected} suspiciousOnly={suspiciousOnly}
            onToggle={toggle} onRename={handleRename} onMoveSelected={handleMoveSelected}
            onSplitSelected={handleSplitSelected} onDisband={handleDisband}
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
                <PageTile key={p} sourceId={sourceId} pageIndex={p} selected={selected.has(p)} suspicious={false}
                  onToggle={toggle} />
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
    </div>
  );
}
