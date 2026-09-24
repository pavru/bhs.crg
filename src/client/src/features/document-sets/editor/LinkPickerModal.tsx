import { useState, useMemo } from 'react';
import { ShieldCheck, Search, Globe, ExternalLink, Download, Eye } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { SearchInput } from '@/shared/ui/SearchInput';
import { TypePickerField } from '@/shared/ui/TypePickerField';
import type { PickType } from '@/shared/ui/TypePicker';
import {
  useListQualityDocs, searchQualityDocs, importQualityDocFromUrl,
  type SearchCandidate, type QualityDocument,
} from '@/shared/api/qualityDocs';
import type { DocumentType, CatalogScope } from '@/shared/api/types';
import { typeHasTag } from '@/shared/api/schema';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import { docHaystackStems, relevance, weighted } from './qualityMatch';
import { QualityDocForm } from '@/features/quality-docs/QualityDocForm';
import { docNumberOf } from '@/features/quality-docs/docIdentity';
import { formatDateRu } from '@/shared/utils/date';
import { recognizeAndUpdate } from '@/features/quality-docs/recognizeImported';
import { openAttachmentInNewTab } from '@/shared/api/attachments';
import { getValidUntil, isExpired } from './qualityValidity';
import type { MaterialRow } from './materialRow';

interface LinkPickerModalProps {
  open: boolean; onClose: () => void; allDocTypes: DocumentType[];
  scope: CatalogScope; scopeId: string | null; materials: MaterialRow[];
  onPick: (doc: QualityDocument) => void;
}

/** Выбор/создание документа качества для набора материалов. Переиспользуется экраном контроля
 *  связок (issue #555) — там материал приходит из самой связки, а не из набора данных.
 *
 *  <p>Тело монтируется по открытию (issue #858): вкладка, строка поиска и тип документа задаются
 *  инициализаторами состояния, а не эффектом «открылось — сбрось и заполни». Эффектом первый рендер
 *  успевал показать вкладку и запрос от ПРОШЛОГО открытия.</p> */
export function LinkPickerModal(props: LinkPickerModalProps) {
  /**
   * Выбранный тип для веб-поиска живёт в ОБЁРТКЕ, а не в теле (поймано ревью PR #862).
   *
   * <p>Прежний эффект сбрасывал при открытии всё, кроме него: `setSearchType(prev => prev || …)`
   * — то есть выбор человека переживал закрытие намеренно. Тело монтируется по открытию, и
   * инициализатор вернул бы «Сертификат соответствия» на каждом открытии; связывают же материалы
   * подряд, десятками, и выбор пришлось бы делать заново на каждый.</p>
   */
  const [searchType, setSearchType] = useState('');
  return props.open
    ? <LinkPickerModalBody {...props} searchType={searchType} setSearchType={setSearchType} />
    : null;
}

function LinkPickerModalBody({ onClose, allDocTypes, scope, scopeId, materials, onPick, searchType, setSearchType }:
  LinkPickerModalProps & { searchType: string; setSearchType: (v: string) => void }) {
  const count = materials.length;
  // Поисковый запрос формируем из выбранного материала (артикул + наименование).
  const baseQuery = useMemo(() => {
    const m = materials[0];
    if (!m) return '';
    return Array.from(new Set(m.idValues.map(s => s.trim()).filter(Boolean))).join(' ');
  }, [materials]);

  const [tab, setTab] = useState<'pick' | 'search' | 'create'>('pick');
  const [includeExpired, setIncludeExpired] = useState(false);
  // Единая строка поиска: фильтрует библиотеку и используется для веб-поиска. Заполняется
  // материалом ПРИ ЗАВЕДЕНИИ состояния — тело монтируется на открытие, так что это и есть «сброс
  // и инициализация при открытии», только без эффекта (issue #858).
  const [query, setQuery] = useState(baseQuery);
  // Библиотека грузится ЦЕЛИКОМ, без фильтра по области, и это не небрежность: scope здесь говорит,
  // где будет заведена СВЯЗКА (и где создастся новый документ на вкладке «Создать вручную»), а не из
  // чего можно выбирать. Пока областью по умолчанию была System, разница не замечалась; с дефолтом
  // «комплект» (#587) фильтр по области оставил бы пикер пустым при полной библиотеке на System — и
  // пользователь пошёл бы заново импортировать из интернета то, что у него уже есть.
  // Релевантность к материалу считается на клиенте.
  const { data: docs = [], isLoading } = useListQualityDocs({ enabled: true });

  const qualityTypes = useMemo(
    () => allDocTypes.filter(dt => dt.kind === 'Document' && !dt.isAbstract && typeHasTag(dt, FUNCTIONAL_TAG.typeQualityDocument, allDocTypes)),
    [allDocTypes],
  );

  // Пока человек тип не выбрал — «сертификат», иначе первый доступный. Умолчание вычисляем, а не
  // записываем в состояние: типы могут доехать позже, чем откроется окно.
  const effectiveSearchType = searchType
    || (qualityTypes.find(t => /сертификат/i.test(t.name)) ?? qualityTypes[0])?.id
    || '';
  const [results, setResults] = useState<SearchCandidate[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [importingUrl, setImportingUrl] = useState<string | null>(null);
  const [searchError, setSearchError] = useState('');
  // Определения типов полей нужны распознаванию импортированного скана (issue #654).
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const { data: enumTypes = [] } = useListEnumTypes();

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
    if (!effectiveSearchType) { setSearchError('Выберите тип документа'); return; }
    setImportingUrl(c.url); setSearchError('');
    try {
      let doc = await importQualityDocFromUrl({ url: c.url, title: c.title, documentTypeId: effectiveSearchType, scope, scopeId });
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
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title={title} extraWide>
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
              value={effectiveSearchType || undefined}
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
