import { useState, useMemo, useEffect, type ReactNode } from 'react';
import {
  Loader2, Link2, Unlink, ShieldCheck, Search, Globe, ExternalLink, Download, Eye, Check,
  AlertTriangle, Replace,
} from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { SearchInput } from '@/shared/ui/SearchInput';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TypePickerField } from '@/shared/ui/TypePickerField';
import type { PickType } from '@/shared/ui/TypePicker';
import { usePreviewDataSetBindings } from '@/shared/api/datasets';
import { useGetDocumentSet } from '@/shared/api/documentSets';
import {
  useListQualityDocs, useListMaterialLinks, useSetMaterialLinks, useRemoveMaterialLink,
  suggestLinks, searchQualityDocs, importQualityDocFromUrl,
  type LinkSuggestion, type SearchCandidate, type QualityDocument, type MaterialLinkInput,
  type MaterialQualityLink,
} from '@/shared/api/qualityDocs';
import {
  SCOPE_LABELS, SCOPE_PRIORITY, type DocumentInstance, type DocumentType, type CatalogScope,
} from '@/shared/api/types';
import { ScopeIcon } from '@/shared/ui/ScopeIcon';
import { ScopeReachNote } from '@/features/quality-docs/ScopeReachNote';
import { scopeBreakdownText } from '@/features/quality-docs/linkScopes';
import { groupByTargetScope, needsFallbackScope, widestTargetScope, type LinkScope } from './linkTargets';
import {
  typeHasTag, findTaggedFieldPath, collectMaterialRows, materialIdentityKeys,
} from '@/shared/api/schema';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import { identityKey, isIdentityKeyEmpty, normalizeKey } from '@/shared/api/identityKey';
import {
  assessBulkLink, bestSuggestion, collectStrings, collidingIdentities, docHaystackStems,
  relevance, weighted, type BulkLinkAssessment,
} from './qualityMatch';
import { QualityDocForm } from '@/features/quality-docs/QualityDocForm';
import { docNumberOf } from '@/features/quality-docs/docIdentity';
import { formatDateRu } from '@/shared/utils/date';
import { recognizeAndUpdate } from '@/features/quality-docs/recognizeImported';
import { openAttachmentInNewTab } from '@/shared/api/attachments';
import { useToast } from '@/shared/ui/Toast';

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
// Сопоставление материала с документом — общий модуль qualityMatch (issue #552).

/**
 * Строка материала на вкладке: `key` — составной ключ идентичности (им связка и заводится, и
 * ищется), `label` — то же для человека, `idValues` — непустые значения для оценки релевантности.
 */
export interface MaterialRow { key: string; label: string; idValues: string[] }

// ─── Модалка выбора/создания документа для связывания ───────────────────────────

/** Выбор/создание документа качества для набора материалов. Переиспользуется экраном контроля
 *  связок (issue #555) — там материал приходит из самой связки, а не из набора данных. */
export function LinkPickerModal({ open, onClose, allDocTypes, scope, scopeId, materials, onPick }: {
  open: boolean; onClose: () => void; allDocTypes: DocumentType[];
  scope: CatalogScope; scopeId: string | null; materials: MaterialRow[];
  onPick: (doc: QualityDocument) => void;
}) {
  const count = materials.length;
  const [tab, setTab] = useState<'pick' | 'search' | 'create'>('pick');
  const [includeExpired, setIncludeExpired] = useState(false);
  // Единая строка поиска: фильтрует библиотеку и используется для веб-поиска.
  const [query, setQuery] = useState('');
  // Библиотека грузится ЦЕЛИКОМ, без фильтра по области, и это не небрежность: scope здесь говорит,
  // где будет заведена СВЯЗКА (и где создастся новый документ на вкладке «Создать вручную»), а не из
  // чего можно выбирать. Пока областью по умолчанию была System, разница не замечалась; с дефолтом
  // «комплект» (#587) фильтр по области оставил бы пикер пустым при полной библиотеке на System — и
  // пользователь пошёл бы заново импортировать из интернета то, что у него уже есть.
  // Релевантность к материалу считается на клиенте.
  const { data: docs = [], isLoading } = useListQualityDocs({ enabled: open });

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
  // Определения типов полей нужны распознаванию импортированного скана (issue #654).
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const { data: enumTypes = [] } = useListEnumTypes();

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
      score: relevance(queryTokens, docHaystackStems(d.displayName, d.requisites)), // релевантность 0..1 по всем реквизитам
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
      // Определения типов — чтобы распознавание увидело варианты перечислений и вернуло КОДЫ,
      // а не подписи (issue #654): формы здесь нет, расхождение показать некому.
      try { doc = await recognizeAndUpdate(doc, allDocTypes, { primitiveTypes, enumTypes }); }
      catch { /* распознавание не критично */ }
      onPick(doc);
    } catch (e: unknown) {
      setSearchError(e instanceof Error ? e.message : 'Не удалось импортировать');
    } finally { setImportingUrl(null); }
  }

  const tabLabel = { pick: 'Из библиотеки', search: 'Поиск в интернете', create: 'Создать вручную' };

  // Одиночный случай называем материалом (issue #680): «для 1 материал(ов)» не говорит, для какого
  // именно, а окно открывается из строки таблицы, где строк бывает под сотню.
  const title = count === 1 ? `Документ качества: ${materials[0].label}`
    : `Документ качества для ${count} материал(ов)`;

  return (
    <Modal open={open} onOpenChange={o => { if (!o) onClose(); }} title={title} extraWide>
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
          <SearchInput value={query} onChange={setQuery} placeholder="Поиск по материалу / названию..." />
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
                <Button variant="filled" size="sm" onClick={enterSearch} icon={<Globe size={14} />}>
                  Искать в интернете
                </Button>
              </div>
            ) : (
              <div className="max-h-80 overflow-y-auto divide-y divide-muted border border-stroke rounded-md">
                {visible.map(({ d, expired, validUntil, score }) => (
                  <div key={d.id} className="flex items-center gap-2 px-3 py-2 hover:bg-brand-subtle transition-colors">
                    <button onClick={() => onPick(d)} className="flex-1 flex items-center gap-2 min-w-0 text-left">
                      <ShieldCheck size={14} className={expired ? 'text-fg4 shrink-0' : 'text-brand shrink-0'} />
                      {/* Номер документа рядом с именем (issue #588): два сертификата в библиотеке
                          назывались одинаково, а внутри были разные номера, органы и области
                          продукции — по имени человек выбирал вслепую. */}
                      <span className="flex-1 min-w-0">
                        <span className="block text-sm text-fg1 truncate">{d.displayName}</span>
                        {/* Только НОМЕР: срок действия у строки уже есть справа, и вторая дата рядом
                            (да ещё в другом формате) читалась бы как два разных срока. */}
                        {docNumberOf(d, allDocTypes) && (
                          <span className="block text-[11px] text-fg4 truncate">№ {docNumberOf(d, allDocTypes)}</span>
                        )}
                      </span>
                      {queryTokens.length > 0 && score > 0 && (
                        <span className="text-[10px] px-1.5 py-0.5 rounded bg-brand-subtle text-brand shrink-0">{Math.round(score * 100)}%</span>
                      )}
                      {validUntil && <span className={`text-[10px] shrink-0 ${expired ? 'text-danger' : 'text-fg4'}`}>
                        {expired ? 'просрочен ' : 'до '}{formatDateRu(validUntil)}</span>}
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
            {/* w-64, а не w-52 (issue #668): «Сертификат соответствия» — типичное значение, и в
                208 px оно обрезалось многоточием почти сразу даже без кода типа. */}
            <TypePickerField className="w-64" aria-label="Тип документа качества" title="Тип документа качества"
              placeholder="Тип"
              types={qualityTypes.map<PickType>(t => ({ id: t.id, name: t.name, code: t.code, section: 'Документы качества' }))}
              value={searchType || undefined}
              onChange={id => { if (id) setSearchType(id); }} />
            {/* Ведущей лупы здесь нет (issue #668): рядом стоял селектор типа со своей замыкающей
                лупой — обещанием модалки поиска (#565), — и два одинаковых значка подряд означали
                разное. У самой строки намерение уже названо кнопкой «Найти» справа. */}
            <div className="flex-1 flex items-center gap-2 border border-stroke-strong rounded-md px-2 transition-colors focus-within:border-brand focus-within:ring-1 focus-within:ring-brand">
              <input value={query} onChange={e => setQuery(e.target.value)}
                onKeyDown={e => { if (e.key === 'Enter') void runSearch(); }}
                placeholder="строка поиска" className="flex-1 py-2 text-sm bg-transparent focus:outline-none" />
            </div>
            <Button variant="filled" size="sm" onClick={() => runSearch()} loading={searching}
              disabled={!query.trim()} icon={<Search size={14} />}>
              Найти
            </Button>
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
                  <Button variant="filled" size="sm" onClick={() => importAndLink(c)}
                    loading={importingUrl === c.url} disabled={importingUrl !== null}
                    icon={<Download size={12} />} className="shrink-0">
                    В библиотеку
                  </Button>
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {tab === 'create' && (
        <QualityDocForm allDocTypes={allDocTypes} scope={scope} scopeId={scopeId}
          onSaved={doc => onPick(doc)} onCancel={() => setTab('pick')} />
      )}

    </Modal>
  );
}

/**
 * Действие в строке материала (issue #680).
 *
 * Своя пилюля, а не `Button size="sm"`: у кнопки высота 32 px, а строк на живом реестре 130 — это
 * лишний экран прокрутки в таблице, которая и так скроллится внутри себя. Размер взят у пилюли
 * «привязать», которая в строке уже стояла, поэтому вертикальная цена нулевая.
 *
 * Тон говорит о роли: `brand` — согласие с догадкой машины (главное действие подсказки),
 * `tonal` — «сделаю выбор сам».
 */
function RowPill({ onClick, title, tone = 'tonal', children }: {
  onClick: () => void; title: string; tone?: 'brand' | 'tonal'; children: ReactNode;
}) {
  return (
    <button type="button" onClick={onClick} title={title}
      className={'flex items-center gap-1 px-1.5 py-0.5 text-xs rounded-full shrink-0 '
        + 'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand '
        + (tone === 'brand'
          ? 'bg-brand hover:bg-brand-hover text-on-brand'
          : 'bg-tonal text-on-tonal hover:brightness-[.97]')}>
      {children}
    </button>
  );
}

// ─── Вкладка «Документы качества» ───────────────────────────────────────────────

export function QualityLinksTab({ instance, setId, allDocTypes }: {
  instance: DocumentInstance; setId: string; allDocTypes: DocumentType[];
}) {
  // Дефолт — КОМПЛЕКТ (issue #587). Раньше стояла System, и привязка незаметно становилась
  // общесистемной: все 113 связей комплекта 250701.ЭОМ-1 оказались на System, ни одной ниже.
  // Узкая область — обратимая ошибка (не нашлась связка), широкая — тихая (чужой сертификат
  // подставился в чужой документ).
  const [scope, setScope] = useState<CatalogScope>('Set');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [pickerOpen, setPickerOpen] = useState(false);
  /**
   * Строка, для которой пикер открыт «в одиночку» (issue #680).
   *
   * Отдельно от `selected` намеренно: если подмешивать одиночную строку в общий выбор, закрытие
   * окна по Esc оставит её отмеченной — фантомный выбор и счётчик на нижней кнопке, которых человек
   * не заводил.
   */
  const [singleTarget, setSingleTarget] = useState<MaterialRow | null>(null);
  // Сводка перед массовой привязкой (issue #552) — показывается, только если что-то не сходится.
  const [pendingLink, setPendingLink] = useState<{
    docId: string; docName: string; chosen: MaterialRow[];
    assessment: BulkLinkAssessment<MaterialRow>;
  } | null>(null);
  const [suggestions, setSuggestions] = useState<LinkSuggestion[] | null>(null);
  const [suggestSel, setSuggestSel] = useState<Set<string>>(new Set());
  const [suggesting, setSuggesting] = useState(false);
  const [viewDoc, setViewDoc] = useState<QualityDocument | null>(null);
  // Разрыв связи — через подтверждение (issue #682): в проекте это общее правило для удаления, и
  // соседний экран контроля связок его уже соблюдает. Метку несём отдельно: у связки она бывает
  // пустой (до #554), а в строке имя материала под рукой.
  const [breaking, setBreaking] = useState<{ link: MaterialQualityLink; label: string } | null>(null);

  const { data: preview, isFetching, refetch } = usePreviewDataSetBindings({ ownerId: instance.id });
  // Цепочка уровней комплекта (issue #587): раздел и стройка нужны и чтобы ЗАВЕСТИ связку выше
  // комплекта, и чтобы УВИДЕТЬ заведённую там — резолвер их учитывает с самого начала.
  const { data: set } = useGetDocumentSet(setId);
  const sectionId = set?.sectionId ?? null;
  const constructionId = set?.constructionId ?? null;
  /** Уровень готов принимать связки: System живёт без id, остальным id обязателен. */
  const scopeReady = scope === 'System' || !!(scope === 'Set' ? setId : scope === 'Section' ? sectionId : constructionId);
  /**
   * Область ещё не готова принимать связки (комплект догружается) — говорим об этом.
   *
   * Молчаливый выход отсюда был бы хуже отказа: пользователь нажал «Привязать», ничего не
   * произошло, и объяснить это нечем — а окно осталось открытым с выбором, как будто всё идёт.
   */
  function scopeNotReady(): boolean {
    if (scopeReady) return false;
    linksToast.error('Область связи ещё не готова — комплект загружается. Повторите через мгновение.');
    return true;
  }
  const scopeId = scope === 'Set' ? setId
    : scope === 'Section' ? sectionId
    : scope === 'Construction' ? constructionId
    : null;

  const { data: linksSystem = [] } = useListMaterialLinks({ scope: 'System' });
  // enabled по наличию id: запрос уровня без scopeId вернул бы связки ВСЕХ разделов/строек.
  const { data: linksConstruction = [] } = useListMaterialLinks(
    { scope: 'Construction', scopeId: constructionId ?? undefined, enabled: !!constructionId });
  const { data: linksSection = [] } = useListMaterialLinks(
    { scope: 'Section', scopeId: sectionId ?? undefined, enabled: !!sectionId });
  const { data: linksSet = [] } = useListMaterialLinks({ scope: 'Set', scopeId: setId });
  const { data: docsSystem = [] } = useListQualityDocs({ scope: 'System' });
  const { data: docsConstruction = [] } = useListQualityDocs(
    { scope: 'Construction', scopeId: constructionId ?? undefined, enabled: !!constructionId });
  const { data: docsSection = [] } = useListQualityDocs(
    { scope: 'Section', scopeId: sectionId ?? undefined, enabled: !!sectionId });
  const { data: docsSet = [] } = useListQualityDocs({ scope: 'Set', scopeId: setId });
  const linksToast = useToast();
  const setLinks = useSetMaterialLinks();
  const removeLink = useRemoveMaterialLink();

  const docById = useMemo(() => {
    const m = new Map<string, QualityDocument>();
    // Порядок = приоритет: узкий уровень раньше широкого, первый победивший остаётся.
    [...docsSet, ...docsSection, ...docsConstruction, ...docsSystem]
      .forEach(d => { if (!m.has(d.id)) m.set(d.id, d); });
    return m;
  }, [docsSystem, docsConstruction, docsSection, docsSet]);
  const docName = useMemo(() => {
    const m = new Map<string, string>();
    docById.forEach((d, id) => m.set(id, d.displayName));
    return m;
  }, [docById]);

  /** Все связки всех четырёх уровней — один материал может нести не одну (см. shadowedBy). */
  const allLinks = useMemo(
    () => [...linksSystem, ...linksConstruction, ...linksSection, ...linksSet],
    [linksSystem, linksConstruction, linksSection, linksSet]);

  /**
   * Связка, которая вступит в силу, если снять эту, — или null.
   *
   * `linkByKey` держит только победившую, поэтому по экрану не видно, что под ней лежит другая. Без
   * этого подтверждение разрыва обещало бы «материал останется без документа качества», а в PDF
   * подставился бы документ с уровня пошире — и человек, который снимал связь именно чтобы её
   * убрать, узнал бы об этом из готового PDF.
   */
  const shadowedBy = (link: MaterialQualityLink): MaterialQualityLink | null => {
    const rivals = allLinks.filter(l => l.materialKey === link.materialKey && l.id !== link.id);
    if (rivals.length === 0) return null;
    return rivals.reduce((a, b) => (SCOPE_PRIORITY[a.scope] <= SCOPE_PRIORITY[b.scope] ? a : b));
  };

  const linkByKey = useMemo(() => {
    // Связка кладётся ЦЕЛИКОМ, а не парой id: её область нужна и для записи (перепривязка идёт в
    // область действующей связки, issue #681), и для предупреждения о том, куда достаёт разрыв.
    const m = new Map<string, MaterialQualityLink>();
    // Узкий уровень побеждает широкий — тот же приоритет, что у QualityLinkResolver на генерации
    // (Set=1 … System=5). Здесь это порядок перезаписи: последний записавший и остаётся.
    [...linksSystem, ...linksConstruction, ...linksSection, ...linksSet]
      .forEach(l => m.set(l.materialKey, l));
    return m;
  }, [linksSystem, linksConstruction, linksSection, linksSet]);

  // Ключи полей-идентификаторов МАТЕРИАЛА — по тэгам, без хардкода имён (issue #569).
  const identityKeys = useMemo(() => materialIdentityKeys(allDocTypes), [allDocTypes]);

  // Материалы из набора данных (превью) И из реквизитов (массивы материал-типа).
  const materials = useMemo<MaterialRow[]>(() => {
    const rows: MaterialRow[] = [];
    const seen = new Set<string>();
    const add = (rec: Record<string, unknown>) => {
      // Значения берём ПО ВСЕМ полям идентичности, сохраняя позиции: ключ составной (#582), и
      // выброшенное пустое значение сдвинуло бы все последующие компоненты.
      const ordered = identityKeys.map(k => (typeof rec[k] === 'string' ? (rec[k] as string) : null));
      // Легаси-маркер ссылки («🔗 …» — так когда-то хранились составные поля) не относится к
      // значению: с ним подпись на экране и ключ связи расходились бы с тем, что видит резолвер.
      const idValues = ordered
        .map(v => (v ?? '').replace(/🔗/g, ' ').replace(/\s+/g, ' ').trim())
        .filter(v => v.length > 0);
      if (idValues.length === 0) return;
      const key = identityKey(ordered);
      if (isIdentityKeyEmpty(key) || seen.has(key)) return;
      seen.add(key);
      rows.push({ key, label: idValues.join(' · '), idValues });
    };
    if (preview)
      for (const r of preview)
        if (r.mode === 'tabular' && Array.isArray(r.data))
          for (const row of r.data as Record<string, unknown>[]) add(row);
    const docType = allDocTypes.find(t => t.id === instance.documentTypeId);
    // По всей глубине реквизитов (issue #648): в АОСР материалы лежат внутри union-обёртки
    // «массив ИЛИ ссылка на реестр» (#320), и обход только верхнего уровня их не видел.
    if (docType)
      for (const row of collectMaterialRows(docType, allDocTypes, instance.requisites)) add(row);
    return rows;
  }, [preview, identityKeys, allDocTypes, instance]);

  // Строки, спорящие за одну связку (issue #585): совпало значение идентичности, но не ключ целиком.
  const colliding = useMemo(() => collidingIdentities(materials, normalizeKey), [materials]);

  // Связь ищем по СОСТАВНОМУ ключу материала — у него ровно один ключ (#582). Прежний перебор «любое
  // поле идентичности» позволял двум связкам претендовать на один материал: на живых данных из 151
  // строки реестра у 49 нашлась связка и по артикулу, и по наименованию, и во всех 49 сертификаты
  // были разные — побеждала именная, потому что «Наименование» стоит в схеме раньше «Артикула».
  const findLink = (m: MaterialRow) => linkByKey.get(m.key);

  // Авто-подсказки: лучший непросроченный документ из библиотеки (правило — в bestSuggestion, там
  // же и порог, общий с ручной проверкой: до issue #682 их было два, и совпадали они случайно).
  const libHays = useMemo(
    () => [...docById.values()].map(d => ({ doc: d, expired: isExpired(d, allDocTypes), stems: docHaystackStems(d.displayName, d.requisites) })),
    [docById, allDocTypes],
  );
  const suggestionByKey = useMemo(() => {
    const map = new Map<string, { doc: QualityDocument; score: number }>();
    const active = libHays.filter(h => !h.expired);
    if (active.length === 0) return map;
    for (const mat of materials) {
      if (findLink(mat)) continue;
      const best = bestSuggestion(mat, active);
      if (best) map.set(mat.key, best);
    }
    return map;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [materials, libHays, linkByKey]);

  function toggle(key: string) {
    setSelected(prev => { const n = new Set(prev); n.has(key) ? n.delete(key) : n.add(key); return n; });
  }

  /** Название документа + все его строковые реквизиты — «стог», с которым сравниваем материал. */
  function docHaystackText(doc: QualityDocument): string[] {
    const parts: string[] = [doc.displayName];
    collectStrings(doc.requisites as Record<string, unknown>, parts);
    return parts;
  }

  /** Область действующей связки материала — та, в которую его перепривязка и обязана писать. */
  const existingScopeOf = (m: { key: string }): LinkScope | undefined => {
    const l = linkByKey.get(m.key);
    return l ? { scope: l.scope, scopeId: l.scopeId ?? null } : undefined;
  };

  /** Пикер открыт для одной строки — иначе для всего отмеченного. */
  const pickerRows = singleTarget ? [singleTarget] : materials.filter(m => selected.has(m.key));

  /**
   * Область, которую видит пикер: САМАЯ ШИРОКАЯ из тех, куда лягут связки.
   *
   * На выбор из библиотеки она не влияет (библиотека грузится целиком), а вот новый документ —
   * созданный вручную или импортированный из интернета — заводится именно в ней. Взять сюда область
   * из селектора, когда связка пишется выше, значит завести документ уже, чем связку на него: в
   * другом комплекте связка найдётся, а документ — нет, и в строке останется «(документ)» вместо
   * имени. Соседний экран решает то же самое и так же (QualityDocLinks).
   */
  const pickerScope = widestTargetScope(pickerRows, existingScopeOf, { scope, scopeId });

  function openPickerFor(m: MaterialRow) {
    setSingleTarget(m);
    setPickerOpen(true);
  }

  function openPickerForSelected() {
    setSingleTarget(null); // иначе окно откроется для строки, оставшейся от прошлого одиночного захода
    setPickerOpen(true);
  }

  function closePicker() {
    setPickerOpen(false);
    setSingleTarget(null);
  }

  /**
   * Выбран документ для привязки. Прежде чем связывать — СВОДКА (issue #552).
   *
   * Отсюда и родились 68 неверных связок из 69: отметить весь список материалов и ткнуть один
   * сертификат можно было без единого сигнала, а после привязки все строки выглядели одинаково
   * благополучно. Запрещать нельзя — у артикулов сравнивать нечего, и запрет ломал бы честный
   * сценарий; поэтому предупреждаем и показываем, чего именно не сходится.
   */
  async function handlePick(doc: QualityDocument) {
    const chosen = pickerRows;
    const assessment = assessBulkLink(chosen, m => m.label, docHaystackText(doc));

    if (assessment.mismatched.length > 0) {
      setPickerOpen(false);
      setPendingLink({ docId: doc.id, docName: doc.displayName, chosen, assessment });
      return;
    }
    await linkChosen(doc.id, chosen);
  }

  /**
   * Единственная точка записи связок (issue #681).
   *
   * Строка со связкой пишется в область ЭТОЙ связки, строка без связки — в область из селектора.
   * Одной областью на всех отправлять нельзя: команда апсертит по тройке (область, объект, ключ),
   * и запись с другой областью заводит вторую связку, оставив прежнюю действовать в других
   * комплектах. Возвращает false, если отправлять было некуда, — вызывающий тогда не закрывает
   * окно и не сбрасывает выбор.
   */
  async function linkMaterials(docId: string, rows: readonly MaterialLinkInput[]): Promise<boolean> {
    // Уровень выбран, а его id ещё не доехал (комплект перезагружается) — связку класть некуда:
    // с пустым scopeId она стала бы «на все разделы разом», чего в селекторе никто не выбирал.
    // Спрашиваем, только если селектор вообще участвует: перепривязке в своей области он не нужен.
    if (needsFallbackScope(rows, existingScopeOf) && scopeNotReady()) return false;
    // Метку материала кладём в связку сразу (issue #554): человеческое имя есть только здесь —
    // строки наборов данных не хранятся, и на экране контроля восстановить его будет неоткуда.
    for (const group of groupByTargetScope(rows, existingScopeOf, { scope, scopeId }))
      await setLinks.mutateAsync({
        scope: group.scope, scopeId: group.scopeId,
        materials: group.materials.map(m => ({ key: m.key, label: m.label })), qualityDocumentId: docId,
      });
    return true;
  }

  async function linkChosen(docId: string, chosen: MaterialRow[]) {
    if (!await linkMaterials(docId, chosen)) return;
    closePicker();
    setPendingLink(null);
    // Выбор сбрасываем только у массового пути: одиночная привязка его не заводила и трогать
    // отмеченные пользователем строки не должна.
    if (!singleTarget) setSelected(new Set());
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
    await linkMaterials(docId, [{ key: mat.key, label: mat.label }]);
  }
  /**
   * Показать подсказки из библиотеки в том же обзоре, что и подсказки по истории (issue #682).
   *
   * Раньше эта кнопка привязывала N связок одним нажатием без всякого обзора — при том что ручная
   * массовая привязка предупреждает, а модалка «Предложенные связи» рядом делает ровно этот обзор
   * для подсказок по истории. Два соседних действия с одним смыслом вели себя противоположно.
   * Подтверждающий диалог был бы здесь неверным инструментом: все предложения по построению
   * проходят порог, и диалог приучал бы жать «да».
   */
  function reviewLibrarySuggestions() {
    const list: LinkSuggestion[] = [];
    for (const mat of materials) {
      const s = suggestionByKey.get(mat.key);
      if (!s) continue;
      list.push({
        materialKey: mat.key, materialName: mat.label,
        qualityDocumentId: s.doc.id, docDisplayName: s.doc.displayName, score: s.score,
      });
    }
    setSuggestions(list);
    setSuggestSel(new Set(list.map(x => x.materialKey)));
  }

  async function applySuggestions() {
    const chosen = (suggestions ?? []).filter(s => suggestSel.has(s.materialKey));
    // группируем по документу — один вызов на документ (внутри разойдётся ещё и по областям)
    const byDoc = new Map<string, MaterialLinkInput[]>();
    for (const s of chosen) {
      const arr = byDoc.get(s.qualityDocumentId) ?? [];
      // у подсказки имя материала своё — оно же показывалось человеку в списке предложений
      arr.push({ key: s.materialKey, label: s.materialName });
      byDoc.set(s.qualityDocumentId, arr);
    }
    for (const [docId, items] of byDoc)
      if (!await linkMaterials(docId, items)) return;
    setSuggestions(null);
  }

  const linkedCount = materials.filter(m => findLink(m)).length;
  const suggestCount = suggestionByKey.size;

  /** Выбранные строки, чья связка живёт ШИРЕ выбранного в селекторе уровня, — им правка уйдёт туда. */
  const widerThanSelector = materials
    .filter(m => selected.has(m.key))
    .map(m => linkByKey.get(m.key))
    .filter((l): l is MaterialQualityLink => !!l && SCOPE_PRIORITY[l.scope] > SCOPE_PRIORITY[scope]);

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
          {/* Четыре уровня (issue #587) — ровно те, что разрешает резолвер. Раньше их было два, и
              «общая» стояла по умолчанию: узкая ошибка обратима (связка не нашлась), широкая тиха
              (чужой сертификат подставился в чужой документ). Уровень без известного id не
              предлагаем — привязку было бы некуда положить. */}
          <Select value={scope} onValueChange={v => setScope(v as CatalogScope)}
            aria-label="Область связи" className="w-56">
            <SelectItem value="Set">Только этот комплект</SelectItem>
            {sectionId && <SelectItem value="Section">Весь раздел</SelectItem>}
            {constructionId && <SelectItem value="Construction">Вся стройка</SelectItem>}
            {/* Слово «Система» — из общего словаря областей (issue #649): экран контроля называет
                этот уровень так же, и выбирать одним словом, а читать другое человек не должен. */}
            <SelectItem value="System">Все стройки (Система)</SelectItem>
          </Select>
        </div>
      </div>

      {materials.length === 0 ? (
        <p className="text-sm text-fg4 text-center py-6">
          Нет материалов. Настройте набор данных (кнопка «Источники» в шапке документа) и нажмите «Обновить материалы».
        </p>
      ) : (
        <div className="border border-stroke rounded-lg overflow-hidden">
          <div className="max-h-[50vh] overflow-y-auto">
            {/* table-fixed — ради ВИДИМОСТИ действий, а не ради вида. Ячейка таблицы с обычной
                раскладкой берёт ширину по содержимому и `truncate` в ней не работает: на живом
                реестре строка выходила 1776 px в контейнере 1550, и правая колонка с «Разорвать»
                уезжала за край — добраться до неё можно было только горизонтальной прокруткой
                внутри таблицы. Фиксированная раскладка берёт ширины из шапки, и обрезка начинает
                действовать (issue #680). */}
            <table className="w-full table-fixed text-sm">
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
                      <td className="px-2 py-1.5 text-fg1">
                        <span className="flex items-center gap-1.5">
                          {/* min-w-0 — не косметика: без него `truncate` не ужимает имя, минимальная
                              ширина ячейки равна всей строке, и таблица разъезжается шире
                              контейнера. Действия правой колонки при этом уезжают за край экрана —
                              то есть аффорданс, ради видимости которого затевался issue #680,
                              достаётся только тому, кто догадался прокрутить таблицу вбок. */}
                          <span className="truncate min-w-0">{m.label}</span>
                          {/* Строка спорит с другой за связку (issue #585): совпало значение, но не
                              ключ целиком. Различить их система не может — решение за человеком. */}
                          {colliding.has(m.key) && (
                            <span
                              title={`Совпадает с другой строкой по «${colliding.get(m.key)!.join('», «')}», но ключ целиком разный. `
                                + 'Либо это одна позиция, записанная по-разному — тогда исправьте материал, '
                                + 'либо разные товары — тогда заведите две связки.'}
                              className="shrink-0 text-warning">
                              <AlertTriangle size={13} />
                            </span>
                          )}
                        </span>
                      </td>
                      <td className="px-2 py-1.5">
                        {link ? (
                          <span className="flex items-center gap-1.5">
                            <ShieldCheck size={13} className="text-success shrink-0" />
                            {/* Уровень показываем, ТОЛЬКО когда он расходится с селектором (issue
                                #681): в обычном случае он у всех строк один, и повторённый 130 раз
                                значок стал бы фоном. А расхождение — ровно то место, где человек
                                думает, что правит связку комплекта, а правит общесистемную. */}
                            {(link.scope !== scope || (link.scopeId ?? null) !== scopeId) && (
                              <ScopeIcon scope={link.scope}
                                title={`Связка заведена на уровне «${SCOPE_LABELS[link.scope]}», а в селекторе выбрано `
                                  + `«${SCOPE_LABELS[scope]}». Перепривязка изменит её на своём уровне — то есть всюду, `
                                  + 'куда этот уровень достаёт.'} />
                            )}
                            {/* Имя документа — единственный вход в просмотр: рядом стояла иконка
                                Eye с тем же обработчиком и той же подсказкой (issue #682). Место,
                                которое она занимала, ушло под «Перепривязать». */}
                            <button onClick={() => { const d = docById.get(link.qualityDocumentId); if (d) setViewDoc(d); }}
                              title="Просмотреть документ"
                              className="flex-1 min-w-0 text-left text-brand-hover hover:underline truncate">
                              {docName.get(link.qualityDocumentId) ?? '(документ)'}
                            </button>
                            {/* Починка неверной связки — перепривязкой, а не разрывом: разрыв меняет
                                одну ошибку («не тот документ») на другую («документа нет»). До этого
                                единственный путь починки шёл через destructive-действие. */}
                            <button onClick={() => openPickerFor(m)} title="Перепривязать к другому документу"
                              className="p-0.5 text-fg4 hover:text-brand"><Replace size={13} /></button>
                            <button onClick={() => setBreaking({ link, label: m.label })} title="Снять связь"
                              className="p-0.5 text-fg4 hover:text-danger"><Unlink size={13} /></button>
                          </span>
                        ) : suggestion ? (
                          <span className="flex items-center gap-1.5">
                            <span className="text-[10px] px-1 py-0.5 rounded bg-brand-subtle text-brand shrink-0">{Math.round(suggestion.score * 100)}%</span>
                            <button onClick={() => setViewDoc(suggestion.doc)} title="Просмотреть предложенный документ"
                              className="flex-1 min-w-0 text-left text-fg3 italic hover:underline truncate">
                              {suggestion.doc.displayName}
                            </button>
                            <RowPill tone="brand" onClick={() => void acceptSuggestion(m, suggestion.doc.id)}
                              title="Привязать предложенный документ">
                              <Check size={12} /> привязать
                            </RowPill>
                            {/* Подсказка — это согласие с догадкой машины, а не вход в выбор (issue
                                #680). Промахнулась догадка — до этой кнопки уйти из строки было
                                некуда, кроме как через чекбокс и кнопку под таблицей. */}
                            <RowPill onClick={() => openPickerFor(m)} title="Выбрать другой документ качества">
                              другой
                            </RowPill>
                          </span>
                        ) : (
                          <span className="flex items-center gap-1.5">
                            <span className="flex-1 min-w-0 text-fg4">—</span>
                            {/* Кнопка ВИДИМАЯ, не по hover: необнаруживаемость одиночного пути и есть
                                предмет жалобы, а мерцающая колонка на 130 строках недоступна ни с
                                клавиатуры, ни с тача. */}
                            <RowPill onClick={() => openPickerFor(m)} title="Выбрать документ качества для этого материала">
                              <Link2 size={12} /> Связать
                            </RowPill>
                          </span>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {/* Панель массового действия — только при непустом выборе (issue #680). Постоянно висевшая
          выключенная «Связать выбранные (0)» была единственным местом на экране, где произносилось
          слово «Связать», и обучала, что работа делается отсюда; теперь то же слово стоит в каждой
          непривязанной строке, а колонка чекбоксов остаётся сигналом массового пути. */}
      {selected.size > 0 && (
        <div className="sticky bottom-0 rounded-md border border-stroke bg-surface px-3 py-2 shadow-sm">
          <div className="flex items-center gap-2">
            <span className="text-sm text-fg2">Выбрано: {selected.size}</span>
            <Button variant="filled" size="sm" onClick={openPickerForSelected} icon={<Link2 size={13} />}>
              Связать выбранные ({selected.size})
            </Button>
            <button onClick={() => setSelected(new Set())}
              className="ml-auto text-xs text-fg4 hover:text-fg2">Снять выбор</button>
          </div>
          {/* Часть выбранных уже связана ШИРЕ, чем выбрано в селекторе, и правка уйдёт на их
              уровень — за пределы этого комплекта. В строке уровень виден значком, но на сотне
              строк его никто не пересчитывает, а панель говорит только «Выбрано: N». Показываем
              здесь, до нажатия: не диалогом — предупреждение, на которое нельзя ответить «нет»,
              приучает жать «да». */}
          {widerThanSelector.length > 0 && (
            <p className="mt-1.5 text-xs text-warning">
              Шире выбранного уровня — {widerThanSelector.length} ({scopeBreakdownText(widerThanSelector)}):
              {' '}документ сменится на уровне самой связки, то есть и в других комплектах.
            </p>
          )}
        </div>
      )}

      <div className="flex items-center gap-3">
        <button onClick={reviewLibrarySuggestions} disabled={suggestCount === 0}
          title="Посмотреть подсказки из библиотеки и привязать выбранные"
          className="flex items-center gap-2 px-4 py-2 text-sm bg-brand-subtle text-brand rounded-md hover:bg-brand/15 disabled:opacity-50">
          <Check size={14} /> Предложения из библиотеки ({suggestCount})
        </button>
        <button onClick={handleSuggest} disabled={suggesting || materials.length === 0}
          title="Предложить связи по истории привязок (для строк, где нет подсказки из библиотеки)"
          className="flex items-center gap-2 px-4 py-2 text-sm border border-stroke rounded-md hover:bg-base disabled:opacity-50">
          {suggesting ? <Loader2 size={14} className="animate-spin" /> : <ShieldCheck size={14} className="text-fg3" />}
          Предложить по истории
        </button>
      </div>

      <p className="text-xs text-fg4">
        Связь хранится по идентичности материала и подмешивается в поле документа качества при
        генерации — переживает переимпорт набора данных. Подсказки из библиотеки — по релевантности
        (без просроченных); их можно принять по одной в строке или разобрать списком.
      </p>

      {/* Сводка перед массовой привязкой (issue #552). Появляется, ТОЛЬКО если что-то не сходится:
          у артикулов сравнивать нечего, и показывать её всегда значило бы приучить нажимать «да». */}
      <ConfirmDialog
        open={!!pendingLink} onOpenChange={o => { if (!o) { setPendingLink(null); setSingleTarget(null); } }}
        title="Материалы не похожи на этот документ"
        description={pendingLink ? (
          <div className="space-y-2">
            {/* Один материал называем по имени: «из 1 выбранных материалов ему соответствуют 0» —
                это отчёт о выборке там, где речь об одной строке (issue #680). */}
            {pendingLink.chosen.length === 1 ? (
              <p>
                Материал <b>{pendingLink.chosen[0].label}</b> не похож на документ
                {' '}<b>{pendingLink.docName}</b>: ни одно его слово в документе не встречается.
              </p>
            ) : (
              <p>
                Документ <b>{pendingLink.docName}</b>: из {pendingLink.chosen.length} выбранных
                материалов ему соответствуют {pendingLink.assessment.fits.length},
                {' '}не похожи — <b>{pendingLink.assessment.mismatched.length}</b>
                {pendingLink.assessment.unverifiable.length > 0
                  && `, ещё ${pendingLink.assessment.unverifiable.length} проверить нечем (только артикул)`}.
              </p>
            )}
            {pendingLink.chosen.length > 1 && (
              <ul className="text-xs text-fg3 space-y-0.5 max-h-40 overflow-y-auto">
                {pendingLink.assessment.mismatched.slice(0, 8).map(m => (
                  <li key={m.key} className="truncate">• {m.label}</li>
                ))}
                {pendingLink.assessment.mismatched.length > 8 && (
                  <li>… и ещё {pendingLink.assessment.mismatched.length - 8}</li>
                )}
              </ul>
            )}
            <p className="text-xs text-fg4">
              Проверка приблизительная — она сравнивает слова материала с текстом документа. Если
              документ действительно тот, привязывайте.
            </p>
          </div>
        ) : ''}
        confirmLabel="Всё равно привязать" errorTitle="Не удалось привязать"
        onConfirm={() => { if (pendingLink) return linkChosen(pendingLink.docId, pendingLink.chosen); }}
      />

      {/* Область — только для показа библиотеки и создания документа. Куда ляжет связка, решает
          linkMaterials: у строки со связкой это область ЕЁ связки (issue #681). */}
      <ConfirmDialog
        open={!!breaking} onOpenChange={o => { if (!o) setBreaking(null); }}
        title="Разорвать связь?"
        description={breaking ? (
          <>
            <p className="mb-2">{breaking.label}</p>
            {shadowedBy(breaking.link) ? (
              <p>Материал без документа качества НЕ останется: под этой связкой лежит другая, уровня
                {' '}«{SCOPE_LABELS[shadowedBy(breaking.link)!.scope]}», с документом
                {' '}<b>{shadowedBy(breaking.link)!.qualityDocumentName}</b> — при генерации
                подставится он. Чтобы материал остался пустым, снимите и её.</p>
            ) : (
              <p>Материал останется без документа качества — при генерации поле документа качества
                будет пустым.</p>
            )}
            <ScopeReachNote links={[breaking.link]} />
          </>
        ) : ''}
        confirmLabel="Разорвать" errorTitle="Не удалось снять связь"
        onConfirm={() => { if (breaking) return removeLink.mutateAsync(breaking.link.id); }}
      />

      {/* Область здесь — только про показ библиотеки и создание нового документа. Куда ляжет
          связка, решает linkMaterials: у строки со связкой это область ЕЁ связки (issue #681). */}
      <LinkPickerModal open={pickerOpen} onClose={closePicker} allDocTypes={allDocTypes}
        scope={pickerScope.scope} scopeId={pickerScope.scopeId} materials={pickerRows} onPick={handlePick} />

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
            <Button variant="filled" onClick={applySuggestions} loading={setLinks.isPending}
              disabled={suggestSel.size === 0}>
              {setLinks.isPending ? 'Применение...' : `Применить выбранные (${suggestSel.size})`}
            </Button>
            <Button variant="text" onClick={() => setSuggestions(null)}>Отмена</Button>
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
