import { useState, useMemo, useEffect } from 'react';
import { Loader2, Link2, Unlink, ShieldCheck, Search, Globe, ExternalLink, Download, Eye, Check } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { usePreviewDataSetBindings } from '@/shared/api/datasets';
import {
  useListQualityDocs, useListMaterialLinks, useSetMaterialLinks, useRemoveMaterialLink,
  suggestLinks, searchQualityDocs, importQualityDocFromUrl,
  type LinkSuggestion, type SearchCandidate, type QualityDocument,
} from '@/shared/api/qualityDocs';
import type { DocumentInstance, DocumentType, CatalogScope } from '@/shared/api/types';
import { typeHasTag, findTaggedFieldPath, resolveEffectiveFields } from '@/shared/api/schema';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import { QualityDocForm } from '@/features/quality-docs/QualityDocForm';
import { recognizeAndUpdate } from '@/features/quality-docs/recognizeImported';
import { openAttachmentInNewTab } from '@/shared/api/attachments';

// ─── Срок действия документа качества (по функциональному тэгу quality.validUntil) ──
function readPath(obj: Record<string, unknown>, path: string[]): unknown {
  return path.reduce<unknown>((o, k) => (o && typeof o === 'object') ? (o as Record<string, unknown>)[k] : undefined, obj);
}
function getValidUntil(doc: QualityDocument, allDocTypes: DocumentType[]): string | null {
  const dt = allDocTypes.find(t => t.id === doc.documentTypeId);
  if (!dt) return null;
  const path = findTaggedFieldPath(dt, FUNCTIONAL_TAG.qualityValidUntil, allDocTypes);
  if (!path) return null;
  const v = readPath(doc.requisites, path);
  return typeof v === 'string' && v.trim() ? v : null;
}
function isExpired(doc: QualityDocument, allDocTypes: DocumentType[]): boolean {
  const vu = getValidUntil(doc, allDocTypes);
  if (!vu) return false; // нет даты — не считаем просроченным
  const d = new Date(vu);
  if (Number.isNaN(d.getTime())) return false;
  const today = new Date(); today.setHours(0, 0, 0, 0);
  return d < today;
}
// ─── Оценка релевантности документа материалу ──────────────────────────────────
const STOP = new Set(['для', 'или', 'при', 'без', 'шт', 'штук', 'тип', 'сертификат', 'декларация', 'соответствия']);
function normText(s: string): string {
  return s.toLowerCase().replace(/ё/g, 'е').replace(/[^a-zа-я0-9]+/g, ' ').trim();
}
function tokenize(s: string): string[] {
  return normText(s).split(/\s+/).filter(t => t.length >= 3 && !STOP.has(t));
}
/** Грубая основа слова: срезаем окончание (русская морфология) — «выключатель»≈«выключатели». */
function stem(t: string): string { return t.length > 6 ? t.slice(0, 6) : t; }
function collectStrings(v: unknown, out: string[]): void {
  if (typeof v === 'string') out.push(v);
  else if (Array.isArray(v)) for (const x of v) collectStrings(x, out);
  else if (v && typeof v === 'object') for (const x of Object.values(v)) collectStrings(x, out);
}
/** Все строковые реквизиты документа + название — «стог» для сопоставления. */
function docHaystackStems(doc: QualityDocument): Set<string> {
  const parts: string[] = [doc.displayName];
  collectStrings(doc.requisites as Record<string, unknown>, parts);
  return new Set(tokenize(parts.join(' ')).map(stem));
}
interface WeightedToken { t: string; w: number }
function weighted(query: string): WeightedToken[] {
  const seen = new Set<string>(); const out: WeightedToken[] = [];
  for (const t of tokenize(query)) {
    if (seen.has(t)) continue; seen.add(t);
    // числа/модели и длинные слова важнее общих коротких слов
    out.push({ t, w: /\d/.test(t) ? 3 : t.length >= 6 ? 2 : 1 });
  }
  return out;
}
function relevance(tokens: WeightedToken[], hayStems: Set<string>): number {
  if (tokens.length === 0) return 0;
  let matched = 0, total = 0;
  for (const { t, w } of tokens) { total += w; if (hayStems.has(stem(t))) matched += w; }
  return total ? matched / total : 0;
}

/** Совпадает с backend MaterialKeyNormalizer: регистр + схлопывание пробелов. */
function normalizeKey(s: string | null | undefined): string {
  if (!s) return '';
  return s.split(/\s+/).filter(Boolean).join(' ').toLowerCase();
}

interface MaterialRow { key: string; label: string; idValues: string[] }

// ─── Модалка выбора/создания документа для связывания ───────────────────────────

function LinkPickerModal({ open, onClose, allDocTypes, scope, scopeId, materials, onPick }: {
  open: boolean; onClose: () => void; allDocTypes: DocumentType[];
  scope: CatalogScope; scopeId: string | null; materials: MaterialRow[];
  onPick: (docId: string) => void;
}) {
  const count = materials.length;
  const [tab, setTab] = useState<'pick' | 'search' | 'create'>('pick');
  const [includeExpired, setIncludeExpired] = useState(false);
  // Единая строка поиска: фильтрует библиотеку и используется для веб-поиска.
  const [query, setQuery] = useState('');
  // Грузим всю библиотеку по области; релевантность к материалу считаем на клиенте.
  const { data: docs = [], isLoading } = useListQualityDocs({ scope, scopeId: scopeId ?? undefined, enabled: open });

  const qualityTypes = useMemo(
    () => allDocTypes.filter(dt => dt.kind === 'Document' && !dt.isAbstract && typeHasTag(dt, FUNCTIONAL_TAG.typeQualityDocument, allDocTypes)),
    [allDocTypes],
  );

  // Поисковый запрос формируем из выбранного материала (артикул + наименование).
  const baseQuery = useMemo(() => {
    const m = materials[0];
    if (!m) return '';
    return Array.from(new Set(m.idValues.map(s => s.trim()).filter(Boolean))).join(' ');
  }, [materials]);

  const [searchType, setSearchType] = useState('');
  const [results, setResults] = useState<SearchCandidate[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [importingUrl, setImportingUrl] = useState<string | null>(null);
  const [searchError, setSearchError] = useState('');

  // Сброс и инициализация при открытии: строку поиска заполняем материалом.
  useEffect(() => {
    if (!open) return;
    setTab('pick'); setIncludeExpired(false);
    setResults(null); setSearchError(''); setQuery(baseQuery);
    setSearchType(prev => prev || (qualityTypes.find(t => /сертификат/i.test(t.name)) ?? qualityTypes[0])?.id || '');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  // Взвешенные токены запроса (из материала или ручного ввода).
  const queryTokens = useMemo(() => weighted(query), [query]);
  const hasQuery = queryTokens.length > 0;
  const ranked = useMemo(() => {
    const arr = docs.map(d => ({
      d,
      expired: isExpired(d, allDocTypes),
      validUntil: getValidUntil(d, allDocTypes),
      score: relevance(queryTokens, docHaystackStems(d)), // релевантность 0..1 по всем реквизитам
    }));
    return arr.sort((a, b) => b.score - a.score);
  }, [docs, allDocTypes, queryTokens]);
  const expiredCount = ranked.filter(x => x.expired).length; // всего просроченных (по области)
  // Действующие — по релевантности; просроченные — только при включённой галке.
  const visible = ranked.filter(x => x.expired ? includeExpired : (!hasQuery || x.score > 0));

  async function runSearch(q?: string) {
    const term = (q ?? query).trim();
    if (!term) return;
    setSearching(true); setSearchError(''); setResults(null);
    try { setResults(await searchQualityDocs(term)); }
    catch (e: unknown) { setSearchError(e instanceof Error ? e.message : 'Ошибка поиска'); }
    finally { setSearching(false); }
  }

  // Переход в веб-поиск: сохраняем строку и сразу запускаем поиск (как библиотека показывает сразу).
  function enterSearch() {
    setTab('search');
    const q = query.trim() || baseQuery;
    setQuery(q);
    if (results === null && q) void runSearch(q);
  }

  async function importAndLink(c: SearchCandidate) {
    if (!searchType) { setSearchError('Выберите тип документа'); return; }
    setImportingUrl(c.url); setSearchError('');
    try {
      let doc = await importQualityDocFromUrl({ url: c.url, title: c.title, documentTypeId: searchType, scope, scopeId });
      // Автоматически распознаём скан импортированного документа (best-effort).
      try { doc = await recognizeAndUpdate(doc, allDocTypes); } catch { /* распознавание не критично */ }
      onPick(doc.id);
    } catch (e: unknown) {
      setSearchError(e instanceof Error ? e.message : 'Не удалось импортировать');
    } finally { setImportingUrl(null); }
  }

  const tabLabel = { pick: 'Из библиотеки', search: 'Поиск в интернете', create: 'Создать вручную' };

  return (
    <Modal open={open} onOpenChange={o => { if (!o) onClose(); }} title={`Документ качества для ${count} материал(ов)`} extraWide>
      <div className="flex gap-1 mb-3 bg-muted rounded-lg p-0.5 w-fit">
        {(['pick', 'search', 'create'] as const).map(t => (
          <button key={t} onClick={() => { if (t === 'search') enterSearch(); else setTab(t); }}
            className={`px-3 py-1.5 text-sm rounded-md ${tab === t ? 'bg-surface text-fg1 font-medium shadow-sm' : 'text-fg3'}`}>
            {tabLabel[t]}
          </button>
        ))}
      </div>

      {tab === 'pick' && (
        <div className="space-y-2">
          <div className="flex items-center gap-2 border border-stroke-strong rounded-md px-2">
            <Search size={14} className="text-fg4" />
            <input value={query} onChange={e => setQuery(e.target.value)} placeholder="Поиск по материалу / названию..."
              className="flex-1 py-2 text-sm bg-transparent focus:outline-none" />
          </div>
          <label className="flex items-center gap-1.5 text-xs text-fg3 cursor-pointer">
            <input type="checkbox" checked={includeExpired} onChange={e => setIncludeExpired(e.target.checked)}
              className="w-3.5 h-3.5 rounded border-stroke-strong text-brand" />
            Показать просроченные{expiredCount > 0 ? ` (${expiredCount})` : ''}
          </label>
          {isLoading ? <p className="text-sm text-fg4 py-3 text-center">Загрузка...</p>
            : visible.length === 0 ? (
              <div className="text-center py-5 space-y-2">
                <p className="text-sm text-fg4">
                  {docs.length === 0 ? 'Библиотека пуста.'
                    : queryTokens.length > 0 ? 'По материалу в библиотеке ничего не найдено.'
                    : 'Нет подходящих (непросроченных) документов.'}
                </p>
                <button onClick={enterSearch}
                  className="inline-flex items-center gap-2 px-3 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md">
                  <Globe size={14} /> Искать в интернете
                </button>
              </div>
            ) : (
              <div className="max-h-80 overflow-y-auto divide-y divide-muted border border-stroke rounded-md">
                {visible.map(({ d, expired, validUntil, score }) => (
                  <div key={d.id} className="flex items-center gap-2 px-3 py-2 hover:bg-brand-subtle transition-colors">
                    <button onClick={() => onPick(d.id)} className="flex-1 flex items-center gap-2 min-w-0 text-left">
                      <ShieldCheck size={14} className={expired ? 'text-fg4 shrink-0' : 'text-brand shrink-0'} />
                      <span className="flex-1 text-sm text-fg1 truncate">{d.displayName}</span>
                      {queryTokens.length > 0 && score > 0 && (
                        <span className="text-[10px] px-1.5 py-0.5 rounded bg-brand-subtle text-brand shrink-0">{Math.round(score * 100)}%</span>
                      )}
                      {validUntil && <span className={`text-[10px] shrink-0 ${expired ? 'text-danger' : 'text-fg4'}`}>
                        {expired ? 'просрочен ' : 'до '}{validUntil}</span>}
                    </button>
                    {d.scanBlobPath && (
                      <button onClick={() => void openAttachmentInNewTab(d.scanBlobPath!)} title="Просмотр скана (в новой вкладке)"
                        className="p-1 text-fg4 hover:text-brand shrink-0"><Eye size={14} /></button>
                    )}
                  </div>
                ))}
              </div>
            )}
        </div>
      )}

      {tab === 'search' && (
        <div className="space-y-2">
          <div className="flex items-center gap-2">
            <select value={searchType} onChange={e => setSearchType(e.target.value)}
              className="border border-stroke-strong rounded-md px-2 py-2 text-sm bg-surface text-fg1">
              {qualityTypes.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
            </select>
            <div className="flex-1 flex items-center gap-2 border border-stroke-strong rounded-md px-2">
              <Search size={14} className="text-fg4" />
              <input value={query} onChange={e => setQuery(e.target.value)}
                onKeyDown={e => { if (e.key === 'Enter') void runSearch(); }}
                placeholder="строка поиска" className="flex-1 py-2 text-sm bg-transparent focus:outline-none" />
            </div>
            <button onClick={() => runSearch()} disabled={searching || !query.trim()}
              className="flex items-center gap-1.5 px-3 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
              {searching ? <Loader2 size={14} className="animate-spin" /> : <Search size={14} />} Найти
            </button>
          </div>
          {searchError && <p className="text-sm text-danger">{searchError}</p>}
          {results && results.length === 0 && <p className="text-sm text-fg4 py-3 text-center">Ничего не найдено.</p>}
          {results && results.length > 0 && (
            <div className="max-h-80 overflow-y-auto divide-y divide-muted border border-stroke rounded-md">
              {results.map(c => (
                <div key={c.url} className="flex items-start gap-2 px-3 py-2">
                  <span className="text-[10px] uppercase px-1.5 py-0.5 rounded bg-base text-fg3 shrink-0 mt-0.5">{c.source}</span>
                  <div className="flex-1 min-w-0">
                    <a href={c.url} target="_blank" rel="noreferrer"
                      className="text-sm text-brand-hover hover:underline flex items-center gap-1">
                      <span className="truncate">{c.title || c.url}</span><ExternalLink size={11} className="shrink-0" />
                    </a>
                    {c.snippet && <p className="text-xs text-fg4 line-clamp-2">{c.snippet}</p>}
                  </div>
                  <button onClick={() => importAndLink(c)} disabled={importingUrl !== null}
                    className="flex items-center gap-1 px-2 py-1 text-xs bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50 shrink-0">
                    {importingUrl === c.url ? <Loader2 size={12} className="animate-spin" /> : <Download size={12} />}
                    В библиотеку
                  </button>
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {tab === 'create' && (
        <QualityDocForm allDocTypes={allDocTypes} scope={scope} scopeId={scopeId}
          onSaved={doc => onPick(doc.id)} onCancel={() => setTab('pick')} />
      )}

    </Modal>
  );
}

// ─── Вкладка «Документы качества» ───────────────────────────────────────────────

export function QualityLinksTab({ instance, setId, allDocTypes }: {
  instance: DocumentInstance; setId: string; allDocTypes: DocumentType[];
}) {
  const [scope, setScope] = useState<CatalogScope>('System');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [pickerOpen, setPickerOpen] = useState(false);
  const [suggestions, setSuggestions] = useState<LinkSuggestion[] | null>(null);
  const [suggestSel, setSuggestSel] = useState<Set<string>>(new Set());
  const [suggesting, setSuggesting] = useState(false);
  const [viewDoc, setViewDoc] = useState<QualityDocument | null>(null);

  const { data: preview, isFetching, refetch } = usePreviewDataSetBindings(instance.id);
  const { data: linksSystem = [] } = useListMaterialLinks({ scope: 'System' });
  const { data: linksSet = [] } = useListMaterialLinks({ scope: 'Set', scopeId: setId });
  const { data: docsSystem = [] } = useListQualityDocs({ scope: 'System' });
  const { data: docsSet = [] } = useListQualityDocs({ scope: 'Set', scopeId: setId });
  const setLinks = useSetMaterialLinks();
  const removeLink = useRemoveMaterialLink();

  const docById = useMemo(() => {
    const m = new Map<string, QualityDocument>();
    [...docsSet, ...docsSystem].forEach(d => { if (!m.has(d.id)) m.set(d.id, d); });
    return m;
  }, [docsSystem, docsSet]);
  const docName = useMemo(() => {
    const m = new Map<string, string>();
    docById.forEach((d, id) => m.set(id, d.displayName));
    return m;
  }, [docById]);

  const linkByKey = useMemo(() => {
    const m = new Map<string, { id: string; docId: string }>();
    // Set приоритетнее System
    [...linksSystem, ...linksSet].forEach(l => m.set(l.materialKey, { id: l.id, docId: l.qualityDocumentId }));
    return m;
  }, [linksSystem, linksSet]);

  // Ключи полей идентичности материала — из тэга material.identity (без хардкода имён).
  const identityKeys = useMemo(() => {
    const keys: string[] = [];
    for (const t of allDocTypes) {
      if (t.kind !== 'Composite') continue;
      for (const f of resolveEffectiveFields(t, allDocTypes))
        if (f.tags?.includes(FUNCTIONAL_TAG.materialIdentity)) keys.push(f.key);
    }
    return Array.from(new Set(keys));
  }, [allDocTypes]);

  // Материалы из набора данных (превью) И из реквизитов (массивы материал-типа).
  const materials = useMemo<MaterialRow[]>(() => {
    const rows: MaterialRow[] = [];
    const seen = new Set<string>();
    const add = (rec: Record<string, unknown>) => {
      const idValues = identityKeys
        .map(k => rec[k])
        .filter((v): v is string => typeof v === 'string' && v.trim().length > 0)
        .map(v => v.trim());
      if (idValues.length === 0) return;
      const key = normalizeKey(idValues[0]);
      if (!key || seen.has(key)) return;
      seen.add(key);
      rows.push({ key, label: idValues.join(' · '), idValues });
    };
    if (preview)
      for (const r of preview)
        if (r.mode === 'tabular' && Array.isArray(r.data))
          for (const row of r.data as Record<string, unknown>[]) add(row);
    const docType = allDocTypes.find(t => t.id === instance.documentTypeId);
    if (docType)
      for (const f of resolveEffectiveFields(docType, allDocTypes)) {
        if (f.type !== 'array' || !f.typeId) continue;
        const ct = allDocTypes.find(t => t.id === f.typeId);
        if (!ct || !resolveEffectiveFields(ct, allDocTypes).some(cf => cf.tags?.includes(FUNCTIONAL_TAG.materialIdentity))) continue;
        const arr = instance.requisites[f.key];
        if (Array.isArray(arr)) for (const el of arr) if (el && typeof el === 'object') add(el as Record<string, unknown>);
      }
    return rows;
  }, [preview, identityKeys, allDocTypes, instance]);

  // Связь материала ищем по ЛЮБОМУ полю идентичности (артикул ИЛИ наименование).
  const findLink = (m: MaterialRow) => {
    for (const v of m.idValues) { const l = linkByKey.get(normalizeKey(v)); if (l) return l; }
    return undefined;
  };

  // Авто-подсказки: лучший непросроченный документ из библиотеки по релевантности.
  const SUGGEST_MIN = 0.34;
  const libHays = useMemo(
    () => [...docById.values()].map(d => ({ doc: d, expired: isExpired(d, allDocTypes), stems: docHaystackStems(d) })),
    [docById, allDocTypes],
  );
  const suggestionByKey = useMemo(() => {
    const map = new Map<string, { doc: QualityDocument; score: number }>();
    const active = libHays.filter(h => !h.expired);
    if (active.length === 0) return map;
    for (const mat of materials) {
      if (findLink(mat)) continue;
      const qt = weighted(mat.idValues.join(' '));
      if (qt.length === 0) continue;
      let best: { doc: QualityDocument; score: number } | null = null;
      for (const h of active) { const s = relevance(qt, h.stems); if (s > (best?.score ?? 0)) best = { doc: h.doc, score: s }; }
      if (best && best.score >= SUGGEST_MIN) map.set(mat.key, best);
    }
    return map;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [materials, libHays, linkByKey]);

  function toggle(key: string) {
    setSelected(prev => { const n = new Set(prev); n.has(key) ? n.delete(key) : n.add(key); return n; });
  }

  async function handlePick(docId: string) {
    await setLinks.mutateAsync({ scope, scopeId: scope === 'Set' ? setId : null, materialKeys: [...selected], qualityDocumentId: docId });
    setPickerOpen(false);
    setSelected(new Set());
  }

  async function handleSuggest() {
    setSuggesting(true);
    try {
      const s = await suggestLinks({ setId, materials: materials.map(m => ({ key: m.key, name: m.label })) });
      setSuggestions(s);
      setSuggestSel(new Set(s.map(x => x.materialKey)));
    } finally { setSuggesting(false); }
  }

  // Принять предложенный документ для одной строки / для всех с подсказкой.
  async function acceptSuggestion(mat: MaterialRow, docId: string) {
    await setLinks.mutateAsync({ scope, scopeId: scope === 'Set' ? setId : null, materialKeys: [mat.key], qualityDocumentId: docId });
  }
  async function acceptAllSuggestions() {
    const byDoc = new Map<string, string[]>();
    for (const mat of materials) {
      const s = suggestionByKey.get(mat.key);
      if (!s) continue;
      const arr = byDoc.get(s.doc.id) ?? []; arr.push(mat.key); byDoc.set(s.doc.id, arr);
    }
    for (const [docId, keys] of byDoc)
      await setLinks.mutateAsync({ scope, scopeId: scope === 'Set' ? setId : null, materialKeys: keys, qualityDocumentId: docId });
  }

  async function applySuggestions() {
    const chosen = (suggestions ?? []).filter(s => suggestSel.has(s.materialKey));
    // группируем по документу — один вызов на документ
    const byDoc = new Map<string, string[]>();
    for (const s of chosen) {
      const arr = byDoc.get(s.qualityDocumentId) ?? [];
      arr.push(s.materialKey);
      byDoc.set(s.qualityDocumentId, arr);
    }
    for (const [docId, keys] of byDoc)
      await setLinks.mutateAsync({ scope, scopeId: scope === 'Set' ? setId : null, materialKeys: keys, qualityDocumentId: docId });
    setSuggestions(null);
  }

  const linkedCount = materials.filter(m => findLink(m)).length;
  const suggestCount = suggestionByKey.size;

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3 flex-wrap">
        <button onClick={() => refetch()} disabled={isFetching}
          className="flex items-center gap-2 text-sm px-3 py-2 rounded-md bg-muted text-fg2 disabled:opacity-50">
          {isFetching ? <Loader2 size={14} className="animate-spin" /> : <Search size={14} />} Обновить материалы
        </button>
        <span className="text-xs text-fg4">{materials.length} материалов · привязано {linkedCount}</span>
        <div className="ml-auto flex items-center gap-2">
          <label className="text-xs text-fg3">Область связи:</label>
          <select value={scope} onChange={e => setScope(e.target.value as CatalogScope)}
            className="border border-stroke rounded-md px-2 py-1 text-xs bg-surface text-fg1">
            <option value="System">Общая (System)</option>
            <option value="Set">Только этот комплект</option>
          </select>
        </div>
      </div>

      {materials.length === 0 ? (
        <p className="text-sm text-fg4 text-center py-6">
          Нет материалов. Настройте набор данных (вкладка «Данные») и нажмите «Обновить материалы».
        </p>
      ) : (
        <div className="border border-stroke rounded-lg overflow-hidden">
          <div className="max-h-[50vh] overflow-y-auto">
            <table className="w-full text-sm">
              <thead className="bg-base sticky top-0">
                <tr>
                  <th className="w-8 px-2 py-2"></th>
                  <th className="px-2 py-2 text-left font-medium text-fg3">Материал</th>
                  <th className="px-2 py-2 text-left font-medium text-fg3 w-2/5">Документ качества</th>
                </tr>
              </thead>
              <tbody>
                {materials.map(m => {
                  const link = findLink(m);
                  const suggestion = !link ? suggestionByKey.get(m.key) : undefined;
                  return (
                    <tr key={m.key} className="border-t border-muted hover:bg-base">
                      <td className="px-2 py-1.5 text-center">
                        <input type="checkbox" checked={selected.has(m.key)} onChange={() => toggle(m.key)}
                          className="w-4 h-4 rounded border-stroke-strong text-brand" />
                      </td>
                      <td className="px-2 py-1.5 text-fg1">{m.label}</td>
                      <td className="px-2 py-1.5">
                        {link ? (
                          <span className="flex items-center gap-1.5">
                            <ShieldCheck size={13} className="text-success shrink-0" />
                            <button onClick={() => { const d = docById.get(link.docId); if (d) setViewDoc(d); }}
                              title="Просмотреть документ"
                              className="flex-1 text-left text-brand-hover hover:underline truncate">
                              {docName.get(link.docId) ?? '(документ)'}
                            </button>
                            <button onClick={() => { const d = docById.get(link.docId); if (d) setViewDoc(d); }}
                              title="Просмотреть документ" className="p-0.5 text-fg4 hover:text-brand"><Eye size={13} /></button>
                            <button onClick={() => removeLink.mutate(link.id)} title="Снять связь"
                              className="p-0.5 text-fg4 hover:text-danger"><Unlink size={13} /></button>
                          </span>
                        ) : suggestion ? (
                          <span className="flex items-center gap-1.5">
                            <span className="text-[10px] px-1 py-0.5 rounded bg-brand-subtle text-brand shrink-0">{Math.round(suggestion.score * 100)}%</span>
                            <button onClick={() => setViewDoc(suggestion.doc)} title="Просмотреть предложенный документ"
                              className="flex-1 text-left text-fg3 italic hover:underline truncate">
                              {suggestion.doc.displayName}
                            </button>
                            <button onClick={() => void acceptSuggestion(m, suggestion.doc.id)} title="Привязать предложенный документ"
                              className="flex items-center gap-1 px-1.5 py-0.5 text-xs bg-brand hover:bg-brand-hover text-white rounded">
                              <Check size={12} /> привязать
                            </button>
                          </span>
                        ) : <span className="text-fg4">—</span>}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </div>
      )}

      <div className="flex items-center gap-3">
        <button onClick={() => setPickerOpen(true)} disabled={selected.size === 0}
          className="flex items-center gap-2 px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
          <Link2 size={14} /> Связать выбранные ({selected.size})
        </button>
        <button onClick={() => void acceptAllSuggestions()} disabled={suggestCount === 0 || setLinks.isPending}
          title="Привязать все предложенные из библиотеки документы"
          className="flex items-center gap-2 px-4 py-2 text-sm bg-brand-subtle text-brand rounded-md hover:bg-brand/15 disabled:opacity-50">
          <Check size={14} /> Принять предложения ({suggestCount})
        </button>
        <button onClick={handleSuggest} disabled={suggesting || materials.length === 0}
          title="Предложить связи по истории привязок (для строк, где нет подсказки из библиотеки)"
          className="flex items-center gap-2 px-4 py-2 text-sm border border-stroke rounded-md hover:bg-base disabled:opacity-50">
          {suggesting ? <Loader2 size={14} className="animate-spin" /> : <ShieldCheck size={14} className="text-fg3" />}
          Предложить по истории
        </button>
        {selected.size > 0 && (
          <button onClick={() => setSelected(new Set())} className="text-sm text-fg3 hover:text-fg2">Сбросить выбор</button>
        )}
      </div>

      <p className="text-xs text-fg4">
        Связь хранится по идентичности материала и подмешивается в поле документа качества при
        генерации — переживает переимпорт набора данных. Подсказки из библиотеки — по релевантности
        (без просроченных); «Принять предложения» привязывает их одним нажатием.
      </p>

      <LinkPickerModal open={pickerOpen} onClose={() => setPickerOpen(false)} allDocTypes={allDocTypes}
        scope={scope} scopeId={scope === 'Set' ? setId : null}
        materials={materials.filter(m => selected.has(m.key))} onPick={handlePick} />

      <Modal open={viewDoc !== null} onOpenChange={o => { if (!o) setViewDoc(null); }} title="Документ качества" extraWide>
        {viewDoc && (
          <QualityDocForm allDocTypes={allDocTypes} scope={viewDoc.scope} scopeId={viewDoc.scopeId ?? null}
            initial={viewDoc} onSaved={() => setViewDoc(null)} onCancel={() => setViewDoc(null)} />
        )}
      </Modal>

      <Modal open={suggestions !== null} onOpenChange={o => { if (!o) setSuggestions(null); }}
        title="Предложенные связи" wide
        footer={
          <div className="flex items-center gap-2">
            <button onClick={applySuggestions} disabled={suggestSel.size === 0 || setLinks.isPending}
              className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
              {setLinks.isPending ? 'Применение...' : `Применить выбранные (${suggestSel.size})`}
            </button>
            <button onClick={() => setSuggestions(null)} className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">Отмена</button>
          </div>
        }>
        {suggestions && suggestions.length === 0 ? (
          <p className="text-sm text-fg4 py-4 text-center">
            Подходящих документов не найдено. Свяжите несколько материалов вручную — дальше похожие предложатся автоматически.
          </p>
        ) : (
          <div className="divide-y divide-muted border border-stroke rounded-md max-h-[55vh] overflow-y-auto">
            {(suggestions ?? []).map(s => (
              <label key={s.materialKey} className="flex items-center gap-3 px-3 py-2 text-sm cursor-pointer hover:bg-base">
                <input type="checkbox" checked={suggestSel.has(s.materialKey)}
                  onChange={() => setSuggestSel(prev => { const n = new Set(prev); n.has(s.materialKey) ? n.delete(s.materialKey) : n.add(s.materialKey); return n; })}
                  className="w-4 h-4 rounded border-stroke-strong text-brand" />
                <span className="flex-1 truncate text-fg1">{s.materialName}</span>
                <span className="text-fg4">→</span>
                <span className="flex-1 truncate text-brand-hover">{s.docDisplayName}</span>
                <span className="text-xs text-fg4 shrink-0">{Math.round(s.score * 100)}%</span>
              </label>
            ))}
          </div>
        )}
      </Modal>
    </div>
  );
}
