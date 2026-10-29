import { LOST, MISSING } from './lostReferences';
import { useEffect, useRef, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CheckCircle, EditNote, Refresh, Save, Undo, Warning } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { LeaveGuardDialog } from '@/shared/ui/LeaveGuardDialog';
import { useLeaveGuard } from '@/shared/ui/NavigationGuard';
import { useToast } from '@/shared/ui/Toast';
import type { CostsConstruction } from '@/shared/api/invoices';
import { useCan } from '@/shared/api/access';
import { useMedia, WIDE } from '@/shared/hooks/useMedia';
import {
  useCorrectWaybill, useFreshWaybill, useMatchWaybillLine, useSaveWaybill, useWaybillState, type WaybillView,
} from '@/shared/api/waybills';
import { NomenclaturePicker } from './NomenclaturePicker';
import { neighboursOf } from './pickerCandidates';
import { waybillLineWhere } from './pickerLine';
import { WaybillDeleteMenu } from './WaybillDeleteDialog';
import { WaybillCorrectionBar, WaybillLockNote } from './WaybillCorrectionBar';
import { WaybillHistory } from './WaybillHistory';
import { mayCorrectWaybill } from './waybillCorrection';
import { withArchiveWord } from '@/shared/ui/archive';
import { pickSite, REVEAL_CHOICE, useSiteChoices } from '@/shared/siteFilter/siteReveal';
import { RevealItem, SiteSelectItems } from '@/shared/siteFilter/SiteOptions';
import { formatDate } from '@/shared/format/format';
import { NumberInput } from '@/shared/ui/NumberInput';
import { EditGrid } from '@/shared/ui/EditGrid';
import {
  emptyLine, headerPayload, isDirty, linesPayload, toDraft, unmatchedCount, unmatchedNote, withoutBlanks,
  type WaybillDraft, type WaybillLineDraft,
} from './waybills';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import { DateField } from '@/shared/ui/DateField';

const cell = 'w-full rounded-xs border border-outline-variant bg-surface px-1.5 py-1 text-xs text-on-surface outline-none '
  + 'focus:border-primary placeholder:text-on-surface-variant disabled:bg-surface-container disabled:text-on-surface-variant';

/**
 * Форма расходной накладной (задача D1 этапа 2, issue #1083; ТЗ COST-5, COST-17).
 *
 * <p><b>Черновик правят целиком, проведённую — нет.</b> Строки проведённой накладной уже лежат в
 * перечне отпущенного на стройку, и тихая правка количества меняла бы его без следа. Исправить —
 * вернуть в черновик, поправить, провести снова.</p>
 *
 * <p><b>Запертую закрытым периодом — исправлением</b> (issue #1340, макет 1a, design D10): «Исправить»
 * переключает ту же форму в режим правки, причина и всё содержимое уходят одним телом, прежнее ложится
 * версией в историю. Позиция строки в этом режиме кладётся в черновик исправления, а не пишется сразу.</p>
 *
 * <p>⚠️ <b>Исключение — позиция номенклатуры:</b> её выбирают и у проведённой, и сохраняется она сразу,
 * без кнопки «Сохранить»: накладная из 1С придёт проведённой и с несопоставленными строками, и
 * распроводить её ради справочника незачем.</p>
 *
 * <p>⚠️ Число несопоставленных строк стоит на виду всегда, пока оно не ноль: эти строки в «материалы
 * на объекте» не попадают, и промолчать об этом значило бы показать перечень, который выглядит
 * полным.</p>
 *
 * <p>⚠️ <b>Набранное не пропадает молча</b> (ревью PR #1206). Свежий ответ сервера форма берёт, только
 * пока в ней нет несохранённого: фоновое перечитывание иначе стирало бы строки, вписанные с бумаги.
 * Если накладную тем временем изменили, форма говорит об этом и предлагает перечитать; сохранение
 * устаревшей формы сервер отвергает по версии (заголовок <c>If-Match</c>). Уход со страницы и выбор другой
 * накладной спрашивают, что делать с правками.</p>
 */
export function WaybillForm({ view: fresh, sites, sitesFailed, canEdit: mayEdit, gone, onLeaveGuard, onClose }: {
  /** Накладная, как её сейчас знает кэш. Форма работает по той, по которой собрана, — см. `view` ниже. */
  view: WaybillView;
  sites: CostsConstruction[];
  /** Список строек не пришёл: выбрать получателя не из чего, и это отказ, а не «строек нет». */
  sitesFailed: boolean;
  canEdit: boolean;
  /**
   * Форма сообщает странице вопрос «что делать с правками», пока они есть: страница задаёт его перед
   * выбором другой накладной. `null` — спрашивать не о чем. Ссылка обязана быть устойчивой.
   */
  onLeaveGuard: (ask: ((proceed: () => void) => void) | null) => void;
  /**
   * Накладной больше нет — её удалили, пока она была открыта (issue #1283). Форма остаётся с набранным,
   * чтобы его можно было перенести, но действий записи у неё нет: сохранять уже некуда.
   */
  gone: boolean;
  /** Закрыть форму накладной, которой нет: удалённой отсюда либо соседом. Страница чистит кэш. */
  onClose: () => void;
}) {
  const canEdit = mayEdit && !gone;
  // Накладная, по которой собрана форма: с ней сверяется «изменено ли» и её версию называет сохранение.
  const [view, setView] = useState(fresh);
  const [draft, setDraft] = useState<WaybillDraft>(() => toDraft(fresh));
  const adopt = (next: WaybillView) => { setView(next); setDraft(toDraft(next)); };
  const can = useCan();
  const wide = useMedia(WIDE);
  // Режим исправления запертой накладной и его причина: причина — про намерение, перечитывание её не стирает.
  const [correcting, setCorrecting] = useState(false);
  const [reason, setReason] = useState('');
  const correctable = mayCorrectWaybill(view, can.permission, gone);

  const editable = canEdit && (view.state !== 'Posted' || correcting);
  // Свежий ответ берём, только когда терять нечего. Идентификаторы новых строк и названия позиций
  // после своего сохранения приходят этим же путём.
  if (fresh !== view && !(editable && isDirty(draft, view))) adopt(fresh);
  const outdated = fresh !== view && fresh.version !== view.version;

  const save = useSaveWaybill();
  const state = useWaybillState();
  const match = useMatchWaybillLine();
  const correct = useCorrectWaybill();
  const readFresh = useFreshWaybill();
  const toast = useToast();
  // Очередь сопоставлений: см. matchLine.
  const matching = useRef<{ tail: Promise<string | null>; waiting: number }>({ tail: Promise.resolve(null), waiting: 0 });

  const posted = view.state === 'Posted';
  // Заперта закрытым периодом: ни вернуть в черновик, ни сопоставить строку сервер не даст — и
  // кнопок для этого на экране нет, есть причина.
  const closed = posted ? view.locked ?? null : null;
  const moved = posted && view.accountingOn && view.accountingOn !== view.issuedOn ? view.accountingOn : null;
  // Проведена, а учётной даты нет — накладная из резервной копии, снятой до появления этой даты.
  const undated = posted && !view.accountingOn;
  const locked = !editable;
  const dirty = editable && isDirty(draft, view);
  // Набранная причина — тоже правка: уйти молча значило бы её потерять.
  const unsaved = dirty || (correcting && reason.trim() !== '');
  // Закрытие отменили, пока шла правка, — исправлять больше нечего (кэш знает это раньше формы).
  const unneeded = correcting && !fresh.locked;

  const [leave, setLeave] = useState<(() => void) | null>(null);
  useLeaveGuard(unsaved, proceed => setLeave(() => proceed));
  useEffect(() => {
    onLeaveGuard(unsaved ? proceed => setLeave(() => proceed) : null);
    return () => onLeaveGuard(null);
  }, [onLeaveGuard, unsaved]);
  const busy = save.isPending || state.isPending || match.isPending || correct.isPending;
  const unmatched = dirty ? unmatchedCount(draft) : view.totals.unmatched;
  const note = unmatchedNote(unmatched, posted,
    closed ? (correcting ? 'correcting' : correctable ? 'correct' : 'closed') : null);

  const edit = (patch: Partial<WaybillDraft>) => setDraft(d => ({ ...d, ...patch }));
  const editLine = (key: string, patch: Partial<WaybillLineDraft>) =>
    setDraft(d => ({ ...d, lines: d.lines.map(l => l.key === key ? { ...l, ...patch } : l) }));

  // Недописанная дата отпуска наружу не уходит: в черновике осталась прежняя. Записать её молча значило бы
  // сохранить или провести не ту дату, что на экране (ревью PR #1556).
  const [dateProblem, setDateProblem] = useState<string | null>(null);
  const refusedForDate = () => {
    if (!dateProblem) return false;
    toast.error(`Накладная не записана: в дате отпуска ошибка — «${dateProblem}».`);
    return true;
  };

  /** Сохранить черновик; возвращает записанное — следующему действию нужна его версия — либо null. */
  async function saveDraft(): Promise<WaybillView | null> {
    if (refusedForDate()) return null;
    // Пустые заготовки уходят и с экрана: номер строки в отказе сервера обязан совпасть с номером здесь.
    const sent = withoutBlanks(draft);
    if (sent !== draft) setDraft(sent);
    try {
      const saved = await save.mutateAsync({
        id: view.id, seen: view.version, header: headerPayload(sent), lines: linesPayload(sent),
      });
      adopt(saved);
      return saved;
    } catch (e) { toast.apiError(e, 'Накладная не сохранена'); return null; }
  }

  async function post() {
    // Проводится то, что на экране: несохранённая правка, оставшаяся в форме, иначе пропала бы молча,
    // а проведённым оказалось бы прежнее состояние.
    // Версию называет то, что только что записано: у сохранённого она уже другая.
    if (refusedForDate()) return;
    const saved = dirty ? await saveDraft() : view;
    if (!saved) return;
    try { await state.mutateAsync({ id: view.id, seen: saved.version, to: 'posted' }); }
    catch (e) {
      // 409 бывает и про накладную, которую уже провели — сосед либо этот же запрос, чей ответ
      // потерялся. Различает их только свежий вид: «не проведена» про проведённую было бы неправдой.
      const conflict = (e as { response?: { status?: number } })?.response?.status === 409;
      const now = conflict ? await readFresh(view.id).catch(() => null) : null;
      if (now?.state === 'Posted') toast.info('Накладная уже проведена.');
      else toast.apiError(e, 'Накладная не проведена');
    }
  }

  function stopCorrecting() {
    setCorrecting(false);
    setReason('');
    adopt(fresh);
  }

  /** Записать исправление: причина, шапка и строки одним телом (D2); версию называет форма. */
  async function writeCorrection() {
    if (refusedForDate()) return;
    const sent = withoutBlanks(draft);
    if (sent !== draft) setDraft(sent);
    try {
      const saved = await correct.mutateAsync({
        id: view.id, seen: view.version, reason: reason.trim(), header: headerPayload(sent), lines: linesPayload(sent),
      });
      setCorrecting(false);
      setReason('');
      adopt(saved);
      toast.success(`Исправление записано: прежнее содержимое — версия ${(saved.currentVersion ?? 2) - 1} в истории.`);
    } catch (e) { toast.apiError(e, 'Исправление не записано'); }
  }

  async function backToDraft() {
    try { await state.mutateAsync({ id: view.id, seen: view.version, to: 'draft' }); }
    catch (e) { toast.apiError(e, 'Накладная не возвращена в черновик'); }
  }

  /**
   * Сопоставить строку проведённой накладной. Запросы идут ПО ОЧЕРЕДИ, и следующий называет версию из
   * ответа предыдущего: каждое сопоставление сдвигает версию накладной, и выбранная следом позиция в
   * соседней строке иначе уходила бы с прежней версией и получала 409 «тем временем изменили» — на
   * собственное действие человека (ревью PR #1296). Блокировать пикеры на время запроса незачем:
   * строки сопоставляют подряд, и ждать ответа после каждой значило бы работать вдвое медленнее.
   */
  function matchLine(line: WaybillLineDraft, nomenclatureId: string | null) {
    if (!line.id) return;
    const lineId = line.id, mine = view.version, queue = matching.current;
    // Очередь пуста — версию называет экран; иначе — ответ того, кто стоит впереди (не удался — экран).
    const queued = queue.waiting > 0;
    queue.waiting++;
    queue.tail = queue.tail.then(async prior => {
      try {
        const saved = await match.mutateAsync({ id: view.id, seen: (queued ? prior : null) ?? mine, lineId, nomenclatureId });
        return saved.version;
      } catch (e) {
        toast.apiError(e, 'Позиция не записана');
        return null;
      } finally { queue.waiting--; }
    });
  }

  function correctionBar() {
    if (!correcting) return null;
    return (
      <WaybillCorrectionBar reason={reason} onReason={setReason} writing={correct.isPending}
        ready={reason.trim() !== '' && dirty && !busy && !outdated}
        siteMoved={draft.construction !== (view.constructionId ?? '')} unneeded={unneeded}
        onCancel={stopCorrecting} onWrite={() => void writeCorrection()} onRelease={stopCorrecting} />
    );
  }

  // Стройку, которой больше нет, в списке не найти — но показать, что она была названа, надо.
  const orphan = draft.construction && !sites.some(s => s.id === draft.construction);
  // Живая неизбранная стройка — не потеря: она стоит группой «Не в избранном» (issue #1219).
  const choices = useSiteChoices(sites, draft.construction);

  return (
    <div className="flex-1 min-h-0 flex flex-col">
      <LeaveGuardDialog open={leave !== null} saving={save.isPending}
        saveBlocked={correcting ? 'Исправление записывается кнопкой «Записать исправление» — с причиной' : undefined}
        onCancel={() => setLeave(null)}
        onDiscard={() => { const go = leave; setLeave(null); go?.(); }}
        onSave={async () => { const go = leave; setLeave(null); if (await saveDraft()) go?.(); }} />
      <div className="shrink-0 border-b border-outline-variant bg-surface px-5 py-3 space-y-2">
        <div className="flex items-center gap-2 flex-wrap">
          <h2 className="text-sm font-medium text-on-surface">
            Накладная № {view.number ?? 'без номера'}
            {view.issuedOn ? ` от ${formatDate(view.issuedOn)}` : ''}
          </h2>
          <span className={`text-xs px-2 py-0.5 rounded-full ${posted
            ? 'bg-success-container text-on-success-container' : 'bg-surface-container text-on-surface-variant'}`}>
            {posted ? 'Проведена' : 'Черновик'}
          </span>
          <div className="flex-1" />
          {!mayEdit && <span className="text-xs text-on-surface-variant">Только чтение: права вводить накладные нет</span>}
          {canEdit && !posted && (
            <>
              <Button size="sm" variant="outlined" icon={<Icon icon={Save} size={18} />} disabled={!dirty || busy}
                loading={save.isPending} onClick={() => void saveDraft()}>
                Сохранить
              </Button>
              <Button size="sm" variant="filled" icon={<Icon icon={CheckCircle} size={18} />} disabled={busy}
                loading={state.isPending} onClick={() => void post()}>
                Провести
              </Button>
            </>
          )}
          {correctable && !correcting && (
            <Button size="sm" variant="outlined" icon={<Icon icon={EditNote} size={18} />} disabled={busy}
              onClick={() => setCorrecting(true)}>
              Исправить
            </Button>
          )}
          {canEdit && posted && !closed && (
            <Button size="sm" variant="outlined" icon={<Icon icon={Undo} size={18} />} disabled={busy}
              loading={state.isPending} onClick={() => void backToDraft()}>
              Вернуть в черновик
            </Button>
          )}
          {/* Удаление идёт мимо исправления: в его режиме меню нет. */}
          {!correcting && <WaybillDeleteMenu view={view} canEdit={canEdit} dirty={dirty} onDeleted={onClose} />}
        </div>
        {gone && (
          <div role="alert" data-testid="waybill-gone-open" className="flex items-start gap-2 rounded-md border
            border-error/30 bg-error-container px-3 py-2 text-xs text-on-error-container">
            <Icon icon={Warning} size={18} className="shrink-0 mt-0.5" />
            <p className="flex-1">
              Этой накладной больше нет — её удалили, пока она была открыта у вас. Набранное осталось на
              экране: перенесите нужное себе. Сохранить его уже некуда.
            </p>
            <Button size="sm" variant="outlined" onClick={onClose}>Закрыть накладную</Button>
          </div>
        )}
        {outdated && !gone && (
          <div role="alert" className="flex items-start gap-2 rounded-md border border-error/30
            bg-error-container px-3 py-2 text-xs text-on-error-container">
            <Icon icon={Warning} size={18} className="shrink-0 mt-0.5" />
            <span className="flex-1">
              Накладную тем временем изменили. Ваши правки на экране целы, но сохранить их поверх нельзя —
              сервер откажет. Перечитайте накладную и внесите правки заново.
            </span>
            <Button size="sm" variant="outlined" icon={<Icon icon={Refresh} size={18} />} onClick={() => adopt(fresh)}>
              Перечитать, отбросив правки
            </Button>
          </div>
        )}
        {note && (
          <p role="status" className="flex items-start gap-2 text-xs text-warning">
            <Icon icon={Warning} size={18} className="shrink-0 mt-0.5" />{note}
          </p>
        )}
        {closed && !correcting && <WaybillLockNote closed={closed} correctable={correctable} />}
        {/* На телефоне полоса исправления уходит в прокрутку (кадр A4): в закреплённой шапке она заняла бы полэкрана. */}
        {wide && correctionBar()}
        {moved && (
          <p className="text-xs text-on-surface-variant" data-testid="waybill-moved">
            Введена после закрытия периода: учтена {formatDate(moved)}, а не датой отпуска.
          </p>
        )}
        {undated && (
          <p className="text-xs text-on-surface-variant" data-testid="waybill-undated">
            Учётная дата не записана — накладная восстановлена из прежней резервной копии. Период для неё
            считается по дате отпуска.
          </p>
        )}
        {posted && canEdit && !closed && (
          <p className="text-xs text-on-surface-variant">
            Проведённую накладную не правят: её строки уже в перечне материалов, отпущенных на стройку.
            Чтобы исправить — верните в черновик. Позицию номенклатуры можно выбрать и так.
          </p>
        )}
      </div>

      <div className="flex-1 min-h-0 overflow-y-auto px-5 py-4 space-y-5">
        {!wide && correctionBar()}
        {/* Шапка — в слое формы, как у счёта (issue #1467): запертая накладная показывает значения
            полями для чтения, а не отключёнными — приглушённое поле читается как «здесь ничего нет». */}
        <section className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
          {/* Обязательны поля, без которых накладную не провести: черновик сохраняется и без них. */}
          <TextField label="Номер" value={draft.number} readOnly={locked} required
            onChange={e => edit({ number: e.target.value })} />
          <DateField label="Дата отпуска" value={draft.issuedOn} readOnly={locked} required
            onChange={issuedOn => edit({ issuedOn })} onIncomplete={setDateProblem} />
          <TextField label="Склад" value={draft.warehouse} readOnly={locked} hint={locked ? undefined : 'Как в накладной'}
            onChange={e => edit({ warehouse: e.target.value })} />
          {locked
            ? <TextField label="Получатель — стройка" readOnly onChange={() => {}}
                value={sites.find(s => s.id === draft.construction)?.name
                  ?? (draft.construction ? view.constructionName ?? MISSING.construction : '')} />
            : (
              <Select label="Получатель — стройка" value={draft.construction} required
                emptyItem="не выбрана" stayOpenOn={REVEAL_CHOICE}
                error={view.constructionLost && orphan ? 'Стройки больше нет — выберите заново'
                  : sitesFailed ? 'Список строек не пришёл: это отказ чтения, а не пустой список' : undefined}
                onValueChange={choice => pickSite(choice, choices.reveal, construction => edit({ construction }))}>
                {orphan && <SelectItem value={draft.construction}>{view.constructionName ?? MISSING.construction}</SelectItem>}
                <SiteSelectItems choices={choices} />
                <RevealItem hidden={choices.hidden} />
              </Select>
            )}
          <TextField label="Получил" value={draft.receivedBy} readOnly={locked} hint={locked ? undefined : 'Кто принял на объекте'}
            onChange={e => edit({ receivedBy: e.target.value })} />
          <TextField label="Примечание" value={draft.note} readOnly={locked}
            onChange={e => edit({ note: e.target.value })} />
        </section>

        <section className="space-y-2">
          <div className="flex items-center gap-3">
            <h3 className="text-sm font-medium text-on-surface-variant">Строки накладной</h3>
            <span className="text-xs text-on-surface-variant">Денег здесь нет: сколько материал стоил, знает счёт.</span>
          </div>

          <EditGrid label="Строки накладной" numbered rows={draft.lines} rowKey={line => line.key}
            empty="Строк нет. Без строк накладную не провести."
            add={locked ? undefined : { label: 'Добавить строку', onAdd: () => edit({ lines: [...draft.lines, emptyLine()] }) }}
            onRemove={locked ? undefined : line => edit({ lines: draft.lines.filter(l => l.key !== line.key) })}
            columns={[
              {
                key: 'nomenclature', label: 'Позиция номенклатуры', width: 'w-[30%]',
                cell: (line, index) => canEdit && (!closed || correcting) ? (
                  <NomenclaturePicker chosen={line.nomenclatureId !== null} chosenId={line.nomenclatureId}
                    name={line.nomenclatureName}
                    neighbours={() => neighboursOf(draft.lines, index, l => (l.nomenclatureId && !l.nomenclatureLost && !l.nomenclatureArchived
                      ? { name: l.sourceText, positionId: l.nomenclatureId, positionName: l.nomenclatureName } : null))}
                    lost={line.nomenclatureLost} archived={line.nomenclatureArchived}
                    from={{ name: line.sourceText, unit: line.unit }}
                    line={{ where: waybillLineWhere(index + 1), quantity: line.quantity }}
                    onPick={(id, name) => posted && !correcting
                      ? void matchLine(line, id)
                      : editLine(line.key, { nomenclatureId: id, nomenclatureName: name, nomenclatureLost: false, nomenclatureArchived: false })}
                    onClear={() => posted && !correcting
                      ? void matchLine(line, null)
                      : editLine(line.key, { nomenclatureId: null, nomenclatureName: null, nomenclatureLost: false, nomenclatureArchived: false })} />
                ) : (
                  <span className={line.nomenclatureId ? 'text-on-surface' : 'text-warning'}>
                    {line.nomenclatureLost ? LOST.position
                      : line.nomenclatureId
                        ? withArchiveWord(line.nomenclatureName ?? 'позиция без названия', !!line.nomenclatureArchived)
                        : 'не сопоставлена'}
                  </span>
                ),
              },
              {
                key: 'sourceText', label: 'Наименование в накладной',
                cell: (line, index) => (
                  <input className={cell} value={line.sourceText} disabled={locked}
                    aria-label={`Наименование в накладной, строка ${index + 1}`}
                    onChange={e => editLine(line.key, { sourceText: e.target.value })} />
                ),
              },
              {
                key: 'unit', label: 'Ед.', width: 'w-20',
                cell: (line, index) => (
                  <input className={cell} value={line.unit} disabled={locked}
                    aria-label={`Единица, строка ${index + 1}`}
                    onChange={e => editLine(line.key, { unit: e.target.value })} />
                ),
              },
              {
                key: 'quantity', label: 'Количество', width: 'w-28', align: 'right',
                cell: (line, index) => (
                  <NumberInput value={line.quantity} disabled={locked}
                    label={`Количество, строка ${index + 1}`}
                    onChange={quantity => editLine(line.key, { quantity })} />
                ),
              },
              {
                // Примечание строки на виду (решение владельца 26.10.2026, D10): исправление правит только видимое.
                key: 'note', label: 'Примечание',
                cell: (line, index) => (
                  <input className={cell} value={line.note} disabled={locked}
                    aria-label={`Примечание, строка ${index + 1}`}
                    onChange={e => editLine(line.key, { note: e.target.value })} />
                ),
              },
            ]} />
        </section>

        <WaybillHistory view={view} gone={gone} />
      </div>
    </div>
  );
}
