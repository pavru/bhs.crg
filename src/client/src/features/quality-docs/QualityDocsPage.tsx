import { useState, useMemo, Fragment } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Plus, Pencil, Trash2, ShieldCheck, FileText, Search, Globe, ExternalLink, Download, Loader2, ChevronRight, ChevronDown } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { openAttachmentInNewTab } from '@/shared/api/attachments';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import {
  useListQualityDocs, useDeleteQualityDoc, searchQualityDocs, importQualityDocFromUrl,
  type QualityDocument, type SearchCandidate,
} from '@/shared/api/qualityDocs';
import type { CatalogScope } from '@/shared/api/types';
import { typeHasTag, findTaggedFieldPath } from '@/shared/api/schema';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import type { DocumentType } from '@/shared/api/types';
import { QualityDocForm } from './QualityDocForm';
import { recognizeAndUpdate } from './recognizeImported';

const SCOPE_LABEL: Record<string, string> = { System: 'Общая', Construction: 'Стройка', Section: 'Раздел', Set: 'Комплект' };
const SOURCE_LABEL: Record<string, string> = { file: 'Файл', fgis: 'ФГИС', manufacturer: 'Произв.', web: 'Веб' };
const NO_MANUFACTURER = '— без производителя —';

function readPath(obj: Record<string, unknown>, path: string[]): unknown {
  return path.reduce<unknown>((o, k) => (o && typeof o === 'object') ? (o as Record<string, unknown>)[k] : undefined, obj);
}
/** Производитель документа качества по функциональному тэгу quality.manufacturer. */
function getManufacturer(doc: QualityDocument, docTypes: DocumentType[]): string {
  const dt = docTypes.find(t => t.id === doc.documentTypeId);
  const path = dt ? findTaggedFieldPath(dt, FUNCTIONAL_TAG.qualityManufacturer, docTypes) : null;
  const v = path ? readPath(doc.requisites, path) : undefined;
  const s = typeof v === 'string' ? v.trim() : '';
  return s || NO_MANUFACTURER;
}
/** Номер документа по функциональному тэгу doc.number. */
function getDocNumber(doc: QualityDocument, docTypes: DocumentType[]): string {
  const dt = docTypes.find(t => t.id === doc.documentTypeId);
  const path = dt ? findTaggedFieldPath(dt, FUNCTIONAL_TAG.docNumber, docTypes) : null;
  const v = path ? readPath(doc.requisites, path) : undefined;
  return typeof v === 'string' ? v.trim() : '';
}

export function QualityDocsPage() {
  const qc = useQueryClient();
  const [search, setSearch] = useState('');
  const [scopeFilter, setScopeFilter] = useState<'all' | 'System'>('all');
  const [createOpen, setCreateOpen] = useState(false);
  const [editDoc, setEditDoc] = useState<QualityDocument | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<QualityDocument | null>(null);
  const [openGroups, setOpenGroups] = useState<Set<string>>(new Set()); // по умолчанию все свёрнуты

  // веб-поиск
  const [webQuery, setWebQuery] = useState('');
  const [candidates, setCandidates] = useState<SearchCandidate[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [importingUrl, setImportingUrl] = useState<string | null>(null);
  const [searchError, setSearchError] = useState('');

  const { data: docTypes = [] } = useListDocumentTypes();
  const { data: docs = [], isLoading } = useListQualityDocs({
    scope: scopeFilter === 'System' ? 'System' : undefined,
    search: search || undefined,
  });
  const del = useDeleteQualityDoc();

  const groups = useMemo(() => {
    const m = new Map<string, QualityDocument[]>();
    for (const d of docs) {
      const k = getManufacturer(d, docTypes);
      const arr = m.get(k); if (arr) arr.push(d); else m.set(k, [d]);
    }
    // «без производителя» — в конец, остальные по алфавиту
    return [...m.entries()].sort((a, b) =>
      a[0] === NO_MANUFACTURER ? 1 : b[0] === NO_MANUFACTURER ? -1 : a[0].localeCompare(b[0], 'ru'));
  }, [docs, docTypes]);
  const toggleGroup = (k: string) =>
    setOpenGroups(prev => { const n = new Set(prev); n.has(k) ? n.delete(k) : n.add(k); return n; });

  const typeName = (id: string) => docTypes.find(d => d.id === id)?.name ?? '—';
  const defaultTypeId = useMemo(() => {
    const q = docTypes.filter(d => d.kind === 'Document' && !d.isAbstract && typeHasTag(d, FUNCTIONAL_TAG.typeQualityDocument, docTypes));
    return q.find(d => /сертификат/i.test(d.name))?.id ?? q[0]?.id ?? '';
  }, [docTypes]);

  async function handleWebSearch() {
    if (!webQuery.trim()) return;
    setSearching(true); setSearchError(''); setCandidates(null);
    try { setCandidates(await searchQualityDocs(webQuery.trim())); }
    catch (e: unknown) {
      const resp = (e as { response?: { data?: { error?: string } } })?.response;
      setSearchError(resp?.data?.error ?? (e instanceof Error ? e.message : 'Ошибка поиска'));
    } finally { setSearching(false); }
  }

  async function handleImport(c: SearchCandidate) {
    if (!defaultTypeId) { setSearchError('Не найден тип «документ качества».'); return; }
    setImportingUrl(c.url); setSearchError('');
    try {
      const doc = await importQualityDocFromUrl({ url: c.url, title: c.title, documentTypeId: defaultTypeId, scope: 'System' as CatalogScope, scopeId: null });
      // Автоматически распознаём скан импортированного документа (best-effort).
      try { await recognizeAndUpdate(doc, docTypes); } catch { /* распознавание не критично */ }
      qc.invalidateQueries({ queryKey: ['quality-docs'] });
    } catch (e: unknown) {
      const resp = (e as { response?: { data?: { error?: string } } })?.response;
      setSearchError(resp?.data?.error ?? (e instanceof Error ? e.message : 'Не удалось импортировать'));
    } finally { setImportingUrl(null); }
  }

  return (
    <div className="px-6 py-4">
      <div className="flex items-center justify-between mb-4 gap-4 flex-wrap">
        <h1 className="text-xl font-semibold text-fg1 flex items-center gap-2">
          <ShieldCheck size={20} className="text-brand" /> Документы качества
        </h1>
        <button onClick={() => setCreateOpen(true)}
          className="flex items-center gap-2 bg-brand hover:bg-brand-hover text-white text-sm font-medium px-4 py-2 rounded-md transition-colors">
          <Plus size={16} /> Добавить документ
        </button>
      </div>

      {/* Веб-поиск (ФГИС → производитель → веб) */}
      <div className="border border-stroke rounded-lg p-4 mb-4 bg-surface">
        <div className="flex items-center gap-2 mb-2">
          <Globe size={15} className="text-brand" />
          <span className="text-sm font-medium text-fg2">Поиск документов в интернете</span>
        </div>
        <div className="flex items-center gap-2 flex-wrap">
          <input value={webQuery} onChange={e => setWebQuery(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') void handleWebSearch(); }}
            placeholder="Напр.: Выключатель автоматический EKF AV-10"
            className="flex-1 min-w-[260px] border border-stroke-strong rounded-md px-3 py-2 text-sm bg-surface text-fg1" />
          <button onClick={handleWebSearch} disabled={searching || !webQuery.trim()}
            className="flex items-center gap-2 px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
            {searching ? <Loader2 size={14} className="animate-spin" /> : <Search size={14} />} Найти
          </button>
        </div>
        {searchError && <p className="text-sm text-danger mt-2">{searchError}</p>}
        {candidates && (
          candidates.length === 0
            ? <p className="text-sm text-fg4 mt-3">Ничего не найдено.</p>
            : (
              <div className="mt-3 divide-y divide-muted border border-stroke rounded-md max-h-96 overflow-y-auto">
                {candidates.map(c => (
                  <div key={c.url} className="flex items-start gap-3 px-3 py-2">
                    <span className="text-[10px] px-1.5 py-0.5 rounded bg-brand-subtle text-brand shrink-0 mt-0.5">{SOURCE_LABEL[c.source] ?? c.source}</span>
                    <div className="min-w-0 flex-1">
                      <p className="text-sm text-fg1 truncate">{c.title || c.url}</p>
                      {c.snippet && <p className="text-xs text-fg4 line-clamp-2">{c.snippet}</p>}
                      <a href={c.url} target="_blank" rel="noopener noreferrer"
                        className="text-xs text-brand-hover inline-flex items-center gap-1 mt-0.5">
                        <ExternalLink size={11} /> Открыть
                      </a>
                    </div>
                    <button onClick={() => handleImport(c)} disabled={importingUrl === c.url}
                      title="Скачать файл по ссылке и добавить в библиотеку (если это прямой PDF/скан)"
                      className="flex items-center gap-1.5 text-xs px-2 py-1 rounded-md border border-stroke hover:bg-base disabled:opacity-50 shrink-0">
                      {importingUrl === c.url ? <Loader2 size={12} className="animate-spin" /> : <Download size={12} />} В библиотеку
                    </button>
                  </div>
                ))}
              </div>
            )
        )}
      </div>

      <div className="flex items-center gap-3 mb-4 flex-wrap">
        <div className="flex items-center gap-2 border border-stroke-strong rounded-md px-2 flex-1 min-w-[220px] max-w-md">
          <Search size={14} className="text-fg4" />
          <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Поиск по названию..."
            className="flex-1 py-2 text-sm bg-transparent focus:outline-none" />
        </div>
        <select value={scopeFilter} onChange={e => setScopeFilter(e.target.value as 'all' | 'System')}
          className="border border-stroke-strong rounded-md px-2 py-2 text-sm bg-surface text-fg1">
          <option value="all">Все области</option>
          <option value="System">Только общие (System)</option>
        </select>
      </div>

      <div className="border border-stroke rounded-lg overflow-hidden bg-surface">
        {isLoading ? (
          <p className="text-sm text-fg4 text-center py-8">Загрузка...</p>
        ) : docs.length === 0 ? (
          <p className="text-sm text-fg4 text-center py-8">Документов нет. Добавьте первый — можно загрузить скан и распознать реквизиты.</p>
        ) : (
          <table className="w-full text-sm">
            <thead className="bg-base border-b border-stroke">
              <tr>
                <th className="px-4 py-2.5 text-left font-medium text-fg3">Название</th>
                <th className="px-4 py-2.5 text-left font-medium text-fg3 w-56">Тип</th>
                <th className="px-4 py-2.5 text-left font-medium text-fg3 w-28">Область</th>
                <th className="px-4 py-2.5 text-center font-medium text-fg3 w-20">Скан</th>
                <th className="px-4 py-2.5 w-24"></th>
              </tr>
            </thead>
            <tbody className="divide-y divide-muted">
              {groups.map(([manuf, items]) => {
                const open = openGroups.has(manuf);
                return (
                  <Fragment key={manuf}>
                    <tr className="bg-base/60 hover:bg-base">
                      <td colSpan={5} className="p-0">
                        <button type="button" onClick={() => toggleGroup(manuf)} aria-expanded={open}
                          className="w-full flex items-center gap-2 px-3 py-2 text-sm font-medium text-fg2 text-left select-none focus:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-brand">
                          {open ? <ChevronDown size={14} className="text-fg4 shrink-0" /> : <ChevronRight size={14} className="text-fg4 shrink-0" />}
                          <span className="truncate">{manuf}</span>
                          <span className="text-xs text-fg4 font-normal">({items.length})</span>
                        </button>
                      </td>
                    </tr>
                    {open && items.map(d => (
                      <tr key={d.id} className="group hover:bg-base">
                        <td className="px-4 py-2.5 text-fg1 font-medium pl-9">
                          {d.displayName}
                          {(() => { const n = getDocNumber(d, docTypes); return n ? <span className="ml-2 text-xs text-fg4 font-normal">№ {n}</span> : null; })()}
                        </td>
                        <td className="px-4 py-2.5 text-fg3">{typeName(d.documentTypeId)}</td>
                        <td className="px-4 py-2.5 text-fg4 text-xs">{SCOPE_LABEL[d.scope] ?? d.scope}</td>
                        <td className="px-4 py-2.5 text-center">
                          {d.scanBlobPath
                            ? <button onClick={() => void openAttachmentInNewTab(d.scanBlobPath!)} title="Просмотр скана (в новой вкладке)"
                                className="text-success hover:text-brand transition-colors"><FileText size={14} className="inline" /></button>
                            : <span className="text-fg4">—</span>}
                        </td>
                        <td className="px-4 py-2.5">
                          <div className="flex items-center justify-end gap-1 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 transition-opacity">
                            <button onClick={() => setEditDoc(d)} className="p-1.5 text-fg4 hover:text-fg2 rounded" title="Редактировать">
                              <Pencil size={14} />
                            </button>
                            <button onClick={() => setDeleteTarget(d)}
                              disabled={del.isPending}
                              className="p-1.5 text-fg4 hover:text-danger rounded disabled:opacity-40" title="Удалить">
                              <Trash2 size={14} />
                            </button>
                          </div>
                        </td>
                      </tr>
                    ))}
                  </Fragment>
                );
              })}
            </tbody>
          </table>
        )}
      </div>

      <Modal open={createOpen} onOpenChange={setCreateOpen} title="Новый документ качества" wide>
        {createOpen && (
          <QualityDocForm allDocTypes={docTypes} scope={'System' as CatalogScope} scopeId={null}
            onSaved={() => setCreateOpen(false)} onCancel={() => setCreateOpen(false)} />
        )}
      </Modal>


      <Modal open={!!editDoc} onOpenChange={o => { if (!o) setEditDoc(null); }} title="Документ качества" wide>
        {editDoc && (
          <QualityDocForm allDocTypes={docTypes} scope={editDoc.scope} scopeId={editDoc.scopeId ?? null} initial={editDoc}
            onSaved={() => setEditDoc(null)} onCancel={() => setEditDoc(null)} />
        )}
      </Modal>

      <ConfirmDialog
        open={!!deleteTarget}
        onOpenChange={o => { if (!o) setDeleteTarget(null); }}
        title={`Удалить «${deleteTarget?.displayName ?? ''}»?`}
        description={<p>Связи с материалами также будут удалены.</p>}
        confirmLabel="Удалить"
        onConfirm={() => { if (deleteTarget) del.mutate(deleteTarget.id); }}
      />
    </div>
  );
}
