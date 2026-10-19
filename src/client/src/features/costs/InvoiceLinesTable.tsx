import { useEffect, useRef, useState } from 'react';
import { CircleCheck, Save, Undo2 } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { Checkbox } from '@/shared/ui/Checkbox';
import { EditGrid } from '@/shared/ui/EditGrid';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { useToast } from '@/shared/ui/Toast';
import {
  useConfirmInvoiceFields, useInvoiceState, useReplaceInvoiceLines, type InvoiceView, type VatMode,
} from '@/shared/api/invoices';
import { K, PARSED, REJECTED, UNCONFIRMED_HINT, VAT_MODE_MARK, refEntryId } from './invoiceFields';
import { askMatchSuggestions, useLaidMatchSuggestions } from '@/shared/api/supplierMatches';
import { apiError } from '@/shared/utils/apiError';
import { SupplierMatchNote } from './SupplierMatchNote';
import {
  applyOffer, lineKey, memoryFailure, memoryToast, pending, sameKey, usable, type MatchOffer,
} from './supplierMatches';
import { useDraftBase } from './draftBase';
import { StaleInvoiceNotice } from './StaleInvoiceNotice';
import { formatMoney } from '@/shared/format/format';
import { emptyDraft, toDrafts, toPayload, totals, type LineDraft } from './invoiceLines';
import {
  modeConsequences, modeHint, modeProof, reconcile, savedExplains, vatModeOf, withMode, type Reconciliation,
} from './invoiceVat';
import { InvoiceLinesPaste } from './InvoiceLinesPaste';
import { AllocationSummary } from './LineAllocation';
import { useInvoiceLineColumns } from './invoiceLineColumns';
import { NO_ACCESS, hasPermission, useAccess } from '@/shared/api/access';
import { InvoiceReadinessLine } from './InvoiceReadinessLine';
import { READINESS_ATTR, lineAnchor, readinessItems } from './invoiceReadiness';
import { useCorrecting } from './useCorrectionDraft';

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
 * <p>⚠️ <b>«Разобран» не отключается, даже когда заведомо откажет.</b> Приглушённая кнопка молчит о
 * причине, а причин много (нет строк, ждут позиции, не заполнено обязательное, не сходится разноска),
 * и знает их сервер. Поэтому кнопка живая, а отказ приезжает от сервера с перечнем строк и полей.
 * С issue #1291 причины видны и ДО нажатия — строкой готовности под кнопкой; перечень пуст — кнопка
 * становится главной.
 * У отклонённого счёта кнопки нет вовсе — на её месте «Вернуть в черновик»: условие тут одно, оно на
 * экране, и невыполнимое действие не показывают (issue #1284).</p>
 *
 * <p>⚠️ Правки строк прежнего счёта сбрасывает ПЕРЕМОНТИРОВАНИЕ (`key={view.id}` у формы) — тем же
 * приёмом, что правки шапки: эффект со сбросом состояния рисует лишний кадр, в котором строки одного
 * счёта стоят в другом.</p>
 */
export function InvoiceLinesTable({ view, locked, readOnly = false, onDirty, headerDirty = false, onParsed }: {
  view: InvoiceView;
  /** Переход «разобран» идёт через страницу (issue #1293): она запирает форму на время записи и
   *  решает, вести ли к следующему счёту очереди. Отказ записи возвращается сюда же. */
  onParsed?: (send: () => Promise<unknown>) => Promise<unknown>;
  /** Есть ли несохранённые правки строк: о них обязан сказать диалог удаления счёта (issue #1282). */
  onDirty?: (dirty: boolean) => void;
  /** Счёт заперт закрытым периодом: строки только читаются, действий над ними нет — убраны, а не
   *  приглушены; причину называет полоса вверху формы. */
  locked: boolean;
  /** Права вводить счета нет: строки читаются так же, но разноска остаётся за своим правом. */
  readOnly?: boolean;
  /** В шапке есть несохранённое: строка готовности — о сохранённом счёте и обязана это сказать. */
  headerDirty?: boolean;
}) {
  // Исправление запертого счёта (E2, issue #1100, PR 6b-1): строки открыты, но своей записи у них нет —
  // набранное уходит в черновик исправления и записывается вместе с шапкой и разноской.
  const correcting = useCorrecting();
  const still = (locked && !correcting) || readOnly;
  const { data: access = NO_ACCESS } = useAccess();
  // Режим НДС — часть черновика строк (issue #1499): он описывает их суммы и пишется вместе с ними.
  const savedMode = vatModeOf(view);
  const [mode, setMode] = useState<VatMode>(savedMode);
  const [drafts, setDrafts] = useState<LineDraft[]>(() => toDrafts(view.lines, savedMode));
  const [dirty, setDirty] = useState(false);
  useEffect(() => onDirty?.(dirty), [dirty, onDirty]);
  const report = correcting?.setLines;
  // Нетронутые строки — `null`, а не набор: исправление шлёт их такими, какими они лежат в счёте.
  useEffect(() => report?.(dirty ? drafts : null), [report, dirty, drafts]);

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
  const state = useInvoiceState();
  const confirm = useConfirmInvoiceFields();
  const toast = useToast();

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
      setAskFailed(apiError(e));
    }
  }

  const sums = totals(drafts, mode);
  const strip = reconcile(sums, view.requisites[K.vat], view.requisites[K.total], formatMoney);
  const parsed = view.requisites[K.state] === PARSED;
  const rejected = view.requisites[K.state] === REJECTED;
  // Запертому счёту (закрытый период, удалён) строки готовности нет вовсе: «Разобран» ему откажет замок,
  // каким бы ни был перечень, и «всё готово» было бы неправдой. Права — отдельно: пункт без права виден.
  const readiness = locked ? null : readinessItems(view, {
    edit: !readOnly, allocate: hasPermission(access, 'costs.allocation.edit'),
  });
  // Главной кнопка становится только по СОХРАНЁННОМУ: с несохранённой правкой рядом переход прошёл бы
  // по прежнему состоянию, а правка осталась бы на экране несохранённой.
  const settled = readiness?.length === 0 && !dirty && !headerDirty;

  const columns = useInvoiceLineColumns({
    view, still, mode, allocationLocked: locked || correcting !== null,
    blocked: dirty ? 'Разносить можно сохранённые строки: сохраните правки строк' : null,
    offerFor, hasSupplier: supplierId !== null, onBorn: draft => void suggest([draft]),
    onAllocating: setAllocating, onEdit: edit,
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

  async function save() {
    try {
      const saved = await base.save(seen => replace.mutateAsync({
        id: view.id, seen, lines: toPayload(drafts, mode), vatMode: mode,
      }));
      // Строки перечитываем ИЗ ОТВЕТА: сервер вернул досчитанные суммы и идентификаторы новых строк,
      // а без них следующее сохранение прочиталось бы как «удали эти строки и заведи новые».
      setDrafts(toDrafts(saved.lines, vatModeOf(saved)));
      setMode(vatModeOf(saved));
      setDirty(false);
      // Один тост на сохранение, и только когда есть что сказать: запомненное меняет поведение
      // следующих счетов этого поставщика — молча это делать нельзя.
      const remembered = memoryToast(saved.memory);
      if (remembered) toast.success(remembered);
      const forgotten = memoryFailure(saved.memory);
      if (forgotten) toast.info(forgotten);
    } catch (e) {
      toast.apiError(e, 'Строки не сохранены');
    }
  }

  async function move(to: 'parsed' | 'draft') {
    // Переход утверждает то, что человек видит: версию называет вид на экране.
    // Возврат отклонённого — решение о счёте, как и отклонение: о нём говорят вслух. Вход у возврата
    // один, этот (ревью PR #1290): второй, в меню «Ещё», жил своей мутацией и своими правилами.
    try {
      const send = () => state.mutateAsync({ id: view.id, seen: view.version, to });
      await (to === 'parsed' && onParsed ? onParsed(send) : send());
      if (rejected) toast.success('Счёт возвращён в черновик.');
    }
    catch (e) { toast.apiError(e, to === 'parsed' ? 'Счёт не разобран' : 'Счёт не возвращён в черновик'); }
  }

  return (
    <section className="space-y-3" data-readiness="lines">
      <div className="flex items-center gap-2 flex-wrap">
        <h2 className="text-sm font-medium text-fg2">Строки счёта</h2>
        {/* Где менять нельзя (заперт, нет права, исправление — смена режима исправлением в #1498), флажка
            нет: «сверху» говорит пометка, «в том числе» — обычный случай, о нём молчат заголовки колонок. */}
        {still || correcting
          ? mode === 'OnTop' && <StatusBadge tone="quiet">цены без НДС, НДС сверху</StatusBadge>
          : (
            <span className={modeMarked ? 'rounded-md ring-1 ring-warning-border px-1.5 py-0.5' : undefined}>
              <Checkbox ref={modeBox} size="sm" label="Цены без НДС, НДС сверху" checked={mode === 'OnTop'}
                onChange={on => switchMode(on ? 'OnTop' : 'Included')} />
            </span>
          )}
        <div className="flex-1" />
        {!still && (
          <>
            {/* В исправлении режим не меняется (#1498): тело исправления несёт режим счёта, и вставка,
                переключившая его в черновике, легла бы бумагой другого режима. */}
            <InvoiceLinesPaste mode={mode} modeFixed={correcting !== null} onAdd={add} />
            {!correcting && (
              <Button size="sm" variant="filled" icon={<Save size={13} />} disabled={!dirty}
                loading={replace.isPending} onClick={save}>
                Сохранить строки
              </Button>
            )}
          </>
        )}
      </div>

      {/* Метка — текстом, а не одной рамкой: цвет не может быть единственным носителем смысла. */}
      {modeMarked && (
        <div className="flex items-center gap-2 flex-wrap text-xs text-warning" data-testid="vat-mode-recognized">
          <span>
            {UNCONFIRMED_HINT} ·{' '}
            {modeProof(totals(toDrafts(view.lines, savedMode), savedMode), view.requisites[K.vat],
              view.requisites[K.total], formatMoney)}
          </span>
          <Button size="sm" variant="text" loading={confirm.isPending} onClick={confirmMode}>Верно</Button>
        </div>
      )}

      {mode !== savedMode && view.lines.length > 0 && (
        <p className="text-xs text-warning">{modeConsequences(view, drafts, parsed)}</p>
      )}

      {base.stale && dirty && !correcting && <StaleInvoiceNotice what="строки" onReread={reread} />}

      {!still && drafts.length > 0 && (
        <SupplierMatchNote hasSupplier={supplierId !== null}
          waiting={drafts.filter(d => d.nomenclatureId === null).length}
          ready={ready.length} blocked={blockedOffers}
          failed={askFailed ?? (laid.isError ? apiError(laid.error) : null)}
          onApply={applyReady} />
      )}

      <EditGrid label="Строки счёта" numbered rows={drafts} rowKey={draft => draft.key} minWidth="min-w-[70rem]"
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

      <ReconciliationStrip sums={sums} strip={strip} unsaved={dirty}
        matched={drafts.filter(d => d.matchedBy !== null).length}
        hint={!still && !correcting && !dirty ? savedExplains(view) : null}
        onHint={to => switchMode(to, true)} />

      <div className="flex items-center gap-2 flex-wrap">
        {/* У отклонённого — тоже возврат: «Разобран» ему сервер откажет словами «верните в черновик». */}
        {still || correcting ? null : parsed || rejected
          ? (
            <Button size="sm" variant="outlined" icon={<Undo2 size={13} />} loading={state.isPending}
              onClick={() => move('draft')}>
              Вернуть в черновик
            </Button>
          )
          : (
            // Перечень пуст — «Разобран» становится главным действием: условия выполнены, осталось решение.
            <Button size="sm" variant={settled ? 'filled' : 'outlined'} icon={<CircleCheck size={13} />}
              loading={state.isPending} onClick={() => move('parsed')}>
              Разобран
            </Button>
          )}
        {/* У разобранного — тоже, если разноска не сходится: счёт, разобранный до F1 (#1085), разноски
            не имеет, и фраза «разноска сходится» ниже была бы про него неправдой. */}
        {(!parsed || !view.allocation.allocated) && view.lines.length > 0 && !rejected
          && <AllocationSummary allocation={view.allocation} />}
        <span className="text-xs text-fg4">
          {rejected
            ? 'Счёт отклонён: по нему не платим, в затраты и «К оплате» он не входит. Строки и разноска сохранены.'
            : parsed
            ? view.allocation.allocated
              ? 'Счёт разобран: строки есть, у всех позиция, обязательные поля заполнены, разноска сходится.'
              : 'Счёт разобран до разноски по стройкам. Пока строки не разнесены, его деньги не относятся ни к одной стройке.'
            : readiness !== null
            ? 'Разобран — это утверждение человека, что счёт сверен с бумагой.'
            : 'Разобран — это утверждение человека, что счёт сверен с бумагой. Условия проверит сервер '
              + 'и назовёт, чего не хватает.'}
        </span>
      </div>
      {/* Чего не хватает до «Разобран» — не нажимая (issue #1291). У разобранного и отклонённого строки нет. */}
      {readiness !== null && <InvoiceReadinessLine items={readiness} unsaved={dirty || headerDirty} />}
    </section>
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
 * <p>Подсказка о режиме (issue #1500) стоит на месте «сохранить всё равно можно» — рядом с числом, которое
 * объясняет, и пропадает вместе с ним. Живого региона у полосы нет нарочно: она меняется на каждом ударе
 * по клавише в клетке.</p>
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
    <div className="flex flex-wrap items-center gap-x-5 gap-y-1 rounded-lg border border-stroke
      bg-muted px-3 py-2 text-xs">
      <span className="text-fg2">Строк: <b>{sums.count}</b></span>

      {sums.withoutNomenclature > 0 && (
        <span className="text-warning">ждут позиции: <b>{sums.withoutNomenclature}</b></span>
      )}

      {matched > 0 && <span className="text-fg4">подставлено из запомненного: {matched}</span>}

      {strip.items.map(item => (
        <span key={item.label} className={TONE[item.tone]}>
          {item.label}{item.value !== null && (item.tone === 'main' ? <b>{formatMoney(item.value)}</b> : formatMoney(item.value))}
        </span>
      ))}

      {strip.difference !== null && (
        <span className="text-warning">
          расхождение: <b>{formatMoney(strip.difference)}</b>
          {offer ? null : ' — сохранить всё равно можно'}
        </span>
      )}
      {offer && (
        <span className="inline-flex flex-wrap items-center gap-x-2 text-fg2">
          {offer.text}
          <Button size="sm" variant="text" onClick={() => onHint(offer.to)}>{offer.action}</Button>
        </span>
      )}
      {strip.agrees && <span className="text-success">сходится с суммой к оплате</span>}

      {unsaved && <span className="text-fg4">(посчитано в форме — строки ещё не сохранены)</span>}
    </div>
  );
}

const TONE: Record<Reconciliation['items'][number]['tone'], string> = {
  main: 'text-fg2', quiet: 'text-fg4', warning: 'text-warning',
};

/**
 * Из чего собран черновик строк: сами строки, как их принял бы сервер, — и режим НДС: чужая смена режима
 * меняет смысл тех же чисел, и черновик без неё не пересобрался бы.
 */
function linesSignature(view: InvoiceView): string {
  const mode = vatModeOf(view);
  return JSON.stringify([mode, toPayload(toDrafts(view.lines, mode), mode)]);
}
