import { useState, useMemo, useEffect } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import {
  Plus, Pencil, Trash2, ShieldCheck, FileText, Search, Globe, ExternalLink, Download, Loader2,
  AlertTriangle, Clock, CircleSlash, Link2, Unlink,
} from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { RowActionsMenu, type RowAction } from '@/shared/ui/RowActionsMenu';
import { ListDetailShell, NavSearchInput, NavSection, NavItem } from '@/shared/ui/ListDetailShell';
import { openAttachmentInNewTab } from '@/shared/api/attachments';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import {
  useListQualityDocs, useDeleteQualityDoc, useListMaterialLinks, useRemoveMaterialLinks, searchQualityDocs,
  importQualityDocFromUrl, type QualityDocument, type SearchCandidate, type MaterialQualityLink,
} from '@/shared/api/qualityDocs';
import { SCOPE_LABELS, type CatalogScope } from '@/shared/api/types';
import { resolveEffectiveFields, typeHasTag } from '@/shared/api/schema';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import type { DocumentType } from '@/shared/api/types';
import { QualityDocForm } from './QualityDocForm';
import { ambiguousDocNames, docFieldByTag, docNumberOf, isAmbiguous } from './docIdentity';
import { recognizeAndUpdate } from './recognizeImported';
import { QualityDocLinks, matchesLink } from './QualityDocLinks';
import { ScopeReachNote } from './ScopeReachNote';
import { docState, EXPIRING_SOON_DAYS, type DocState } from './docState';
import { useRememberedSelection } from '@/shared/hooks/useRememberedSelection';

// Метки областей — только из общего словаря (issue #649). Локальный дубль называл System «Общей»,
// и на одном экране уровень документа и уровень его связок читались бы разными словами.
const SOURCE_LABEL: Record<string, string> = { file: 'Файл', fgis: 'ФГИС', manufacturer: 'Произв.', web: 'Веб' };

/** Значение реквизита по функциональному тэгу — общий модуль опознания документа (issue #588):
 *  имена полей не хардкодим, для того тэги и заведены. */
const byTag = docFieldByTag;

const STATE_META: Record<DocState, { label: string; icon: typeof AlertTriangle; danger: boolean }> = {
  expired: { label: 'Просрочен, связки живы', icon: AlertTriangle, danger: true },
  expiring: { label: `Истекает ≤ ${EXPIRING_SOON_DAYS} дней`, icon: Clock, danger: false },
  unlinked: { label: 'Без связок', icon: CircleSlash, danger: false },
};

// Выбор в URL + память последнего открытого (issue #787, общий хелпер). Фильтр состояния входит
// в выбор: клик по нему и так снимает выбранный документ, то есть пара «состояние + документ»
// согласована по построению, и возврат должен воспроизводить именно её — работа тут идёт пачкой
// («разбираю просроченные»). Поиск не запоминаем: он про разовый акт, а не про место работы.
const DOC_STATES = ['expired', 'expiring', 'unlinked'] as const;
const SELECTION_KEYS = ['state', 'doc'] as const;
const QUALITY_LAST_KEY = 'quality-docs-last';

export function QualityDocsPage() {
  const [search, setSearch] = useState('');
  const { values, remember } = useRememberedSelection(QUALITY_LAST_KEY, SELECTION_KEYS);
  const selected = values.doc || null;
  const setSelected = (id: string | null) => remember({ doc: id ?? '' });
  // Состояние и документ пишем одним вызовом: два подряд не накапливаются — второй считает от
  // значения времени рендера и вернул бы снятый документ обратно.
  const setStateFilter = (s: DocState | null) => remember({ state: s ?? '', doc: '' });
  const restoredState = DOC_STATES.find(s => s === values.state) ?? null;
  const [createOpen, setCreateOpen] = useState(false);
  const [webOpen, setWebOpen] = useState(false);
  const [editDoc, setEditDoc] = useState<QualityDocument | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<QualityDocument | null>(null);

  const { data: docTypes = [] } = useListDocumentTypes();
  const { data: docs = [], isLoading } = useListQualityDocs({});
  // Связки ВСЕХ областей одним запросом (issue #554): экран смотрит поперёк областей.
  const { data: links = [] } = useListMaterialLinks();
  const del = useDeleteQualityDoc();
  const removeLinks = useRemoveMaterialLinks();
  const [breakAll, setBreakAll] = useState<QualityDocument | null>(null);

  const linksByDoc = useMemo(() => {
    const m = new Map<string, MaterialQualityLink[]>();
    for (const l of links) {
      const arr = m.get(l.qualityDocumentId); if (arr) arr.push(l); else m.set(l.qualityDocumentId, [l]);
    }
    return m;
  }, [links]);

  const states = useMemo(() => {
    const m = new Map<DocState, QualityDocument[]>();
    for (const d of docs) {
      const s = docState(byTag(d, docTypes, FUNCTIONAL_TAG.qualityValidUntil) || null, linksByDoc.get(d.id)?.length ?? 0);
      if (!s) continue;
      const arr = m.get(s); if (arr) arr.push(d); else m.set(s, [d]);
    }
    return m;
  }, [docs, docTypes, linksByDoc]);

  // Состояние могло рассосаться, пока нас не было (сроки продлили, связки добавили): фильтр с
  // пустым списком снимаем молча — иначе вход выглядел бы как «библиотека пуста». Снимаем и тогда,
  // когда восстановленный документ в него не попадает: пара «состояние + документ» согласована
  // при выборе, но данные могли поменяться, а показывать в детали то, чего нет в рейле, нельзя —
  // ни строки, ни подсветки, и снять выбор неоткуда.
  // Пока список не пришёл, состояний нет ни у кого — судить о «пустом фильтре» рано: иначе
  // подавление срабатывало бы на каждом входе и стирало фильтр из памяти навсегда.
  const dataReady = !isLoading && docs.length > 0;
  const restoredGroup = restoredState ? (states.get(restoredState) ?? []) : [];
  const stateFilter = restoredState && (!dataReady
    || (restoredGroup.length > 0 && (!selected || restoredGroup.some(d => d.id === selected))))
    ? restoredState : null;

  // Подавили фильтр — забываем его: иначе он остался бы в памяти, всплыл бы обратно в адрес при
  // следующей записи выбора (`remember` сливает прежние значения) и однажды применился бы сам —
  // когда состояние снова появится, а пользователь его не выбирал.
  useEffect(() => {
    if (dataReady && restoredState && !stateFilter) remember({ state: '' });
    // remember стабилен по смыслу вызова; следим за самим фактом подавления
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dataReady, restoredState, stateFilter]);

  /**
   * Поиск переопределяет список: документ остаётся, если совпало его имя/номер ИЛИ хоть одна его
   * связка. Иначе поиск по материалу молча прятал бы ровно то, что нашёл.
   */
  const visibleDocs = useMemo(() => {
    const q = search.trim().toLowerCase();
    const inState = stateFilter ? (states.get(stateFilter) ?? []) : docs;
    if (!q) return inState;
    return inState.filter(d =>
      d.displayName.toLowerCase().includes(q)
      || byTag(d, docTypes, FUNCTIONAL_TAG.docNumber).toLowerCase().includes(q)
      || (linksByDoc.get(d.id) ?? []).some(l => matchesLink(l, q)));
  }, [docs, docTypes, search, stateFilter, states, linksByDoc]);

  // Имена, которые в библиотеке встречаются дважды (issue #588) — считаем по ВСЕЙ библиотеке, а не
  // по видимой части: имя не перестаёт быть неоднозначным оттого, что второй документ отфильтрован.
  const ambiguousNames = useMemo(() => ambiguousDocNames(docs), [docs]);


  const current = selected ? docs.find(d => d.id === selected) ?? null : null;

  // Если ни у одного документа срок не резолвится тэгом, состояния по сроку посчитать нельзя —
  // и об этом надо сказать, а не показывать пустоту (в живой базе ровно так: реквизит
  // «ПериодДействия» есть, а поля с тэгом quality.validUntil в схеме нет, см. #558).
  const validUntilKnown = useMemo(
    () => docs.some(d => byTag(d, docTypes, FUNCTIONAL_TAG.qualityValidUntil) !== ''),
    [docs, docTypes]);

  const nav = (
    <>
      <div className="p-3 border-b border-stroke shrink-0">
        <NavSearchInput value={search} onChange={setSearch} placeholder="Материал или документ…" />
      </div>
      {/* Список скроллится САМ: без этого длинный перечень растит страницу и уносит шапку. */}
      <div className="flex-1 overflow-y-auto p-2">
        {[...states.keys()].length > 0 && (
          <>
            <NavSection label="Требует внимания" />
            {(['expired', 'expiring', 'unlinked'] as DocState[]).map(s => {
              const items = states.get(s);
              if (!items?.length) return null;
              const meta = STATE_META[s];
              const Icon = meta.icon;
              return (
                <NavItem key={s} icon={<Icon size={15} />} label={meta.label}
                  active={stateFilter === s}
                  count={meta.danger ? undefined : items.length}
                  alert={meta.danger ? items.length : undefined} alertDanger={meta.danger}
                  onClick={() => setStateFilter(stateFilter === s ? null : s)} />
              );
            })}
          </>
        )}
        {!validUntilKnown && (
          <p className="text-[11px] text-warning px-3 py-2 leading-snug">
            Срок действия документов системе неизвестен: ни одно поле типа не помечено тэгом
            «срок действия». Поэтому состояния «просрочен» и «истекает» не считаются — и по той же
            причине просроченные документы не прячутся из подсказок.
          </p>
        )}
        <NavSection label={stateFilter ? STATE_META[stateFilter].label : 'Документы'} />
        {/* Одноимённым документам дописываем номер (issue #588): в библиотеке два сертификата
            назывались одинаково, а внутри — разные номера, органы и области продукции. Приписывать
            номер ВСЕМ незачем: тогда он примелькается и перестанет читаться там, где нужен. */}
        {visibleDocs.map(d => (
          <NavItem key={d.id} icon={<ShieldCheck size={15} />}
            label={isAmbiguous(d, ambiguousNames)
              ? `${d.displayName} · ${docNumberOf(d, docTypes) || 'без номера'}`
              : d.displayName}
            count={linksByDoc.get(d.id)?.length ?? 0}
            active={selected === d.id} onClick={() => setSelected(d.id)} />
        ))}
        {visibleDocs.length === 0 && (
          <p className="text-xs text-fg4 px-3 py-4 text-center">Ничего не найдено.</p>
        )}
      </div>
    </>
  );

  const actions: RowAction[] = current ? [
    ...(current.scanBlobPath
      ? [{ key: 'scan', label: 'Открыть скан', icon: <FileText size={13} />,
        onSelect: () => void openAttachmentInNewTab(current.scanBlobPath!) }]
      : []),
    { key: 'edit', label: 'Редактировать', icon: <Pencil size={13} />, onSelect: () => setEditDoc(current) },
    // «Разорвать все» — пунктом меню, а не кнопкой (issue #556): действие редкое и опасное, под руку
    // попадаться не должно.
    ...((linksByDoc.get(current.id)?.length ?? 0) > 0
      ? [{ key: 'unlink-all', label: `Разорвать все связки (${linksByDoc.get(current.id)!.length})`,
        icon: <Unlink size={13} />, danger: true, onSelect: () => setBreakAll(current) }]
      : []),
    { key: 'delete', label: 'Удалить документ', icon: <Trash2 size={13} />, danger: true,
      onSelect: () => setDeleteTarget(current) },
  ] : [];

  const detail = !current ? (
    <div className="flex-1 min-w-0 flex items-center justify-center">
    <EmptyState icon={<ShieldCheck size={30} />}
      title={docs.length === 0 ? 'Документов качества пока нет' : 'Выберите документ'}
      description={docs.length === 0
        ? 'Добавьте первый документ — можно загрузить скан и распознать реквизиты, либо найти в интернете.'
        : 'Слева — библиотека и связанные с документами материалы. Здесь видно, что именно висит на выбранном документе.'}
      action={docs.length === 0
        ? <Button variant="filled" icon={<Plus size={16} />} onClick={() => setCreateOpen(true)}>Добавить документ</Button>
        : undefined} />
    </div>
  ) : (
    <div className="flex-1 min-w-0 overflow-y-auto px-6 py-4 space-y-4">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          {/* Имени мало: в библиотеке два документа с одинаковым названием и разным содержимым. */}
          <h2 className="text-lg font-semibold text-fg1 break-words">{current.displayName}</h2>
          <p className="text-xs text-fg3 mt-0.5">
            {[typeNameOf(current, docTypes),
              byTag(current, docTypes, FUNCTIONAL_TAG.docNumber) && `№ ${byTag(current, docTypes, FUNCTIONAL_TAG.docNumber)}`,
              byTag(current, docTypes, FUNCTIONAL_TAG.qualityValidUntil) && `до ${formatDate(byTag(current, docTypes, FUNCTIONAL_TAG.qualityValidUntil))}`,
              byTag(current, docTypes, FUNCTIONAL_TAG.qualityManufacturer),
              SCOPE_LABELS[current.scope as CatalogScope] ?? current.scope,
            ].filter(Boolean).join(' · ')}
          </p>
        </div>
        <RowActionsMenu actions={actions} ariaLabel="Действия над документом" />
      </div>

      <DocRequisites doc={current} docTypes={docTypes} />

      <div>
        <h3 className="text-sm font-medium text-fg2 mb-2 flex items-center gap-2">
          <Link2 size={14} className="text-fg4" />
          Связки материалов · {linksByDoc.get(current.id)?.length ?? 0}
        </h3>
        {/* Фильтруем строки ТОЛЬКО если запрос попал в связки. Иначе человек, нашедший документ по
            его собственному имени, увидел бы «ни одна связка не подходит» под заголовком «Связки · 69». */}
        {/* key — чтобы выбор строк не переезжал на другой документ: иначе панель показывала бы
            «Выбрано: 3» без единой отмеченной строки, а разрыв ушёл бы с пустым списком. */}
        <QualityDocLinks key={current.id} links={linksByDoc.get(current.id) ?? []} allLinks={links} allDocTypes={docTypes}
          search={(linksByDoc.get(current.id) ?? []).some(l => matchesLink(l, search.trim().toLowerCase())) ? search : ''} />
      </div>
    </div>
  );

  return (
    <>
    <ListDetailShell
      title="Документы качества"
      subtitle="Библиотека сертификатов и деклараций; связки материалов — на карточке документа"
      titleIcon={<ShieldCheck size={20} className="text-brand" />}
      headerAction={
        <div className="flex items-center gap-2">
          <Button variant="outlined" icon={<Globe size={15} />} onClick={() => setWebOpen(true)}>
            Найти в интернете
          </Button>
          <Button variant="filled" icon={<Plus size={16} />} onClick={() => setCreateOpen(true)}>
            Добавить документ
          </Button>
        </div>
      }
      nav={isLoading ? <p className="text-sm text-fg4 p-4">Загрузка…</p> : nav}
      detail={detail}
    />
    {/* Модалки — СНАРУЖИ shell: его проп overlay заменяет собой весь сплит, а не накладывается. */}
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

          <WebSearchModal open={webOpen} onClose={() => setWebOpen(false)} docTypes={docTypes} />

          <ConfirmDialog
            open={!!breakAll} onOpenChange={o => { if (!o) setBreakAll(null); }}
            title={`Разорвать все связки документа (${breakAll ? linksByDoc.get(breakAll.id)?.length ?? 0 : 0})?`}
            description={<>
              <p>Материалы останутся без документа качества — при генерации поле документа
                качества будет пустым. Сам документ останется в библиотеке.</p>
              {/* Действие идёт поперёк уровней (issue #649): среди связок бывают общесистемные,
                  действующие на другие стройки, и одного числа для решения мало. */}
              <ScopeReachNote links={breakAll ? linksByDoc.get(breakAll.id) ?? [] : []} />
            </>}
            confirmLabel="Разорвать все"
            onConfirm={async () => {
              if (!breakAll) return;
              await removeLinks.mutateAsync((linksByDoc.get(breakAll.id) ?? []).map(l => l.id));
            }}
          />

          <ConfirmDialog
            open={!!deleteTarget}
            onOpenChange={o => { if (!o) setDeleteTarget(null); }}
            title={`Удалить «${deleteTarget?.displayName ?? ''}»?`}
            description={
              <p>Связи с материалами также будут удалены
                {deleteTarget ? ` (${linksByDoc.get(deleteTarget.id)?.length ?? 0})` : ''}.</p>
            }
            confirmLabel="Удалить"
            onConfirm={async () => {
              if (!deleteTarget) return;
              await del.mutateAsync(deleteTarget.id);
              if (selected === deleteTarget.id) setSelected(null);
            }}
          />
    </>
  );
}

function typeNameOf(doc: QualityDocument, docTypes: DocumentType[]): string {
  return docTypes.find(t => t.id === doc.documentTypeId)?.name ?? '';
}

function formatDate(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleDateString('ru-RU');
}

/**
 * Реквизиты документа рядом со списком связок — в первую очередь ради «Продукции»: увидев
 * «Выключатели автоматические, модель AV-125» над перечнем светильников и розеток, человек делает
 * вывод сам. Система при этом ничего не утверждает и не может ошибиться.
 *
 * Поля берём из схемы типа, а не по именам: имя реквизита хардкодить нельзя — в живых данных
 * «ТипДокумента» местами противоречит самому типу документа (issue #554).
 */
function DocRequisites({ doc, docTypes }: { doc: QualityDocument; docTypes: DocumentType[] }) {
  const rows = useMemo(() => {
    const dt = docTypes.find(t => t.id === doc.documentTypeId);
    if (!dt) return [];
    return resolveEffectiveFields(dt, docTypes)
      .filter(f => f.type === 'string' || f.type === 'text')
      .map(f => ({ key: f.key, title: f.title || f.key, value: doc.requisites[f.key] }))
      .filter((r): r is { key: string; title: string; value: string } =>
        typeof r.value === 'string' && r.value.trim() !== '');
  }, [doc, docTypes]);

  if (rows.length === 0) return null;
  return (
    <dl className="rounded-lg border border-stroke bg-base px-3 py-2 text-xs space-y-1">
      {rows.map(r => (
        <div key={r.key} className="flex gap-2">
          <dt className="text-fg4 shrink-0 w-40">{r.title}</dt>
          <dd className="text-fg2 min-w-0 break-words">{r.value}</dd>
        </div>
      ))}
    </dl>
  );
}

/** Веб-поиск документов (ФГИС → производитель → веб) — вынесен в модалку из шапки страницы. */
function WebSearchModal({ open, onClose, docTypes }: {
  open: boolean; onClose: () => void; docTypes: DocumentType[];
}) {
  const qc = useQueryClient();
  const [query, setQuery] = useState('');
  const [candidates, setCandidates] = useState<SearchCandidate[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [importingUrl, setImportingUrl] = useState<string | null>(null);
  const [error, setError] = useState('');
  // Определения типов полей нужны распознаванию импортированного скана (issue #654).
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const { data: enumTypes = [] } = useListEnumTypes();

  // Тип берём ТОЛЬКО среди помеченных тэгом «документ качества»: без него q[0] это первый попавшийся
  // тип документа (например АОСР), и импорт молча создал бы «документ качества» чужого типа, чья
  // схема потом кормит распознавание. Тот же фильтр — в LinkPickerModal.
  const defaultTypeId = useMemo(() => {
    const q = docTypes.filter(dt => dt.kind === 'Document' && !dt.isAbstract
      && typeHasTag(dt, FUNCTIONAL_TAG.typeQualityDocument, docTypes));
    return q.find(d => /сертификат/i.test(d.name))?.id ?? q[0]?.id ?? '';
  }, [docTypes]);

  async function run() {
    if (!query.trim()) return;
    setSearching(true); setError(''); setCandidates(null);
    try { setCandidates(await searchQualityDocs(query.trim())); }
    catch (e: unknown) {
      // Сервер объясняет причину («поиск не настроен») — она полезнее, чем «status code 503».
      const resp = (e as { response?: { data?: { error?: string } } })?.response;
      setError(resp?.data?.error ?? (e instanceof Error ? e.message : 'Ошибка поиска'));
    }
    finally { setSearching(false); }
  }

  async function importOne(c: SearchCandidate) {
    if (!defaultTypeId) { setError('Не найден тип «документ качества».'); return; }
    setImportingUrl(c.url); setError('');
    try {
      const created = await importQualityDocFromUrl({
        url: c.url, documentTypeId: defaultTypeId, title: c.title || c.url,
        scope: 'System' as CatalogScope, scopeId: null,
      });
      // Распознавание — best-effort: документ уже импортирован, и падение распознавателя (например,
      // выключенная Ollama) не должно выглядеть как «не удалось импортировать» — человек нажал бы ещё
      // раз и завёл дубль.
      try { await recognizeAndUpdate(created, docTypes, { primitiveTypes, enumTypes }); }
      catch { /* распознавание не критично */ }
      // Без инвалидации список слева не обновится до перефокуса окна (staleTime 30 с), и тот же
      // документ импортировали бы повторно.
      qc.invalidateQueries({ queryKey: ['quality-docs'] });
    } catch (e: unknown) {
      const resp = (e as { response?: { data?: { error?: string } } })?.response;
      setError(resp?.data?.error ?? (e instanceof Error ? e.message : 'Не удалось импортировать'));
    } finally { setImportingUrl(null); }
  }

  return (
    <Modal open={open} onOpenChange={o => { if (!o) onClose(); }} title="Поиск документов в интернете" wide>
      <div className="flex items-center gap-2 flex-wrap">
        <input value={query} onChange={e => setQuery(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') void run(); }}
          placeholder="Напр.: Выключатель автоматический EKF AV-10"
          className="flex-1 min-w-[260px] border border-stroke-strong rounded-md px-3 py-2 text-sm bg-surface text-fg1" />
        <Button variant="filled" onClick={run} disabled={searching || !query.trim()}
          loading={searching} icon={<Search size={14} />}>
          Найти
        </Button>
      </div>
      {error && <p className="text-sm text-danger mt-2">{error}</p>}
      {candidates && (
        candidates.length === 0
          ? <p className="text-sm text-fg4 mt-3">Ничего не найдено.</p>
          : (
            <div className="mt-3 divide-y divide-muted border border-stroke rounded-md max-h-96 overflow-y-auto">
              {candidates.map(c => (
                <div key={c.url} className="flex items-start gap-3 px-3 py-2">
                  <span className="text-[10px] px-1.5 py-0.5 rounded bg-brand-subtle text-brand shrink-0 mt-0.5">
                    {SOURCE_LABEL[c.source] ?? c.source}
                  </span>
                  <div className="min-w-0 flex-1">
                    <p className="text-sm text-fg1 truncate">{c.title || c.url}</p>
                    {c.snippet && <p className="text-xs text-fg4 line-clamp-2">{c.snippet}</p>}
                    <a href={c.url} target="_blank" rel="noopener noreferrer"
                      className="text-xs text-brand-hover inline-flex items-center gap-1 mt-0.5">
                      <ExternalLink size={11} /> Открыть
                    </a>
                  </div>
                  <button onClick={() => importOne(c)} disabled={importingUrl === c.url}
                    title="Скачать файл по ссылке и добавить в библиотеку (если это прямой PDF/скан)"
                    className="flex items-center gap-1.5 text-xs px-2 py-1 rounded-md border border-stroke hover:bg-base disabled:opacity-50 shrink-0">
                    {importingUrl === c.url ? <Loader2 size={12} className="animate-spin" /> : <Download size={12} />} В библиотеку
                  </button>
                </div>
              ))}
            </div>
          )
      )}
    </Modal>
  );
}

