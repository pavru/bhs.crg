import { useEffect, useLayoutEffect, useRef, useState, type MutableRefObject } from 'react';
import { Button } from '@/shared/ui/Button';
import { Tooltip } from '@/shared/ui/Tooltip';
import { Divider } from '@/shared/ui/Divider';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Chip } from '@/shared/ui/Chip';
import { EditGrid } from '@/shared/ui/EditGrid';
import { StatusText } from '@/shared/ui/StatusText';
import { SNACKBAR_UNDO_MS, useSnackbar } from '@/shared/ui/Snackbar';
import {
  useConfirmInvoiceFields, useReplaceInvoiceLines, type InvoiceView, type VatMode,
} from '@/shared/api/invoices';
import { K, PARSED, UNCONFIRMED_HINT, VAT_MODE_MARK, refEntryId } from './invoiceFields';
import { askMatchSuggestions, useLaidMatchSuggestions } from '@/shared/api/supplierMatches';
import { apiError } from '@/shared/utils/apiError';
import {
  HELD_MEMORY_NOTICE, applyOffer, dropForeignMarks, holdMemory, lineKey, memoryNotice, offerOfSupplier, pending, sameKey,
  usable, type MatchOffer, type SupplierChange,
} from './supplierMatches';
import { useDraftBase } from './draftBase';
import { mergeSaved } from './invoiceLinesMerge';
import { formatMoney } from '@/shared/format/format';
import { emptyDraft, hasSavedAllocation, restoreLine, sameAsSaved, toDrafts, toPayload, totals, type LineDraft } from './invoiceLines';
import {
  modeConsequences, modeHint, modePreview, modeProof, reconcile, savedExplains, vatModeOf, withMode, type Reconciliation,
} from './invoiceVat';
import { VatModeDialog } from './VatModeDialog';
import { enterInLastField, leftRow } from './invoiceLineCommit';
import { InvoiceLinesPaste } from './InvoiceLinesPaste';
import { LineDeleteDialog, type LineDeleteAsk } from './LineDeleteDialog';
import { useInvoiceLineColumns } from './invoiceLineColumns';
import { READINESS_ATTR, lineAnchor } from './invoiceReadiness';
import type { MatchFacts } from './invoiceStatus';
import { useCorrecting } from './useCorrectionDraft';
import type { LinesStep } from './invoiceSave';

const blank = (value: string) => value.trim() || null;

/**
 * Строки счёта (задача C2, issue #1078, ТЗ COST-7, COST-7.2, COST-6.2).
 *
 * <p><b>Сверка суммы строк с суммой к оплате — всегда на виду и не запрет.</b> Расхождение показано
 * числом: у поставщика бывает округление, скидка строкой и доставка, не попавшая в таблицу, — и человек
 * знает об этом больше нас. Запрет означал бы, что счёт нельзя завести, пока он не сойдётся, а заводят
 * его как раз затем, чтобы разбираться.</p>
 *
 * <p><b>Строка без позиции номенклатуры сохраняется.</b> Обязательность проверяется на переходе
 * «разобран», а не при сохранении: наименования в бумаге — слова поставщика, и сопоставить их может
 * только человек или таблица соответствий (C3).</p>
 *
 * <p><b>Своей кнопки записи у строк нет</b> (D-10.7, costs-invoice-screen-12-autosave, D-12.1): шапку и строки
 * пишет одна очередь формы, шапка раньше строк. Таблица отдаёт ей свой шаг — `saverRef` — и говорит, когда
 * строки готовы к записи (`onCommit`): у черновика они пишутся сами, по уходу из строки и цельным действием.</p>
 *
 * <p>⚠️ Правки строк прежнего счёта сбрасывает ПЕРЕМОНТИРОВАНИЕ (`key={view.id}` у формы) — тем же
 * приёмом, что правки шапки: эффект со сбросом состояния рисует лишний кадр, в котором строки одного
 * счёта стоят в другом.</p>
 */
export function InvoiceLinesTable({
  view, locked, readOnly = false, onDirty, onReport, supplierName = null, saverRef, compact = false, supplierChange = null,
  autosave = false, onCommit,
}: {
  view: InvoiceView;
  /**
   * Строки пишутся сами (черновик, costs-invoice-screen-12-autosave, D-12.3): смена режима НДС — окном с
   * предпросмотром, повод записи — `onCommit`.
   */
  autosave?: boolean;
  /**
   * Строки готовы к записи (D-12.2): фокус ушёл из строки, Enter в последнем поле, вставка, выбор позиции,
   * удаление (по закрытию снэкбара «Вернуть» или по подтверждению вопроса), смена режима. Пишет очередь формы.
   */
  onCommit?: () => void;
  /**
   * Поставщик в шапке изменён и не сохранён (#1657, D3). Запомненное прежнего тогда не подставляется и не
   * предлагается (D2), а строки, записанные без шапки, своего выбора не запоминают (решение (б)).
   */
  supplierChange?: SupplierChange | null;
  /**
   * Поставщик названием — для плашки окна выбора позиции (issue #1517). Из шапки, если её смена не сохранена:
   * окно подбирает подходящие по нему же (ревью PR #1830).
   */
  supplierName?: string | null;
  /** Сюда таблица кладёт свой шаг записи — его зовёт «Сохранить» сводки после шапки (D-10.7). */
  saverRef?: MutableRefObject<LinesStep | null>;
  /** Плотный вид — рядом список счетов: уже колонки, высоты прежние (D-10.10). */
  compact?: boolean;
  /** Есть ли несохранённые правки строк: о них обязан сказать диалог удаления счёта (issue #1282). */
  onDirty?: (dirty: boolean) => void;
  /** Счёт заперт закрытым периодом: строки только читаются, действий над ними нет — убраны, а не
   *  приглушены; причину называет полоса вверху формы. */
  locked: boolean;
  /** Права вводить счета нет: строки читаются так же, но разноска остаётся за своим правом. */
  readOnly?: boolean;
  /**
   * Что таблица знает для индикатора состояния (11): строки собраны по прежнему виду и запомненное. Сообщений
   * своих у таблицы нет — пункты перечня собирает форма. Обязан быть стабильным.
   */
  onReport?: (report: LinesReport) => void;
}) {
  // Исправление запертого счёта (E2, issue #1100, PR 6b-1): строки открыты, но своей записи у них нет —
  // набранное уходит в черновик исправления и записывается вместе с шапкой и разноской.
  const correcting = useCorrecting();
  const still = (locked && !correcting) || readOnly;
  // Режим НДС — часть черновика строк (issue #1499): он описывает их суммы и пишется вместе с ними.
  const savedMode = vatModeOf(view);
  const [mode, setMode] = useState<VatMode>(savedMode);
  const [drafts, setDrafts] = useState<LineDraft[]>(() => toDrafts(view.lines, savedMode));
  const [dirty, setDirty] = useState(false);
  useEffect(() => onDirty?.(dirty), [dirty, onDirty]);
  // Повод записи отдаётся форме ПОСЛЕ слова о грязи (эффект ниже `onDirty`): поданный прямо из события, он
  // застал бы очередь, ещё не знающую о правке, и та сбросила бы пометку как «писать нечего» (вставка, выбор).
  const [commits, setCommits] = useState(0);
  const onCommitNow = useRef(onCommit);
  useLayoutEffect(() => { onCommitNow.current = onCommit; });
  useEffect(() => { if (commits > 0) onCommitNow.current?.(); }, [commits]);
  const commit = () => { if (onCommit) setCommits(n => n + 1); };
  const report = correcting?.setLines;
  const reportMode = correcting?.setVatMode;
  // Нетронутые строки — `null`, а не набор: исправление шлёт их такими, какими они лежат в счёте. Режим —
  // вместе с ними (issue #1498): он описывает их суммы, и строки без него прочлись бы бумагой другого режима.
  useEffect(() => report?.(dirty ? drafts : null), [report, dirty, drafts]);
  useEffect(() => reportMode?.(dirty ? mode : null), [reportMode, dirty, mode]);

  // На чём собран черновик строк (issue #1176). Подпись — сами строки, как их принял бы сервер: правка
  // шапки или разноски версию счёта двигает, а строк не касается — и черновик остаётся в силе.
  //
  // ⚠️ Пока открыт диалог разноски строки, таблица не пересобирается: пересборка убрала бы строку,
  // которую сосед удалил, — вместе с диалогом и набранными в нём частями, без единого слова (ревью
  // PR #1208). Диалог закрылся — пересборка происходит тем же рендером.
  const [allocating, setAllocating] = useState(false);
  // Удаление строки с разноской, ждущее ответа (#1655).
  const [asking, setAsking] = useState<LineDeleteAsk | null>(null);
  const base = useDraftBase(view, linesSignature, dirty || allocating);
  // Строки изменили, а своих правок нет — показываем свежие. Иначе таблица держала бы прежние до
  // перезагрузки страницы, и «сохранить» первой же правкой затёрло бы чужие.
  if (base.rebuild) {
    setDrafts(toDrafts(view.lines, savedMode));
    setMode(savedMode);
  }

  function reread() {
    setDrafts(toDrafts(view.lines, savedMode));
    setMode(savedMode);
    setDirty(false);
    base.rebase();
  }

  // Флажок — сюда переводится фокус после подсказки (issue #1500): кнопка подсказки пропадает с нажатием,
  // и фокус без этого ушёл бы в никуда.
  const modeBox = useRef<HTMLInputElement>(null);
  // Режим, выбранный и ждущий подтверждения (D-12.6): у черновика смена режима — явный шаг с предпросмотром.
  const [modeAsk, setModeAsk] = useState<{ to: VatMode; focus: boolean } | null>(null);
  function switchMode(next: VatMode, focus = false) {
    if (next === mode) return;
    if (autosave && !correcting) { setModeAsk({ to: next, focus }); return; }
    applyMode(next, focus);
  }
  function applyMode(next: VatMode, focus: boolean) {
    setMode(next);
    setDrafts(prev => withMode(prev));
    setDirty(true);
    if (focus) requestAnimationFrame(() => modeBox.current?.focus());
  }
  function answerMode(confirmed: boolean) {
    const ask = modeAsk;
    setModeAsk(null);
    if (!ask) return;
    if (confirmed) { applyMode(ask.to, ask.focus); commit(); } else if (ask.focus) requestAnimationFrame(() => modeBox.current?.focus());
  }

  const replace = useReplaceInvoiceLines();
  const confirm = useConfirmInvoiceFields();
  const toast = useSnackbar();

  // Режим поставило распознавание (issue #1501): флажок в рамке, рядом — числа и «Верно». Переключил
  // человек — метка гаснет сразу, как у поля шапки, не дожидаясь сохранения.
  const modeMarked = !still && !correcting && mode === savedMode && view.unconfirmed.includes(VAT_MODE_MARK);
  async function confirmMode() {
    try { await confirm.mutateAsync({ id: view.id, seen: view.version, fields: [VAT_MODE_MARK] }); }
    catch (e) { toast.apiError(e, 'Метка не снята'); }
  }

  // Запомненное (C3, issue #1079). Поставщик — тот, что СОХРАНЁН в шапке: строки пишутся отдельно от
  // неё, и сервер сверяет пометку с поставщиком, который лежит в счёте.
  const supplierId = refEntryId(view.requisites[K.supplier]);
  // Поставщик в шапке другой и не сохранён: опыт сохранённого — уже не про этот счёт (#1657, D2). Запомнится ли
  // выбор — судит поставщик из шапки: «Сохранить» записывает её раньше строк.
  const supplierPending = supplierChange !== null;
  // Поставщик, за которым строки запишутся: из шапки, если её смена не сохранена.
  const supplierNow = supplierPending ? supplierChange.to : supplierId;
  const hasSupplier = supplierNow !== null;
  const [askFailed, setAskFailed] = useState<string | null>(null);
  // Строки, о которых вопрос не прошёл, — «Повторить» спрашивает о них, а не обо всех: suggest подставляет
  // запомненное сам, а сохранённым строкам по C3 его подставляет только кнопка (ревью PR #1792).
  const failedKeys = useRef<ReadonlySet<string>>(new Set());
  // Спрашиваем обо ВСЕХ лежащих строках без пометки, а не только о ждущих позиции: человек, меняющий
  // уже стоявшую позицию, обязан увидеть «запомнится вместо прежнего» ДО сохранения, а не узнать о
  // замене из сообщения после него (ревью PR #1262).
  const laid = useLaidMatchSuggestions(supplierId,
    view.lines.filter(line => !line.match && lineKey(line.supplierCode, line.supplierText) !== null)
      .map(line => ({ id: line.id, supplierCode: line.supplierCode, supplierText: line.supplierText })),
    !still && !supplierPending);

  /**
   * Запомненное для строки: спрошенное для неё самой либо, у лежащей строки, — ответ общего вопроса.
   * Ответ общего вопроса годится, только пока ключ строки тот же, что сохранён: он был про тот текст.
   */
  function offerFor(draft: LineDraft): MatchOffer | null {
    if (supplierPending) return null;
    // Предложение в черновике строки могло прийти по прежнему поставщику — шапку с тех пор сохранили, а
    // строки нет (ревью PR #1830). Опыт другого поставщика этому счёту не предлагается.
    if (draft.offer) return offerOfSupplier(draft.offer, supplierId) ? draft.offer : null;
    const saved = draft.id ? view.lines.find(line => line.id === draft.id) : undefined;
    if (!saved || !sameKey(lineKey(saved.supplierCode, saved.supplierText),
      lineKey(draft.supplierCode, draft.supplierText))) return null;
    const found = laid.data?.get(saved.id);
    return found ? { ...found, supplierId } : null;
  }

  // Строки и режим, как они есть СЕЙЧАС, — для ответа, пришедшего позже вопроса или записи. Слойным эффектом:
  // ответ, пришедший сразу за кадром, застал бы обычный эффект ещё не отработавшим.
  const latest = useRef({ drafts, mode, supplier: supplierNow });
  useLayoutEffect(() => { latest.current = { drafts, mode, supplier: supplierNow }; });
  // О каком ключе строку уже спрашивали: уход с поля без правки вопроса не повторяет.
  const asked = useRef(new Map<string, string>());

  const ready = still ? [] : pending(drafts, offerFor);
  const blockedOffers = still ? 0
    : drafts.filter(d => d.nomenclatureId === null && offerFor(d) !== null && !usable(offerFor(d))).length;

  function applyReady() {
    const keys = new Set(ready.map(draft => draft.key));
    setDrafts(prev => prev.map(draft => {
      const offer = offerFor(draft);
      return keys.has(draft.key) && usable(offer) ? { ...draft, ...applyOffer(offer) } : draft;
    }));
    setDirty(true);
  }

  /**
   * Строка РОДИЛАСЬ (вставка, набор) — запомненное подставляется само (ТЗ COST-7.1). Вопрос идёт на
   * сервер, а пока он идёт, строку могли разобрать руками, отменить или переписать — такую не трогаем:
   * ответ был про прежний текст.
   */
  async function suggest(born: readonly LineDraft[]) {
    // Смена поставщика не сохранена — спрашивать прежнего незачем (D2). Строку не помечаем спрошенной: после
    // сохранения шапки о ней спросит правка ключа, а о лежащей — общий вопрос по новому поставщику.
    if (!supplierId || supplierPending) return;
    const askedFor = supplierId;
    // Строка с позицией, выбранной руками, тоже спрашивается — подставлять ей нечего, но запомненное
    // знать надо: без него «запомнится» не отличить от «запомнится вместо прежнего».
    const fresh = born.filter(d => {
      const key = lineKey(d.supplierCode, d.supplierText);
      if (!key || d.declined || d.matchedBy !== null) return false;
      // Поставщик — часть вопроса: о той же строке у другого поставщика спрашивают заново.
      const signature = `${askedFor}:${key.by}:${key.value}`;
      if (asked.current.get(d.key) === signature) return false;
      asked.current.set(d.key, signature);
      return true;
    });
    if (fresh.length === 0) return;

    try {
      const found = await askMatchSuggestions(askedFor,
        fresh.map(d => ({ supplierCode: blank(d.supplierCode), supplierText: blank(d.supplierText) })));
      // Пока шёл вопрос, поставщика сменили — ответ про опыт другого поставщика, и в строки он не ложится
      // (ревью PR #1830). О строках спросит следующий вопрос — по нынешнему.
      if (latest.current.supplier !== askedFor) {
        fresh.forEach(d => asked.current.delete(d.key));
        return;
      }
      setAskFailed(null);
      failedKeys.current = new Set();
      const answers = new Map(found.map(item => [fresh[item.index].key, { offer: { ...item, supplierId: askedFor }, asked: fresh[item.index] }]));
      if (answers.size === 0) return;

      // Ответ про строку, которую тем временем отменили, пометили или переписали, к ней не относится.
      const answerFor = (draft: LineDraft) => {
        const answer = answers.get(draft.key);
        if (!answer || draft.declined || draft.matchedBy !== null) return null;
        return sameKey(lineKey(draft.supplierCode, draft.supplierText),
          lineKey(answer.asked.supplierCode, answer.asked.supplierText)) ? answer.offer : null;
      };
      const fills = (draft: LineDraft, offer: MatchOffer | null) => draft.nomenclatureId === null && usable(offer);

      // «Есть несохранённое» — только если позиция действительно легла в строку: ответ, которому
      // некуда лечь (строку удалили, строки сохранили), правкой не является (ревью PR #1262).
      const touched = latest.current.drafts.some(draft => fills(draft, answerFor(draft)));
      setDrafts(prev => prev.map(draft => {
        const offer = answerFor(draft);
        if (!offer) return draft;
        return fills(draft, offer) ? { ...draft, ...applyOffer(offer) } : { ...draft, offer };
      }));
      if (touched) setDirty(true);
    } catch (e) {
      // Вопрос не прошёл — пусть следующий уход с поля задаст его заново.
      fresh.forEach(d => asked.current.delete(d.key));
      failedKeys.current = new Set([...failedKeys.current, ...fresh.map(d => d.key)]);
      setAskFailed(apiError(e));
    }
  }

  // Смену поставщика отменили, не сохраняя, — вернули прежнего (ревью PR #1830): о строках, родившихся за это
  // время, спрашивают теперь. Смену сохранили — поставщик в виде другой, и не спрашиваем: строки лягут лежащими, и о
  // них спросит общий вопрос по новому поставщику. Кадр между сбросом правок шапки и приходом её ответа выглядит
  // отменой — ответ такого вопроса отбрасывает сверка поставщика в `suggest`.
  const suggestNow = useRef(suggest);
  useLayoutEffect(() => { suggestNow.current = suggest; });
  const pendingFrom = useRef<string | null | undefined>(undefined);
  useEffect(() => {
    if (supplierPending) {
      if (pendingFrom.current === undefined) pendingFrom.current = supplierId;
      return;
    }
    const from = pendingFrom.current;
    pendingFrom.current = undefined;
    if (from !== undefined && from === supplierId) void suggestNow.current(latest.current.drafts.filter(d => d.id === null));
  }, [supplierPending, supplierId]);

  // Колонки, которых в таблице по умолчанию нет (D-10.10): НДС строки и примечание. Ни ввести примечание,
  // ни поправить НДС строки, неверно прочитанный со скана, иначе было бы негде (ревью PR #1786) — их
  // включает чип над таблицей. Колонка примечания, раз показанная, не пропадает у человека под курсором,
  // когда он стирает последнее: она держится до выключения чипа, а с набранным примечанием не выключается.
  const hasNote = drafts.some(draft => draft.note.trim() !== '');
  const [noteOn, setNoteOn] = useState(hasNote);
  if (hasNote && !noteOn) setNoteOn(true);
  const [vatOn, setVatOn] = useState(false);

  const sums = totals(drafts, mode);
  const strip = reconcile(sums, view.requisites[K.vat], view.requisites[K.total], formatMoney);
  const parsed = view.requisites[K.state] === PARSED;
  // Для индикатора (11): факты — значениями, действия — через ссылку, чтобы отчёт не менялся на каждом рендере.
  const stale = base.stale && dirty && !correcting;
  const failed = askFailed ?? (laid.isError ? apiError(laid.error) : null);
  const waiting = drafts.filter(d => d.nomenclatureId === null).length;
  const shown = !still && drafts.length > 0;
  const acts = useRef<LinesActs>(null!);
  useLayoutEffect(() => {
    acts.current = {
      reread,
      apply: applyReady,
      retry: () => {
        if (laid.isError) void laid.refetch();
        if (askFailed === null) return;
        // Строки, о которых не спросилось, тем временем удалили — спрашивать не о чем, и отказа больше нет.
        const again = latest.current.drafts.filter(d => failedKeys.current.has(d.key));
        if (again.length > 0) void suggest(again); else setAskFailed(null);
      },
    };
  });
  useEffect(() => {
    onReport?.({
      stale, acts, modeChanged: mode !== savedMode,
      matches: shown ? { hasSupplier, supplierPending, waiting, ready: ready.length, blocked: blockedOffers, failed } : null,
    });
  }, [onReport, stale, mode, savedMode, shown, hasSupplier, supplierPending, waiting, ready.length, blockedOffers, failed]);
  useEffect(() => () => onReport?.({ stale: false, acts, modeChanged: false, matches: null }), [onReport]);

  const columns = useInvoiceLineColumns({
    view, drafts, still, mode, allocationLocked: locked || correcting !== null,
    blocked: dirty
      ? autosave ? 'Разносить можно записанные строки: уйдите из строки — она запишется сама' : 'Разносить можно сохранённые строки: сохраните правки строк'
      : null,
    offerFor, hasSupplier, supplierPending, supplier: supplierNow, supplierName, onBorn: draft => void suggest([draft]),
    onAllocating: setAllocating, onEdit: edit, onChoose: choose, compact, note: noteOn, vat: vatOn,
  });

  function edit(key: string, patch: Partial<LineDraft>) {
    // НДС, набранный руками, — новое слово человека: отложенный сменой режима ему не вернуть.
    const typed = 'vatAmount' in patch ? { vatAside: undefined } : {};
    setDrafts(prev => prev.map(draft => (draft.key === key ? { ...draft, ...patch, ...typed } : draft)));
    setDirty(true);
  }
  /** Правка цельным действием — выбор позиции, решение пометки: пишется сразу (D-12.2). */
  function choose(key: string, patch: Partial<LineDraft>) {
    edit(key, patch);
    commit();
  }

  /** Строки добавлены; вставка с отметкой «цены без НДС» приносит и режим (issue #1499). */
  function add(added: LineDraft[], pastedMode?: VatMode) {
    if (pastedMode && pastedMode !== mode) {
      setMode(pastedMode);
      setDrafts(prev => [...withMode(prev), ...added]);
    } else setDrafts(prev => [...prev, ...added]);
    setDirty(true);
    void suggest(added);
  }

  /**
   * Удаление строки. Без сохранённой разноски — сразу, со снэкбаром «Строка N удалена · Вернуть»
   * (costs-invoice-screen-13-undo, D-13.2); «Вернуть» сверяет строки счёта на миг нажатия: их могли записать. С
   * разноской — сначала вопрос, называющий её (#1655, D1): разноску после записи не вернуть ничем, и «Вернуть» у
   * неё нет — строка уходит, только когда человек подтвердил.
   */
  function remove(key: string): Promise<boolean> | undefined {
    const index = drafts.findIndex(draft => draft.key === key);
    const line = drafts[index];
    // Исход вопроса — сетке: она ставит фокус в поле строки на месте удалённой или обратно на корзину (D2).
    if (line && hasSavedAllocation(line, view.lines)) return new Promise(answer => setAsking({ key, answer }));
    drop(key);
    if (!line) return;
    const restore = () => {
      // Таблицы уже нет — ушли со счёта: «Вернуть», который молча ничего не делает, был бы ложью.
      if (!alive.current) { toast.info(`Строка ${index + 1} не возвращена: счёт закрыт. Откройте его и добавьте строку заново.`); return; }
      const back = restoreLine(latest.current.drafts, line, index, savedLines.current);
      setDrafts(back);
      // «Вернуть» без других правок — строки снова как записаны: дописывать нечего, удаление не уходит (Э5).
      setDirty(!sameAsSaved(back, latest.current.mode, savedLines.current, savedModeNow.current));
    };
    // Удаление пишется, когда снэкбар закрылся — любым исходом (D-12.2, D-13.2): после «Вернуть» строка снова в
    // черновике, и запись положит то, что на экране. Без автосохранения отклик не нужен.
    toast.success(`Строка ${index + 1} удалена.`, {
      duration: SNACKBAR_UNDO_MS, action: { label: 'Вернуть', onClick: restore },
      ...(onCommit ? { onClose: () => { if (alive.current) commit(); } } : {}),
    });
  }
  function drop(key: string) {
    setDrafts(prev => prev.filter(draft => draft.key !== key));
    setDirty(true);
  }

  // Строка, о которой спрашивают, — какой она стала сейчас: пока окно открыто, таблицу могли запереть, а строки —
  // перечитать (у другого номера, без разноски, без самой строки). Тогда вопроса больше нет: окно закрывается, а
  // строка остаётся; исход сетке не сообщается — фокус не трогается.
  const askingIndex = asking ? drafts.findIndex(draft => draft.key === asking.key) : -1;
  const askingDraft = askingIndex >= 0 && !still ? drafts[askingIndex] : undefined;
  const askingSaved = askingDraft && hasSavedAllocation(askingDraft, view.lines)
    ? view.lines.find(saved => saved.id === askingDraft.id) ?? null
    : null;
  if (asking && !askingSaved) setAsking(null);

  /** Ответ окна: подтверждение удаляет строку тем же рендером, что закрывает окно; отказ — оставляет. */
  function answerAsking(confirmed: boolean) {
    if (!asking) return;
    if (confirmed) { drop(asking.key); commit(); }
    setAsking(null);
    asking.answer(confirmed);
  }
  const savedLines = useRef(view.lines);
  const savedModeNow = useRef(savedMode);
  useLayoutEffect(() => { savedLines.current = view.lines; savedModeNow.current = savedMode; });
  const alive = useRef(true);
  const grid = useRef<HTMLDivElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);

  /**
   * Шаг записи строк в очереди формы (D-12.1). `after` — вид из ответа шапки, записанной прямо перед строками:
   * основа о нём ещё не знает, а версия у счёта одна. Отказ — исключением: его называет очередь (носитель).
   */
  async function save(after: InvoiceView | null): Promise<InvoiceView> {
    // Подставленное из памяти другого поставщика за тем, кто будет лежать в счёте, не числится: шапка сменила
    // его этим нажатием либо прошлым, когда строки не записались (ревью PR #1830).
    const target = after !== null ? refEntryId(after.requisites[K.supplier]) : supplierId;
    const sent = dropForeignMarks(drafts, view.lines, target);
    // Строки уходят без шапки, а смена поставщика в ней не сохранена (#1657, решение (б)): выбор не запоминается
    // вовсе — сервер записал бы его за прежним поставщиком.
    const hold = supplierPending && after === null ? holdMemory(sent.drafts, view.lines) : { drafts: sent.drafts, held: false };
    // Снимок черновика на миг отправки — с ним сверяется набранное, пока запись идёт (issue #1656).
    const snapshot = { drafts, mode };
    const saved = await base.save(seen => replace.mutateAsync({
      id: view.id, seen, lines: toPayload(hold.drafts, mode), vatMode: mode,
    }), after);
    // Ответ своей записи — основа черновика, а не чужая правка: следующая запись назовёт его версию.
    base.adopt(saved);
    if (sent.dropped > 0)
      toast.info(`Поставщик сменился: у ${sent.dropped === 1 ? 'одной строки' : `${sent.dropped} строк`} позиция была `
        + 'подставлена из запомненного за прежним. Позиция оставлена, пометка «запомнено» снята, за новым поставщиком '
        + 'выбор не запомнен.');
    // Строки берём ИЗ ОТВЕТА — досчитанные суммы и идентификаторы, — но только те, что не правили, пока
    // запись шла: набранное за это время остаётся на экране несохранённым (issue #1656, `mergeSaved`).
    const merged = mergeSaved(snapshot, latest.current, saved);
    setDrafts(merged.drafts);
    setMode(merged.mode);
    setDirty(merged.dirty);
    // Одно уведомление на сохранение, и только когда есть что сказать: запомненное меняет поведение
    // следующих счетов этого поставщика — молча это делать нельзя.
    const notice = hold.held ? { variant: 'info' as const, text: HELD_MEMORY_NOTICE } : memoryNotice(saved.memory);
    if (notice) toast[notice.variant](notice.text);
    return saved;
  }
  // Шаг отдаётся очереди свежим — слойным эффектом, как колбэк охраны ухода: замыкание помнит строки.
  // Запертым строкам и исправлению своей записи нет: у исправления запись одна, своя.
  //
  // ⚠️ Снимает шаг только СВОЙ (ревью PR #1787): при перемонтировании по `key={correction.generation}`
  // уборка прежней таблицы идёт ПОСЛЕ слойного эффекта новой и обнулила бы уже отданный ею шаг.
  const own = useRef<LinesStep | null>(null);
  useLayoutEffect(() => {
    own.current = still || correcting ? null : save;
    if (saverRef) saverRef.current = own.current;
  });
  useEffect(() => () => { if (saverRef && saverRef.current === own.current) saverRef.current = null; }, [saverRef]);

  return (
    <div className="space-y-3" data-readiness="lines">
      <div className="flex items-center gap-2 flex-wrap">
        {/* Где менять нельзя (заперт без исправления, нет права), флажка нет: «сверху» говорит пометка, «в том
            числе» — обычный случай, о нём молчат заголовки колонок. Исправление режим меняет (#1498). */}
        {still
          ? mode === 'OnTop' && <StatusText tone="quiet">цены без НДС, НДС сверху</StatusText>
          : (
            <span className={modeMarked ? 'rounded-sm ring-1 ring-warning px-1.5 py-0.5' : undefined}>
              <Checkbox ref={modeBox} label="Цены без НДС, НДС сверху" checked={mode === 'OnTop'}
                onChange={on => switchMode(on ? 'OnTop' : 'Included')} />
            </span>
          )}
        {/* Метка режима со скана — пункт перечня (11, 36), «Верно» — здесь: сверяют режим у переключателя. Числа
            для сверки — в подсказке кнопки и в пункте: рамка не единственный носитель смысла. */}
        {modeMarked && (
          <Button variant="text" loading={confirm.isPending} onClick={confirmMode} data-testid="vat-mode-recognized"
            aria-label={`Верно: режим НДС — ${mode === 'OnTop' ? 'цены без НДС, НДС сверху' : 'НДС в сумме строк'}`}
            title={`${UNCONFIRMED_HINT}: ${modeProof(view.totals, view.requisites[K.vat], view.requisites[K.total], formatMoney)}`}>
            Верно
          </Button>
        )}
        <div className="flex-1" />
        {!still && (
          <>
            <Chip selected={vatOn} onClick={() => setVatOn(on => !on)}>НДС строк</Chip>
            <Chip selected={noteOn} disabled={noteOn && hasNote}
              title={noteOn && hasNote ? 'У строк есть примечания — колонку не спрятать' : undefined}
              onClick={() => setNoteOn(on => !on)}>
              Примечание
            </Chip>
            <InvoiceLinesPaste mode={mode} onAdd={(rows, pastedMode) => { add(rows, pastedMode); commit(); }} />
          </>
        )}
      </div>

      {mode !== savedMode && view.lines.length > 0 && (
        <p className="text-body-small text-warning">{modeConsequences(view, drafts, parsed)}</p>
      )}

      {/* Колонки с шириной — 492 px, две текстовые делят остаток (D-10.10): таблица шириной с часть «Строки»
          и вбок не прокручивается от 640 px — форма рядом с файлом на 1280 шире. Уже — прокручивается
          только сама таблица. */}
      {/* Повод записи — уход из строки и Enter в последнем поле (D-12.2): слушаем вокруг сетки, сетка общая. */}
      <div ref={grid} onBlur={e => { if (onCommit && leftRow(e.target, e.relatedTarget, grid.current)) commit(); }}
        onKeyDown={e => { if (onCommit && enterInLastField(e.key, e.target, grid.current)) commit(); }}>
      <EditGrid label="Строки счёта" numbered fill minWidth="min-w-160" rows={drafts} rowKey={draft => draft.key}
        empty={(
          <>
            Строк нет. Черновик живёт и без них — заведите их с клавиатуры или вставьте таблицу из
            счёта. «Разобран» без строк не проходит, и это единственное, чего они стоят.
          </>
        )}
        rowData={(_, index) => ({ [READINESS_ATTR]: lineAnchor(index + 1) })}
        add={still ? undefined : { label: 'Добавить строку', onAdd: () => add([emptyDraft()]) }}
        onRemove={still ? undefined : draft => remove(draft.key)}
        columns={columns} />
      </div>
      <VatModeDialog to={modeAsk?.to ?? null} preview={modeAsk ? modePreview(drafts, mode, modeAsk.to) : null}
        consequences={modeAsk && view.lines.length > 0 ? modeConsequences(view, withMode(drafts), parsed) : null}
        onConfirm={() => answerMode(true)} onCancel={() => answerMode(false)} />
      <LineDeleteDialog saved={askingSaved} number={askingIndex + 1} onClose={answerAsking} />

      {/* Сверка — липкой строкой под таблицей, пока таблица в окне (D1, ТЗ COST-6.2): у сорока строк она
          видна, где бы ни стояла прокрутка. Без своей рамки — черта сверху (`Divider`). */}
      <div className="sticky bottom-0 z-10 bg-surface pb-2" data-testid="invoice-reconciliation">
        <Divider />
        <ReconciliationStrip sums={sums} strip={strip} unsaved={dirty}
          matched={drafts.filter(d => d.matchedBy !== null).length}
          hint={!still && !correcting && !dirty ? savedExplains(view) : null}
          onHint={to => switchMode(to, true)} />
      </div>
    </div>
  );
}

/**
 * Сверка: суммы строк, НДС, сумма к оплате из бумаги и расхождение — порядком бумаги (issue #1499). Ни
 * одно из чисел не запрет.
 *
 * <p>⚠️ Пока строки не сохранены, числа посчитаны ЗДЕСЬ, и об этом сказано: после сохранения их
 * возвращает сервер, и они могут разойтись с нашими на копейку округления. Молчаливое «сумма 4850»,
 * которое после сохранения стало «4850,01», читалось бы как ошибка ввода.</p>
 *
 * <p>Полоса — числа и тон (11, 41): что расхождение значит и что оно не запрет, говорит пункт перечня
 * состояния, по сохранённому. Подсказка о режиме (issue #1500) — рядом с числом, которое объясняет, и
 * пропадает вместе с ним. Живого региона у полосы нет нарочно: она меняется на каждом ударе по клавише в
 * клетке.</p>
 */
function ReconciliationStrip({ sums, strip, unsaved, matched, hint, onHint }: {
  /** Сколько позиций подставлено из запомненного — их человек не выбирал, и это видно числом. */
  matched: number;
  sums: { count: number; withoutNomenclature: number };
  strip: Reconciliation;
  unsaved: boolean;
  /** Сервер видит, что расхождение объясняет режим НДС; `null` — не видит или строки правятся. */
  hint: 'onTop' | 'included' | null;
  onHint: (to: VatMode) => void;
}) {
  // Догадку сервер даёт только при расхождении сверх допуска, и строки при ней сохранены: свою сверку
  // форма здесь не спрашивает. Сверху без НДС у строк она расхождения не показывает вовсе («не посчитать»),
  // а флажок, поставленный зря, — ровно этот случай (ревью PR #1508).
  const offer = hint ? modeHint(hint) : null;
  return (
    <div className="flex flex-wrap items-center gap-x-5 gap-y-1 py-2 text-body-small">
      <span className="text-on-surface-variant">Строк: <b>{sums.count}</b></span>

      {sums.withoutNomenclature > 0 && (
        <span className="text-warning">ждут позиции: <b>{sums.withoutNomenclature}</b></span>
      )}

      {matched > 0 && <span className="text-on-surface-variant">подставлено из запомненного: {matched}</span>}

      {strip.items.map(item => (
        <span key={item.label} className={TONE[item.tone]}>
          {item.label}{item.value !== null && (item.tone === 'main' ? <b>{formatMoney(item.value)}</b> : formatMoney(item.value))}
        </span>
      ))}

      {strip.difference !== null && (
        // Не запрет — подсказкой и описанием для диктора, а не фразой в полосе (41): пока строки не сохранены,
        // пункта перечня о расхождении ещё нет, и число в тоне предупреждения читалось бы запретом (ревью PR #1794).
        <Tooltip content={NOT_A_BAN}>
          <span className="text-warning" data-testid="reconciliation-difference">
            расхождение: <b>{formatMoney(strip.difference)}</b>
          </span>
        </Tooltip>
      )}
      {offer && (
        <span className="inline-flex flex-wrap items-center gap-x-2 text-on-surface-variant">
          {offer.text}
          <Button variant="text" onClick={() => onHint(offer.to)}>{offer.action}</Button>
        </span>
      )}
      {strip.agrees && <span className="text-success">сходится с суммой к оплате</span>}

      {unsaved && <span className="text-on-surface-variant">(посчитано в форме — строки ещё не сохранены)</span>}
    </div>
  );
}

/** Что расхождение не запрет — подсказкой у числа в полосе. */
const NOT_A_BAN = 'Не запрет: строки и счёт сохраняются и с расхождением';

const TONE: Record<Reconciliation['items'][number]['tone'], string> = {
  main: 'text-on-surface-variant', quiet: 'text-on-surface-variant', warning: 'text-warning',
};

/**
 * Из чего собран черновик строк: сами строки, как их принял бы сервер, — и режим НДС: чужая смена режима
 * меняет смысл тех же чисел, и черновик без неё не пересобрался бы.
 */
function linesSignature(view: InvoiceView): string {
  const mode = vatModeOf(view);
  return JSON.stringify([mode, toPayload(toDrafts(view.lines, mode), mode)]);
}

/** Действия таблицы, которые зовёт индикатор: перечитать строки, подставить запомненное, повторить вопрос. */
export interface LinesActs {
  reread: () => void;
  apply: () => void;
  retry: () => void;
}

/** Отчёт таблицы строк форме — для пунктов индикатора состояния (11). */
export interface LinesReport {
  /** Строки набраны поверх прежнего вида счёта. */
  stale: boolean;
  /** Режим НДС переключён и не сохранён: метка режима со скана к набранному не относится (ревью PR #1794). */
  modeChanged: boolean;
  matches: MatchFacts | null;
  acts: MutableRefObject<LinesActs>;
}
