import { useState } from 'react';
import {
  ChevronDown, ChevronUp, Plus, Database, ShieldCheck, Loader2,
  DatabaseZap, RefreshCw, X, CornerUpLeft, Link2,
} from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { Select, SelectItem, SelectGroup } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import {
  useListCommonData, useCommonDataForSet, useCreateCommonDataEntry,
  useUpdateCommonDataEntry, useDeleteCommonDataEntry, useCommonDataEntry, useCheckBindings,
  type BindingCheckItem,
} from '@/shared/api/commonData';
import type { CommonDataEntry, CatalogScope, DocumentType, PrimitiveTypeDef, EnumTypeDef } from '@/shared/api/types';
import { SCOPE_LABELS, SCOPE_PRIORITY } from '@/shared/api/types';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import {
  resolveEffectiveFields, parseSchemaFields, groupEffectiveFields,
  getDefaultValues, findTaggedFieldPath, type SchemaField,
} from '@/shared/api/schema';
import { isFileAttachment, formatBytes } from '@/shared/api/attachments';
import { recognizeDocument } from '@/shared/api/qualityDocs';
import { flattenLeaves, applyRecognized } from '@/features/quality-docs/QualityDocForm';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import { useListDataSetBindings, usePreviewDataSetBindings } from '@/shared/api/datasets';
import { computeBoundFieldKeys, mergeBindingPreviewsIntoValues } from '@/shared/api/datasetHelpers';
import { EntryDataSetBindings } from './EntryDataSetBindings';
import { groupObjectsByType, ObjectRow } from './ObjectsByTypeList';
import {
  SCOPE_COLORS, ComplexFieldGroup, ArrayFieldEditor, DocRefCatalogPickerField,
  PrimitiveInput, FileField, ImageField, AutoFieldsSection,
  BaseInstancePanel, SCOPE_TIER, type BaseCandidate,
  SectionRail,
} from '../fields';

// Отчёт «Проверить связки» (issue #99): статус каждого @@ref-поля.
const CHECK_STATUS: Record<string, { label: string; cls: string }> = {
  matched: { label: 'связано', cls: 'bg-green-50 text-green-700 border-green-200' },
  'not-found': { label: 'не найдено', cls: 'bg-warning-subtle text-warning border-warning-border' },
  dangling: { label: 'запись удалена', cls: 'bg-red-50 text-danger border-red-200' },
  drift: { label: 'устарело', cls: 'bg-warning-subtle text-warning border-warning-border' },
  stale: { label: 'пересохранить', cls: 'bg-warning-subtle text-warning border-warning-border' },
};

function BindingCheckReport({ items }: { items: BindingCheckItem[] }) {
  if (items.length === 0)
    return <p className="text-xs text-fg4 px-1">Ссылочных связок нет — проверять нечего.</p>;
  return (
    <div className="rounded-lg border border-stroke divide-y divide-muted">
      {items.map(it => {
        const s = CHECK_STATUS[it.status] ?? { label: it.status, cls: 'bg-muted text-fg3 border-stroke' };
        return (
          <div key={it.fieldKey} className="flex items-start gap-2 px-3 py-2 text-sm">
            <span className="flex-1 min-w-0">
              <span className="font-medium text-fg1">{it.fieldTitle}</span>
              {it.linkedName && <span className="text-fg4"> → {it.linkedName}</span>}
              {it.detail && <span className="block text-[11px] text-fg4">{it.detail}</span>}
            </span>
            <span className={`shrink-0 text-[11px] px-1.5 py-0.5 rounded-full border font-medium ${s.cls}`}>{s.label}</span>
          </div>
        );
      })}
    </div>
  );
}

/** Показ резолвнутой $ref-ссылки в связанном поле (issue #99): резолвит запись каталога по id → имя. */
function BoundRefValue({ entryId }: { entryId: string }) {
  const { data: entry } = useCommonDataEntry(entryId);
  return (
    <span className="inline-flex items-center gap-1 text-brand">
      <Link2 size={12} className="shrink-0" />
      {entry ? entry.displayName : <span className="text-fg4">запись каталога…</span>}
    </span>
  );
}

export function ScopedCatalogPanel({ scope, scopeId, allDocTypes, setId }: {
  scope: CatalogScope; scopeId: string | null; allDocTypes: DocumentType[];
  setId?: string;
}) {
  const [expanded, setExpanded] = useState(false);
  const [expandedTypes, setExpandedTypes] = useState<Set<string>>(new Set());
  const [addOpen, setAddOpen] = useState(false);
  const [editEntry, setEditEntry] = useState<CommonDataEntry | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<CommonDataEntry | null>(null);

  const { data: entries = [], isLoading } = useListCommonData({
    scope, scopeId: scopeId ?? undefined, enabled: expanded,
  });
  const compositeTypes = allDocTypes.filter(dt => dt.kind === 'Composite');
  const documentTypes = allDocTypes.filter(dt => dt.kind === 'Document' && !dt.isAbstract);
  const allSelectableTypes = [...compositeTypes, ...documentTypes];
  const deleteMutation = useDeleteCommonDataEntry();

  const sorted = [...entries].sort((a, b) => a.displayName.localeCompare(b.displayName));
  const { groups, noType } = groupObjectsByType(sorted, allSelectableTypes);

  function toggleType(id: string) {
    setExpandedTypes(prev => { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n; });
  }

  function renderEntries(items: CommonDataEntry[]) {
    return items.map((entry, idx) => (
      <ObjectRow key={entry.id} entry={entry} siblings={entries}
        onEdit={setEditEntry} onDelete={setDeleteTarget} deleteDisabled={deleteMutation.isPending}
        dense docKind={documentTypes.some(dt => dt.id === entry.compositeTypeId)}
        className={idx > 0 ? 'border-t border-stroke' : ''} />
    ));
  }

  // Группа типа: uppercase-микрозаголовок на заливке bg-base (сильный «заголовочный» сигнал),
  // записи — плоские, на bg-surface, с левым рейлом-коннектором. Три канала различия уровней
  // (типографика + заливка + рейл) — issue #8, слабая визуальная иерархия.
  function renderGroup(key: string, label: string, items: CommonDataEntry[], muted = false) {
    const isOpen = expandedTypes.has(key);
    return (
      <div key={key} className="border border-stroke rounded-lg overflow-hidden bg-surface">
        <button onClick={() => toggleType(key)} aria-expanded={isOpen}
          className="w-full flex items-center gap-2 px-3 py-1.5 bg-base hover:bg-muted transition-colors text-left">
          {isOpen
            ? <ChevronUp size={12} className="text-fg4 shrink-0" />
            : <ChevronDown size={12} className="text-fg4 shrink-0" />}
          <span className={`flex-1 text-xs font-semibold uppercase tracking-wide ${muted ? 'text-fg4' : 'text-fg2'}`}>{label}</span>
          <span className="text-xs text-fg4">{items.length}</span>
        </button>
        {isOpen && (
          <div className="bg-surface">
            <div className="ml-3 border-l border-stroke">{renderEntries(items)}</div>
          </div>
        )}
      </div>
    );
  }

  return (
    <div className="border border-stroke rounded-xl overflow-hidden">
      <button onClick={() => setExpanded(v => !v)} aria-expanded={expanded}
        className="w-full flex items-center gap-3 px-4 py-3 bg-surface hover:bg-base transition-colors text-left">
        <Database size={16} className="text-fg4" />
        <span className={`shrink-0 text-[10px] px-1.5 py-0.5 rounded-full font-medium ${SCOPE_COLORS[scope]}`}>
          {SCOPE_LABELS[scope]}
        </span>
        <span className="text-sm font-medium text-fg2">Каталог общих данных</span>
        {!expanded && entries.length > 0 && (
          <span className="text-xs text-fg4">{entries.length} записей</span>
        )}
        {expanded
          ? <ChevronUp size={14} className="text-fg4 ml-auto" />
          : <ChevronDown size={14} className="text-fg4 ml-auto" />}
      </button>
      {expanded && (
        <div className="border-t border-stroke bg-base px-4 py-4 space-y-2">
          {isLoading ? (
            <p className="text-sm text-fg4 text-center py-2">Загрузка...</p>
          ) : entries.length === 0 ? (
            <p className="text-sm text-fg4 text-center py-2">Записей нет</p>
          ) : (
            <div className="space-y-1">
              {groups.map(({ type, items }) => renderGroup(type.id, type.name, items))}
              {noType.length > 0 && renderGroup('__no_type__', 'Без типа', noType, true)}
            </div>
          )}
          <Button variant="text" size="sm" icon={<Plus size={14} />} onClick={() => setAddOpen(true)} className="mt-1">
            Добавить запись
          </Button>
        </div>
      )}
      <Modal open={addOpen} onOpenChange={setAddOpen} title="Новая запись каталога" wide flushBody>
        {addOpen && (
          <CatalogEntryForm compositeTypes={compositeTypes} documentTypes={documentTypes} allDocTypes={allDocTypes}
            scope={scope} scopeId={scopeId} setId={setId} onClose={() => setAddOpen(false)} />
        )}
      </Modal>
      <Modal open={!!editEntry} onOpenChange={o => { if (!o) setEditEntry(null); }} title="Редактировать запись" wide flushBody>
        {editEntry && (
          <CatalogEntryForm entry={editEntry} compositeTypes={compositeTypes} documentTypes={documentTypes} allDocTypes={allDocTypes}
            scope={scope} scopeId={scopeId} setId={setId} onClose={() => setEditEntry(null)} />
        )}
      </Modal>
      <ConfirmDialog
        open={!!deleteTarget}
        onOpenChange={o => { if (!o) setDeleteTarget(null); }}
        title={`Удалить «${deleteTarget?.displayName ?? ''}»?`}
        confirmLabel="Удалить"
        onConfirm={() => { if (deleteTarget) deleteMutation.mutate(deleteTarget.id); }}
      />
    </div>
  );
}

// Базовый экземпляр каталога использует общий BaseCandidatePicker (issue #73, шаг 2) —
// кандидаты (записи родительского типа по скопам) строятся ниже из parentEntries.

// ─── Catalog entry form (create + edit, shared by ScopedCatalogPanel) ────────

export function CatalogEntryForm({
  entry, compositeTypes, documentTypes = [], allDocTypes, scope, scopeId, setId, onClose,
}: {
  entry?: CommonDataEntry | null;
  compositeTypes: DocumentType[];
  documentTypes?: DocumentType[];
  allDocTypes: DocumentType[];
  scope: CatalogScope;
  scopeId: string | null;
  setId?: string;
  onClose: () => void;
}) {
  const [displayName, setDisplayName] = useState(entry?.displayName ?? '');
  const [aliases, setAliases] = useState<string[]>(entry?.aliases ?? []);
  const [aliasDraft, setAliasDraft] = useState('');
  function addAlias() {
    const t = aliasDraft.trim();
    if (t && !aliases.some(a => a.toLowerCase() === t.toLowerCase())) setAliases(prev => [...prev, t]);
    setAliasDraft('');
  }
  const [typeId, setTypeId] = useState(entry?.compositeTypeId ?? '');
  const [values, setValues] = useState<Record<string, unknown>>(() => entry?.data ?? {});
  const [error, setError] = useState('');
  const [expandedGroups, setExpandedGroups] = useState<Set<string>>(new Set());
  const [recognizing, setRecognizing] = useState(false);
  const [showAllProxyFields, setShowAllProxyFields] = useState(false); // прокси: раскрыть все поля для переопределения (issue #89)
  const createMutation = useCreateCommonDataEntry();
  const updateMutation = useUpdateCommonDataEntry();
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const { data: enumTypes = [] } = useListEnumTypes();

  const getPrimitiveDef = (f: SchemaField): PrimitiveTypeDef | undefined =>
    f.type === 'primitive' ? primitiveTypes.find(pt => pt.id === f.typeId) : undefined;
  const getEnumDef = (f: SchemaField): EnumTypeDef | undefined =>
    f.type === 'enum' ? enumTypes.find(et => et.id === f.typeId) : undefined;

  const allSelectableTypes = [...compositeTypes, ...documentTypes];
  const selectedType = allSelectableTypes.find(t => t.id === typeId) ?? null;

  const parentType = selectedType?.parentId
    ? allDocTypes.find(dt => dt.id === selectedType.parentId) ?? null
    : null;
  const baseRefId = typeof values._baseRef === 'string' ? values._baseRef : undefined;
  const ownFields = selectedType ? parseSchemaFields(selectedType.schema) : [];
  const effectiveFields = selectedType ? resolveEffectiveFields(selectedType, allDocTypes) : [];

  // Распознавание: берём первое поле-файл с загруженным вложением (обычно единственное —
  // "Файл"). Заполняет только простые поля (flattenLeaves пропускает array/doc-ref/complex-с-
  // ссылкой) — составные/ссылочные остаются ручными, этого достаточно для «выборочности».
  const fileFieldValue = effectiveFields
    .map(f => (f.type === 'file' ? values[f.key] : undefined))
    .find(v => isFileAttachment(v));
  const attachment = isFileAttachment(fileFieldValue) ? fileFieldValue : null;

  const { data: allParentEntries = [] } = useCommonDataForSet({
    setId: setId ?? '', typeId: parentType?.id, enabled: !!parentType && !!setId,
  });
  const { data: scopeParentEntries = [] } = useListCommonData({
    scope, scopeId: scopeId ?? undefined, typeId: parentType?.id,
    enabled: !!parentType && !setId && scope !== 'System',
  });
  const { data: systemParentEntries = [] } = useListCommonData({
    scope: 'System', typeId: parentType?.id, enabled: !!parentType,
  });
  // ВАЖНО: клиентский фильтр по типу обязателен — useQuery с typeId:undefined (когда parentType нет)
  // возвращает закешированные данные по совпадающему ключу (нефильтрованный список каталога),
  // даже при enabled:false. Без этого фильтра в пикер протекают объекты чужих типов.
  const parentEntries: CommonDataEntry[] = !parentType ? [] :
    (setId
      ? allParentEntries
      : [...scopeParentEntries, ...systemParentEntries.filter(e => !scopeParentEntries.some(s => s.id === e.id))]
    ).filter(e => e.compositeTypeId === parentType.id);
  // Кандидаты базы для общего пикера (issue #73, шаг 2): записи родительского типа по скопам.
  const baseCandidates: BaseCandidate[] = parentEntries
    .map(e => ({
      kind: 'catalog' as const, id: e.id, name: e.displayName, typeId: e.compositeTypeId,
      tier: SCOPE_TIER[e.scope], scopeLabel: SCOPE_LABELS[e.scope], dist: 0,
    }))
    .sort((a, b) => a.tier - b.tier || a.name.localeCompare(b.name, 'ru'));

  // Кандидаты роли/прокси (issue #89): объекты ТОГО ЖЕ типа, видимые в скоупе (кроме самого себя).
  const proxyTypeId = selectedType?.allowsProxy ? selectedType.id : undefined;
  const { data: allProxyEntries = [] } = useCommonDataForSet({
    setId: setId ?? '', typeId: proxyTypeId, enabled: !!proxyTypeId && !!setId,
  });
  const { data: scopeProxyEntries = [] } = useListCommonData({
    scope, scopeId: scopeId ?? undefined, typeId: proxyTypeId,
    enabled: !!proxyTypeId && !setId && scope !== 'System',
  });
  const { data: systemProxyEntries = [] } = useListCommonData({
    scope: 'System', typeId: proxyTypeId, enabled: !!proxyTypeId,
  });
  // Тот же клиентский фильтр по типу (иммунно к cache-collision по ключу typeId:undefined).
  const proxyEntries: CommonDataEntry[] = !proxyTypeId ? [] :
    (setId
      ? allProxyEntries
      : [...scopeProxyEntries, ...systemProxyEntries.filter(e => !scopeProxyEntries.some(s => s.id === e.id))]
    ).filter(e => e.compositeTypeId === proxyTypeId);
  const proxyCandidates: BaseCandidate[] = proxyEntries
    .filter(e => e.id !== entry?.id) // не сам на себя
    .map(e => ({
      kind: 'catalog' as const, id: e.id, name: e.displayName, typeId: e.compositeTypeId,
      tier: SCOPE_TIER[e.scope], scopeLabel: SCOPE_LABELS[e.scope], dist: 0, proxy: true,
      targetIsProxy: !!(e.data as Record<string, unknown>)?._baseRef,
    }))
    .sort((a, b) => a.tier - b.tier || a.name.localeCompare(b.name, 'ru'));

  const allCandidates = [...proxyCandidates, ...baseCandidates];
  const selectedBase = baseRefId ? allCandidates.find(c => c.id === baseRefId) : undefined;
  const isProxy = !!selectedBase?.proxy;
  const canHaveBase = !!parentType || !!selectedType?.allowsProxy;
  // Резолвнутые данные выбранного реального объекта — для подсказки «наследует: …» в режиме прокси
  // (issue #92). Проходим цепочку _baseRef цели (прокси-на-прокси), мержим база→свои, без _baseRef.
  const realData: Record<string, unknown> = (() => {
    if (!isProxy || !baseRefId) return {};
    const seen = new Set<string>();
    const chain: CommonDataEntry[] = [];
    let curId: string | undefined = baseRefId;
    while (curId && !seen.has(curId)) {
      seen.add(curId);
      const e = proxyEntries.find(x => x.id === curId);
      if (!e) break;
      chain.push(e);
      const br = (e.data as Record<string, unknown>)?._baseRef;
      curId = typeof br === 'string' ? br : (br && typeof br === 'object' && 'id' in br ? (br as { id?: string }).id : undefined);
    }
    const merged: Record<string, unknown> = {};
    for (const e of chain.reverse())
      for (const [k, v] of Object.entries(e.data as Record<string, unknown>))
        if (k !== '_baseRef') merged[k] = v;
    return merged;
  })();

  // Поля формы: у прокси нет деления свои/родительские — все наследуются. Показываем «дельту»
  // (только переопределённые поля), с раскрытием всех для добавления переопределений (issue #89).
  const displayFields = isProxy
    ? (showAllProxyFields ? effectiveFields : effectiveFields.filter(f => values[f.key] !== undefined && values[f.key] !== ''))
    : (parentType && baseRefId) ? ownFields : effectiveFields;
  const sections = selectedType ? groupEffectiveFields(displayFields, selectedType.schema) : [];
  // Rail разделов — только для крупных форм с несколькими именованными группами (issue #102, P3).
  const titledSections = sections.filter(s => s.title);
  const showRail = titledSections.length >= 3 && displayFields.length >= 12;

  // Наборы данных: биндинги существуют только у уже сохранённой записи (нужен id-владелец).
  const { data: bindings = [] } = useListDataSetBindings({ ownerId: entry?.id });
  const { scalarKeys: boundFieldKeys, arrayKeys: boundArrayKeys } = computeBoundFieldKeys(bindings);
  const { refetch: refetchBindingPreview, isFetching: refreshingFromSource } =
    usePreviewDataSetBindings({ ownerId: entry?.id });
  // Проверка связок (issue #99) — по требованию.
  const { data: bindingCheck, refetch: runBindingCheck, isFetching: checkingBindings } = useCheckBindings(entry?.id);

  async function handleRefreshFromSource() {
    const { data: previews } = await refetchBindingPreview();
    if (previews) setValues(v => mergeBindingPreviewsIntoValues(v, previews));
  }

  function setValue(key: string, val: unknown) {
    setValues(p => {
      if (val === undefined) { const n = { ...p }; delete n[key]; return n; }
      return { ...p, [key]: val };
    });
  }
  function toggleGroup(key: string) {
    setExpandedGroups(prev => { const n = new Set(prev); n.has(key) ? n.delete(key) : n.add(key); return n; });
  }
  // Навигация по разделам большой формы (issue #102, P3): раскрыть раздел и проскроллить к нему.
  function goToSection(key: string) {
    setExpandedGroups(prev => new Set(prev).add(key));
    requestAnimationFrame(() =>
      document.getElementById(`cat-section-${key}`)?.scrollIntoView({ behavior: 'smooth', block: 'start' }));
  }

  async function handleRecognize() {
    if (!attachment || !selectedType) return;
    setRecognizing(true); setError('');
    try {
      const rec = await recognizeDocument({
        blobPath: attachment.blobPath, mimeType: attachment.mimeType,
        fields: flattenLeaves(effectiveFields, allDocTypes),
        promptKind: 'titleblock',
      });
      let next = applyRecognized(values, rec.values);
      // Число страниц надёжнее брать из самого файла, чем просить модель прочитать его на штампе.
      if (rec.pageCount != null) {
        const p = findTaggedFieldPath(selectedType, FUNCTIONAL_TAG.docPageCount, allDocTypes);
        if (p) next = applyRecognized(next, { [p.join('.')]: String(rec.pageCount) });
      }
      setValues(next);
    } catch (e: unknown) {
      const resp = (e as { response?: { data?: { error?: string; limit?: boolean } } })?.response;
      if (resp?.data?.limit) setError('Лимит LLM исчерпан — повторите распознавание позже.');
      else setError(resp?.data?.error ?? (e instanceof Error ? e.message : 'Ошибка распознавания'));
    } finally { setRecognizing(false); }
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    if (!displayName.trim() || !typeId) { setError('Укажите название и тип'); return; }
    try {
      if (entry) {
        await updateMutation.mutateAsync({ id: entry.id, displayName, data: JSON.stringify(values), aliases });
      } else {
        await createMutation.mutateAsync({ displayName, compositeTypeId: typeId, data: JSON.stringify(values), scope, scopeId, aliases });
      }
      onClose();
    } catch (err: unknown) { setError(err instanceof Error ? err.message : 'Ошибка'); }
  }

    const isAuto = (f: SchemaField) =>
      (f.type === 'array' && boundArrayKeys.has(f.key)) ||
      (f.type !== 'array' && boundFieldKeys.has(f.key));

    function renderCell(field: SchemaField) {
          const val = values[field.key];
          const isBoundArray = field.type === 'array' && boundArrayKeys.has(field.key);
          const isBoundScalar = field.type !== 'array' && boundFieldKeys.has(field.key);

          if (isBoundArray) {
            const rows = Array.isArray(val) ? val as Record<string, unknown>[] : [];
            const cols = rows.length > 0 ? Object.keys(rows[0]) : [];
            return (
              <div key={field.key}>
                <label className="flex items-center gap-1.5 text-sm font-medium text-fg2 mb-1">
                  {field.title}
                  <span title="Значения подставляются из источника данных"><DatabaseZap size={12} className="text-brand" /></span>
                </label>
                <div className="rounded-md border border-stroke overflow-x-auto bg-muted">
                  {rows.length === 0 ? (
                    <p className="px-3 py-2 text-xs text-fg4">Нет данных из источника</p>
                  ) : (
                    <table className="text-xs w-full">
                      <thead>
                        <tr className="bg-base">
                          {cols.map(k => <th key={k} className="px-3 py-1.5 text-left font-medium whitespace-nowrap text-fg3">{k}</th>)}
                        </tr>
                      </thead>
                      <tbody>
                        {rows.map((row, i) => (
                          <tr key={i} className="border-t border-stroke">
                            {cols.map(k => (
                              <td key={k} className="px-3 py-1.5 whitespace-nowrap text-fg1">{String(row[k] ?? '')}</td>
                            ))}
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  )}
                </div>
              </div>
            );
          }

          if (isBoundScalar) {
            // Резолвнутая ссылка на запись каталога (@@ref, issue #99): показываем связанную запись,
            // а не сырой JSON. Составное поле теперь хранит {$ref:catalog, entryId}, а не строку «🔗 …».
            const refId = val && typeof val === 'object'
              && (val as { $ref?: string }).$ref === 'catalog'
              ? (val as { entryId?: string }).entryId : undefined;
            const display = val === undefined || val === null || val === ''
              ? null
              : isFileAttachment(val)
                ? `📎 ${val.fileName} (${formatBytes(val.size)})`
                : (typeof val === 'string' ? val : JSON.stringify(val));
            return (
              <div key={field.key}>
                <label className="flex items-center gap-1.5 text-sm font-medium text-fg2 mb-1">
                  {field.title}
                  <span title="Значение подставляется из источника данных"><DatabaseZap size={12} className="text-brand" /></span>
                </label>
                <div className="w-full border border-stroke rounded-md px-3 py-2 text-sm bg-muted text-fg2">
                  {refId ? <BoundRefValue entryId={refId} /> : (display ?? <em className="text-fg4">нет данных</em>)}
                </div>
              </div>
            );
          }

          const proxyOverridden = isProxy && values[field.key] !== undefined;
          const proxyReal = realData[field.key];
          const proxyRealHint = isProxy && (typeof proxyReal === 'string' || typeof proxyReal === 'number') && proxyReal !== ''
            ? String(proxyReal) : undefined;
          return (
            <div key={field.key}>
              {isProxy && (
                <div className="flex items-center justify-between gap-2 mb-0.5 min-h-[16px]">
                  <span className="text-[11px] text-fg4 truncate">
                    {proxyOverridden ? 'переопределено'
                      : proxyRealHint ? `наследует: ${proxyRealHint}`
                      : 'наследуется от реального'}
                  </span>
                  {proxyOverridden && (
                    <button type="button" onClick={() => setValue(field.key, undefined)}
                      title="Вернуть к наследованию от реального объекта"
                      className="text-[11px] text-brand hover:text-brand-hover flex items-center gap-0.5 shrink-0">
                      <CornerUpLeft size={11} /> наследовать
                    </button>
                  )}
                </div>
              )}
              {field.type === 'complex' || field.type === 'array' ? (
                <div>
                  <label className="block text-sm font-medium text-fg2 mb-1">
                    {field.title}
                    {field.required && <span className="ml-0.5 text-danger">*</span>}
                  </label>
                  {field.type === 'array' ? (
                    <ArrayFieldEditor
                      field={field} allDocTypes={allDocTypes} value={val}
                      onChange={v => setValue(field.key, v)} showValidation={false}
                      setId={setId} scope={scope} scopeId={scopeId}
                    />
                  ) : (
                    <ComplexFieldGroup
                      field={field} allDocTypes={allDocTypes} value={val}
                      onChange={v => setValue(field.key, v)} showValidation={false}
                      setId={setId} scope={scope} scopeId={scopeId}
                    />
                  )}
                </div>
              ) : field.type === 'doc-ref' ? (
                <div>
                  <label className="block text-sm font-medium text-fg2 mb-1">
                    {field.title}
                    {field.required && <span className="ml-0.5 text-danger">*</span>}
                  </label>
                  <DocRefCatalogPickerField field={field} allDocTypes={allDocTypes} value={val}
                    onChange={v => setValue(field.key, v ?? undefined)}
                    setId={setId} scope={scope} scopeId={scopeId} />
                </div>
              ) : field.type === 'image' ? (
                <div>
                  <label className="block text-sm font-medium text-fg2 mb-1">
                    {field.title}{field.required && <span className="ml-0.5 text-danger">*</span>}
                  </label>
                  <ImageField value={val} onChange={v => setValue(field.key, v)} />
                </div>
              ) : field.type === 'file' ? (
                <div>
                  <label className="block text-sm font-medium text-fg2 mb-1">
                    {field.title}{field.required && <span className="ml-0.5 text-danger">*</span>}
                  </label>
                  <FileField value={val} onChange={v => setValue(field.key, v)} />
                </div>
              ) : (
                <PrimitiveInput field={field} value={val} label={field.title}
                  hint={getPrimitiveDef(field)?.name} onChange={v => setValue(field.key, v)} invalid={false}
                  primitiveTypeDef={getPrimitiveDef(field)} enumTypeDef={getEnumDef(field)} />
              )}
            </div>
          );
    }

    function fieldStack(fields: SchemaField[]) {
      return <div className="space-y-4">{fields.map(renderCell)}</div>;
    }

    // Поля из источника данных (read-only) — под сворачиваемую секцию «Заполняются автоматически»
    // (issue #102, P2), чтобы форма записи каталога не растягивалась в «портянку».
    function renderFields(sectionFields: SchemaField[]) {
      const auto = sectionFields.filter(isAuto);
      if (auto.length === 0) return fieldStack(sectionFields);
      const normal = sectionFields.filter(f => !isAuto(f));
      return (
        <div className="space-y-4">
          {normal.length > 0 && fieldStack(normal)}
          <AutoFieldsSection count={auto.length}>{fieldStack(auto)}</AutoFieldsSection>
        </div>
      );
    }

  const isPending = createMutation.isPending || updateMutation.isPending;

  return (
    <form onSubmit={handleSubmit} className="flex flex-col min-h-0 flex-1">
      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4">
      <div className={showRail ? 'flex gap-5 items-start' : ''}>
      {showRail && (
        <SectionRail
          sections={titledSections.map(section => ({
            key: section.key,
            title: section.title!,
            count: section.fields.length,
          }))}
          isActive={key => expandedGroups.has(key)}
          onSelect={goToSection}
        />
      )}
      <div className="flex-1 min-w-0 space-y-4">
      <div className="flex items-center gap-2">
        <span className={`text-xs px-1.5 py-0.5 rounded font-medium ${SCOPE_COLORS[scope]}`}>
          {SCOPE_LABELS[scope]}
        </span>
        <span className="text-xs text-fg3">приоритет {SCOPE_PRIORITY[scope]}</span>
      </div>

      <TextField label="Наименование" value={displayName} onChange={e => setDisplayName(e.target.value)} required autoFocus />

      {/* Алиасы (issue #74) — доп. имена для поиска записи при связывании с источниками */}
      <div>
        <label className="block text-sm font-medium text-fg2 mb-1">
          Псевдонимы <span className="text-xs text-fg4 font-normal">(для поиска при связывании с источниками)</span>
        </label>
        {aliases.length > 0 && (
          <div className="flex flex-wrap gap-1.5 mb-1.5">
            {aliases.map(a => (
              <span key={a} className="inline-flex items-center gap-1 text-xs bg-muted text-fg2 rounded-2xl pl-2.5 pr-1 py-0.5 max-w-full">
                <span className="min-w-0 break-words">{a}</span>
                <button type="button" onClick={() => setAliases(prev => prev.filter(x => x !== a))}
                  className="text-fg4 hover:text-danger transition-colors shrink-0" title="Удалить">
                  <X size={11} />
                </button>
              </span>
            ))}
          </div>
        )}
        <input value={aliasDraft} onChange={e => setAliasDraft(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); addAlias(); } }}
          onBlur={addAlias}
          placeholder="Добавить псевдоним и Enter..."
          className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface" />
      </div>

      {!entry ? (
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Тип</label>
          <Select value={typeId || undefined} required placeholder="Выберите тип…" aria-label="Тип"
            onValueChange={newId => {
              setTypeId(newId);
              const t = allSelectableTypes.find(c => c.id === newId);
              setValues(t ? getDefaultValues(resolveEffectiveFields(t, allDocTypes)) : {});
            }}>
            {compositeTypes.length > 0 && (
              <SelectGroup label="Составные типы">
                {compositeTypes.map(ct => <SelectItem key={ct.id} value={ct.id}>{ct.name} ({ct.code})</SelectItem>)}
              </SelectGroup>
            )}
            {documentTypes.length > 0 && (
              <SelectGroup label="Типы документов (внешние)">
                {documentTypes.map(dt => <SelectItem key={dt.id} value={dt.id}>{dt.name} ({dt.code})</SelectItem>)}
              </SelectGroup>
            )}
          </Select>
        </div>
      ) : (
        <p className="text-sm text-fg3">
          Тип: <span className="font-medium text-fg2">
            {allSelectableTypes.find(ct => ct.id === entry.compositeTypeId)?.name ?? entry.compositeTypeId}
          </span>
        </p>
      )}

      {canHaveBase && (
        <BaseInstancePanel
          title={parentType?.name}
          candidates={allCandidates}
          selected={selectedBase}
          missing={!!baseRefId && !allCandidates.some(c => c.id === baseRefId)}
          manualHint={!isProxy && ownFields.length < effectiveFields.length
            ? `Без базового экземпляра все ${effectiveFields.length} полей заполняются вручную.` : undefined}
          onSelect={c => { setValue('_baseRef', c.id); setShowAllProxyFields(false); }}
          onClear={() => { setValue('_baseRef', undefined); setShowAllProxyFields(false); }}
        />
      )}

      {attachment && (
        <div className="flex items-center gap-2">
          <Button type="button" variant="tonal" size="sm" onClick={handleRecognize}
            loading={recognizing} icon={<ShieldCheck size={14} />}
            title="Заполнит простые поля по штампу/реквизитам файла — ссылочные и составные поля не тронет">
            Распознать «{attachment.fileName}»
          </Button>
          {recognizing && (
            <span className="flex items-center gap-1.5 text-xs text-fg3">
              <Loader2 size={12} className="animate-spin" /> Идёт распознавание — дождитесь завершения перед сохранением…
            </span>
          )}
        </div>
      )}

      {entry ? (
        <div className="space-y-2">
          <EntryDataSetBindings
            entryId={entry.id}
            bindings={bindings}
            schemaFields={effectiveFields}
            allDocTypes={allDocTypes}
            setId={setId}
            scope={scope}
            scopeId={scopeId}
          />
          {bindings.length > 0 && (
            <div className="flex items-center gap-2">
              <Button type="button" variant="outlined" size="sm" onClick={handleRefreshFromSource}
                loading={refreshingFromSource} icon={<RefreshCw size={14} />}>
                Обновить из источника
              </Button>
              <Button type="button" variant="outlined" size="sm" onClick={() => runBindingCheck()}
                loading={checkingBindings} icon={<ShieldCheck size={14} />}>
                Проверить связки
              </Button>
            </div>
          )}
          {bindingCheck && <BindingCheckReport items={bindingCheck.items} />}
        </div>
      ) : (
        <p className="text-xs text-fg4">Сохраните запись, чтобы привязать источники данных.</p>
      )}

      {isProxy && (
        <div className="rounded-md bg-brand-subtle/40 px-3 py-2 text-xs text-fg3 flex items-center justify-between gap-2">
          <span>Роль наследует все поля реального объекта — заполните только переопределяемые, пустые берутся у реального.</span>
          {!showAllProxyFields && (
            <button type="button" onClick={() => setShowAllProxyFields(true)}
              className="text-brand hover:text-brand-hover font-medium shrink-0">Переопределить ещё…</button>
          )}
        </div>
      )}

      {selectedType && sections.length > 0 && (
        <div className="space-y-3 pt-1 border-t border-muted">
          {sections.map(section => {
            if (!section.title) {
              return <div key={section.key}>{renderFields(section.fields)}</div>;
            }
            const isExpanded = expandedGroups.has(section.key);
            return (
              <div key={section.key} id={`cat-section-${section.key}`}
                className="border border-stroke rounded-lg overflow-hidden scroll-mt-2">
                <button type="button" onClick={() => toggleGroup(section.key)}
                  className="w-full flex items-center gap-2 px-3 py-2.5 bg-base hover:bg-muted transition-colors text-left">
                  {isExpanded
                    ? <ChevronUp size={13} className="text-fg4 shrink-0" />
                    : <ChevronDown size={13} className="text-fg4 shrink-0" />}
                  <span className="text-xs font-semibold uppercase tracking-wide text-fg2 flex-1">{section.title}</span>
                  <span className="text-xs text-fg4">{section.fields.length} п.</span>
                </button>
                {isExpanded && <div className="px-3 py-3">{renderFields(section.fields)}</div>}
              </div>
            );
          })}
        </div>
      )}

      {error && <p className="text-sm text-danger">{error}</p>}
      </div>
      </div>
      </div>
      <div className="shrink-0 px-6 py-3 border-t border-stroke flex justify-end gap-2">
        <Button type="button" variant="text" onClick={onClose}>Отмена</Button>
        <Button type="submit" variant="filled" loading={isPending} disabled={isPending || recognizing}>
          {isPending ? 'Сохранение…' : entry ? 'Сохранить' : 'Создать'}
        </Button>
      </div>
    </form>
  );
}
