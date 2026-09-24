import { useState, useMemo } from 'react';
import { Loader2, ShieldCheck, Check } from 'lucide-react';
import { toggleInSet } from '@/shared/utils/toggleInSet';
import { Modal } from '@/shared/ui/Modal';
import { usePreviewDataSetBindings } from '@/shared/api/datasets';
import { useGetDocumentSet } from '@/shared/api/documentSets';
import {
  useListQualityDocs, useListMaterialLinks, useSetMaterialLinks, useRemoveMaterialLink,
  suggestLinks,
  type LinkSuggestion, type QualityDocument, type MaterialLinkInput,
  type MaterialQualityLink,
} from '@/shared/api/qualityDocs';
import {
  SCOPE_PRIORITY, type DocumentInstance, type DocumentType, type CatalogScope,
} from '@/shared/api/types';
import { groupByTargetScope, needsFallbackScope, widestTargetScope, type LinkScope } from './linkTargets';
import { collectMaterialRows, materialIdentityKeys } from '@/shared/api/schema';
import { identityKey, isIdentityKeyEmpty, normalizeKey } from '@/shared/api/identityKey';
import {
  assessBulkLink, bestSuggestion, collectStrings, collidingIdentities, docHaystackStems,
  type BulkLinkAssessment,
} from './qualityMatch';
import { QualityDocForm } from '@/features/quality-docs/QualityDocForm';
import { useToast } from '@/shared/ui/Toast';
import { isExpired } from './qualityValidity';
import type { MaterialRow } from './materialRow';
import { LinkPickerModal } from './LinkPickerModal';
import { MaterialsTable } from './MaterialsTable';
import { SuggestionsModal } from './SuggestionsModal';
import { BulkLinkMismatchDialog } from './BulkLinkMismatchDialog';
import { BreakLinkDialog } from './BreakLinkDialog';
import { ScopeToolbar } from './ScopeToolbar';
import { BulkSelectionBar } from './BulkSelectionBar';

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
    setSelected(prev => toggleInSet(prev, key));
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
      <ScopeToolbar isFetching={isFetching} refetch={refetch} materialsCount={materials.length}
        linkedCount={linkedCount} scope={scope} setScope={setScope}
        sectionId={sectionId} constructionId={constructionId} />

      {materials.length === 0 ? (
        <p className="text-sm text-fg4 text-center py-6">
          Нет материалов. Настройте набор данных (кнопка «Источники» в шапке документа) и нажмите «Обновить материалы».
        </p>
      ) : (
        <MaterialsTable
          materials={materials} selected={selected} toggle={toggle} colliding={colliding}
          findLink={findLink} suggestionByKey={suggestionByKey} scope={scope} scopeId={scopeId}
          docById={docById} docName={docName} setViewDoc={setViewDoc}
          openPickerFor={openPickerFor} setBreaking={setBreaking} acceptSuggestion={acceptSuggestion} />
      )}

      {selected.size > 0 && (
        <BulkSelectionBar selected={selected} setSelected={setSelected}
          openPickerForSelected={openPickerForSelected} widerThanSelector={widerThanSelector} />
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

      <BulkLinkMismatchDialog pendingLink={pendingLink} setPendingLink={setPendingLink}
        setSingleTarget={setSingleTarget} linkChosen={linkChosen} />

      <BreakLinkDialog breaking={breaking} setBreaking={setBreaking} shadowedBy={shadowedBy}
        removeLink={id => removeLink.mutateAsync(id)} />

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

      <SuggestionsModal suggestions={suggestions} setSuggestions={setSuggestions}
        suggestSel={suggestSel} setSuggestSel={setSuggestSel}
        applySuggestions={applySuggestions} applying={setLinks.isPending} />
    </div>
  );
}
