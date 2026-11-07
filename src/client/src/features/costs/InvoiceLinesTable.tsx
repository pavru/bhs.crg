import { useEffect, useLayoutEffect, useRef, useState, type MutableRefObject } from 'react';
import { Button } from '@/shared/ui/Button';
import { Tooltip } from '@/shared/ui/Tooltip';
import { Divider } from '@/shared/ui/Divider';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Chip } from '@/shared/ui/Chip';
import { EditGrid } from '@/shared/ui/EditGrid';
import { StatusText } from '@/shared/ui/StatusText';
import { useSnackbar } from '@/shared/ui/Snackbar';
import {
  useConfirmInvoiceFields, useReplaceInvoiceLines, type InvoiceView, type VatMode,
} from '@/shared/api/invoices';
import { K, PARSED, UNCONFIRMED_HINT, VAT_MODE_MARK, refEntryId } from './invoiceFields';
import { askMatchSuggestions, useLaidMatchSuggestions } from '@/shared/api/supplierMatches';
import { apiError } from '@/shared/utils/apiError';
import {
  applyOffer, dropForeignMarks, lineKey, memoryNotice, pending, sameKey, usable, type MatchOffer,
} from './supplierMatches';
import { useDraftBase } from './draftBase';
import { formatMoney } from '@/shared/format/format';
import { emptyDraft, toDrafts, toPayload, totals, type LineDraft } from './invoiceLines';
import {
  modeConsequences, modeHint, modeProof, reconcile, savedExplains, vatModeOf, withMode, type Reconciliation,
} from './invoiceVat';
import { InvoiceLinesPaste } from './InvoiceLinesPaste';
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
 * <p><b>Своей кнопки записи у строк нет</b> (costs-invoice-screen-10-layout, D-10.7): шапку и строки пишет
 * одна «Сохранить» в сводке, шапка раньше строк. Таблица отдаёт ей свой шаг — `saverRef`. «Разобран» и
 * «Вернуть в черновик» — тоже в сводке; под таблицей остаются сверка и строка готовности (до 11).</p>
 *
 * <p>⚠️ Правки строк прежнего счёта сбрасывает ПЕРЕМОНТИРОВАНИЕ (`key={view.id}` у формы) — тем же
 * приёмом, что правки шапки: эффект со сбросом состояния рисует лишний кадр, в котором строки одного
 * счёта стоят в другом.</p>
 */
export function InvoiceLinesTable({
  view, locked, readOnly = false, onDirty, onReport, supplierName = null, saverRef, compact = false,
}: {
  view: InvoiceView;
  /** Сохранённый поставщик названием — для плашки окна выбора позиции (issue #1517). */
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
  function switchMode(next: VatMode, focus = false) {
    if (next === mode) return;
    setMode(next);
    setDrafts(prev => withMode(prev));
    setDirty(true);
    if (focus) requestAnimationFrame(() => modeBox.current?.focus());
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
    !still);

  /**
   * Запомненное для строки: спрошенное для неё самой либо, у лежащей строки, — ответ общего вопроса.
   * Ответ общего вопроса годится, только пока ключ строки тот же, что сохранён: он был про тот текст.
   */
  function offerFor(draft: LineDraft): MatchOffer | null {
    if (draft.offer) return draft.offer;
    const saved = draft.id ? view.lines.find(line => line.id === draft.id) : undefined;
    if (!saved || !sameKey(lineKey(saved.supplierCode, saved.supplierText),
      lineKey(draft.supplierCode, draft.supplierText))) return null;
    return laid.data?.get(saved.id) ?? null;
  }

  // Строки, как они есть СЕЙЧАС, — для ответа, пришедшего позже вопроса.
  const latest = useRef(drafts);
  useEffect(() => { latest.current = drafts; });
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
    if (!supplierId) return;
    // Строка с позицией, выбранной руками, тоже спрашивается — подставлять ей нечего, но запомненное
    // знать надо: без него «запомнится» не отличить от «запомнится вместо прежнего».
    const fresh = born.filter(d => {
      const key = lineKey(d.supplierCode, d.supplierText);
      if (!key || d.declined || d.matchedBy !== null) return false;
      const signature = `${key.by}:${key.value}`;
      if (asked.current.get(d.key) === signature) return false;
      asked.current.set(d.key, signature);
      return true;
    });
    if (fresh.length === 0) return;

    try {
      const found = await askMatchSuggestions(supplierId,
        fresh.map(d => ({ supplierCode: blank(d.supplierCode), supplierText: blank(d.supplierText) })));
      setAskFailed(null);
      failedKeys.current = new Set();
      const answers = new Map(found.map(item => [fresh[item.index].key, { offer: item, asked: fresh[item.index] }]));
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
      const touched = latest.current.some(draft => fills(draft, answerFor(draft)));
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
  const hasSupplier = supplierId !== null;
  const acts = useRef<LinesActs>(null!);
  useLayoutEffect(() => {
    acts.current = {
      reread,
      apply: applyReady,
      retry: () => {
        if (laid.isError) void laid.refetch();
        if (askFailed === null) return;
        // Строки, о которых не спросилось, тем временем удалили — спрашивать не о чем, и отказа больше нет.
        const again = latest.current.filter(d => failedKeys.current.has(d.key));
        if (again.length > 0) void suggest(again); else setAskFailed(null);
      },
    };
  });
  useEffect(() => {
    onReport?.({
      stale, acts, modeChanged: mode !== savedMode,
      matches: shown ? { hasSupplier, waiting, ready: ready.length, blocked: blockedOffers, failed } : null,
    });
  }, [onReport, stale, mode, savedMode, shown, hasSupplier, waiting, ready.length, blockedOffers, failed]);
  useEffect(() => () => onReport?.({ stale: false, acts, modeChanged: false, matches: null }), [onReport]);

  const columns = useInvoiceLineColumns({
    view, drafts, still, mode, allocationLocked: locked || correcting !== null,
    blocked: dirty ? 'Разносить можно сохранённые строки: сохраните правки строк' : null,
    offerFor, hasSupplier: supplierId !== null, supplierName, onBorn: draft => void suggest([draft]),
    onAllocating: setAllocating, onEdit: edit, compact, note: noteOn, vat: vatOn,
  });

  function edit(key: string, patch: Partial<LineDraft>) {
    // НДС, набранный руками, — новое слово человека: отложенный сменой режима ему не вернуть.
    const typed = 'vatAmount' in patch ? { vatAside: undefined } : {};
    setDrafts(prev => prev.map(draft => (draft.key === key ? { ...draft, ...patch, ...typed } : draft)));
    setDirty(true);
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

  function remove(key: string) {
    setDrafts(prev => prev.filter(draft => draft.key !== key));
    setDirty(true);
  }

  /**
   * Шаг записи строк в очереди «Сохранить» (D-10.7). `after` — вид из ответа шапки, записанной этим же
   * нажатием: основа о нём ещё не знает, а версия у счёта одна.
   */
  async function save(after: InvoiceView | null): Promise<boolean> {
    // Шапка этим нажатием сменила поставщика: подставленное из памяти прежнего за ним не числится.
    const moved = after !== null && refEntryId(after.requisites[K.supplier]) !== supplierId;
    const sent = moved ? dropForeignMarks(drafts, view.lines) : { drafts, dropped: 0 };
    try {
      const saved = await base.save(seen => replace.mutateAsync({
        id: view.id, seen, lines: toPayload(sent.drafts, mode), vatMode: mode,
      }), after);
      if (sent.dropped > 0)
        toast.info(`Поставщик сменился: у ${sent.dropped === 1 ? 'одной строки' : `${sent.dropped} строк`} позиция была `
          + 'подставлена из запомненного за прежним. Позиция оставлена, пометка «запомнено» снята, за новым поставщиком '
          + 'выбор не запомнен.');
      // Строки перечитываем ИЗ ОТВЕТА: сервер вернул досчитанные суммы и идентификаторы новых строк,
      // а без них следующее сохранение прочиталось бы как «удали эти строки и заведи новые».
      setDrafts(toDrafts(saved.lines, vatModeOf(saved)));
      setMode(vatModeOf(saved));
      setDirty(false);
      // Одно уведомление на сохранение, и только когда есть что сказать: запомненное меняет поведение
      // следующих счетов этого поставщика — молча это делать нельзя.
      const notice = memoryNotice(saved.memory);
      if (notice) toast[notice.variant](notice.text);
      return true;
    } catch (e) {
      toast.apiError(e, 'Строки не сохранены');
      return false;
    }
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
            <InvoiceLinesPaste mode={mode} onAdd={add} />
          </>
        )}
      </div>

      {mode !== savedMode && view.lines.length > 0 && (
        <p className="text-body-small text-warning">{modeConsequences(view, drafts, parsed)}</p>
      )}

      {/* Колонки с шириной — 492 px, две текстовые делят остаток (D-10.10): таблица шириной с часть «Строки»
          и вбок не прокручивается от 640 px — форма рядом с файлом на 1280 шире. Уже — прокручивается
          только сама таблица. */}
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
