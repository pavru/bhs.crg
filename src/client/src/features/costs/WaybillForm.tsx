import { LOST, MISSING } from './lostReferences';
import { useEffect, useRef, useState } from 'react';
import { CircleCheck, Lock, Plus, RefreshCw, Save, Trash2, TriangleAlert, Undo2 } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { LeaveGuardDialog } from '@/shared/ui/LeaveGuardDialog';
import { useLeaveGuard } from '@/shared/ui/NavigationGuard';
import { useToast } from '@/shared/ui/Toast';
import type { CostsConstruction } from '@/shared/api/invoices';
import {
  useFreshWaybill, useMatchWaybillLine, useSaveWaybill, useWaybillState, type WaybillView,
} from '@/shared/api/waybills';
import { NomenclaturePicker } from './NomenclaturePicker';
import { withArchiveWord } from '@/shared/ui/archive';
import { formatDate } from '@/shared/format/format';
import { NumberInput } from '@/shared/ui/NumberInput';
import {
  emptyLine, headerPayload, isDirty, linesPayload, toDraft, unmatchedCount, unmatchedNote, withoutBlanks,
  type WaybillDraft, type WaybillLineDraft,
} from './waybills';

const input = 'w-full rounded border border-stroke bg-surface px-2 py-1.5 text-sm text-fg outline-none '
  + 'focus:border-primary placeholder:text-fg4 disabled:bg-surface2 disabled:text-fg3';
const cell = 'w-full rounded border border-stroke bg-surface px-1.5 py-1 text-xs text-fg outline-none '
  + 'focus:border-primary placeholder:text-fg4 disabled:bg-surface2 disabled:text-fg3';

/**
 * Форма расходной накладной (задача D1 этапа 2, issue #1083; ТЗ COST-5, COST-17).
 *
 * <p><b>Черновик правят целиком, проведённую — нет.</b> Строки проведённой накладной уже лежат в
 * перечне отпущенного на стройку, и тихая правка количества меняла бы его без следа. Исправить —
 * вернуть в черновик, поправить, провести снова.</p>
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
export function WaybillForm({ view: fresh, sites, sitesFailed, canEdit, onLeaveGuard }: {
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
}) {
  // Накладная, по которой собрана форма: с ней сверяется «изменено ли» и её версию называет сохранение.
  const [view, setView] = useState(fresh);
  const [draft, setDraft] = useState<WaybillDraft>(() => toDraft(fresh));
  const adopt = (next: WaybillView) => { setView(next); setDraft(toDraft(next)); };

  const editable = canEdit && view.state !== 'Posted';
  // Свежий ответ берём, только когда терять нечего. Идентификаторы новых строк и названия позиций
  // после своего сохранения приходят этим же путём.
  if (fresh !== view && !(editable && isDirty(draft, view))) adopt(fresh);
  const outdated = fresh !== view && fresh.version !== view.version;

  const save = useSaveWaybill();
  const state = useWaybillState();
  const match = useMatchWaybillLine();
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

  const [leave, setLeave] = useState<(() => void) | null>(null);
  useLeaveGuard(dirty, proceed => setLeave(() => proceed));
  useEffect(() => {
    onLeaveGuard(dirty ? proceed => setLeave(() => proceed) : null);
    return () => onLeaveGuard(null);
  }, [onLeaveGuard, dirty]);
  const busy = save.isPending || state.isPending || match.isPending;
  const unmatched = dirty ? unmatchedCount(draft) : view.totals.unmatched;
  const note = unmatchedNote(unmatched, posted, closed !== null);

  const edit = (patch: Partial<WaybillDraft>) => setDraft(d => ({ ...d, ...patch }));
  const editLine = (key: string, patch: Partial<WaybillLineDraft>) =>
    setDraft(d => ({ ...d, lines: d.lines.map(l => l.key === key ? { ...l, ...patch } : l) }));

  /** Сохранить черновик; возвращает записанное — следующему действию нужна его версия — либо null. */
  async function saveDraft(): Promise<WaybillView | null> {
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

  // Стройку, которой больше нет, в списке не найти — но показать, что она была названа, надо.
  const orphan = draft.construction && !sites.some(s => s.id === draft.construction);

  return (
    <div className="flex-1 min-h-0 flex flex-col">
      <LeaveGuardDialog open={leave !== null} saving={save.isPending}
        onCancel={() => setLeave(null)}
        onDiscard={() => { const go = leave; setLeave(null); go?.(); }}
        onSave={async () => { const go = leave; setLeave(null); if (await saveDraft()) go?.(); }} />
      <div className="shrink-0 border-b border-stroke bg-surface px-5 py-3 space-y-2">
        <div className="flex items-center gap-2 flex-wrap">
          <h2 className="text-sm font-medium text-fg1">
            Накладная № {view.number ?? 'без номера'}
            {view.issuedOn ? ` от ${formatDate(view.issuedOn)}` : ''}
          </h2>
          <span className={`text-xs px-2 py-0.5 rounded-full ${posted
            ? 'bg-success-subtle text-success' : 'bg-surface2 text-fg2'}`}>
            {posted ? 'Проведена' : 'Черновик'}
          </span>
          <div className="flex-1" />
          {!canEdit && <span className="text-xs text-fg3">Только чтение: права вводить накладные нет</span>}
          {canEdit && !posted && (
            <>
              <Button size="sm" variant="outlined" icon={<Save size={13} />} disabled={!dirty || busy}
                loading={save.isPending} onClick={() => void saveDraft()}>
                Сохранить
              </Button>
              <Button size="sm" variant="filled" icon={<CircleCheck size={13} />} disabled={busy}
                loading={state.isPending} onClick={() => void post()}>
                Провести
              </Button>
            </>
          )}
          {canEdit && posted && !closed && (
            <Button size="sm" variant="outlined" icon={<Undo2 size={13} />} disabled={busy}
              loading={state.isPending} onClick={() => void backToDraft()}>
              Вернуть в черновик
            </Button>
          )}
        </div>
        {outdated && (
          <div role="alert" className="flex items-start gap-2 rounded-lg border border-danger-border
            bg-danger-subtle px-3 py-2 text-xs text-danger">
            <TriangleAlert size={13} className="shrink-0 mt-0.5" />
            <span className="flex-1">
              Накладную тем временем изменили. Ваши правки на экране целы, но сохранить их поверх нельзя —
              сервер откажет. Перечитайте накладную и внесите правки заново.
            </span>
            <Button size="sm" variant="outlined" icon={<RefreshCw size={13} />} onClick={() => adopt(fresh)}>
              Перечитать, отбросив правки
            </Button>
          </div>
        )}
        {note && (
          <p role="status" className="flex items-start gap-2 text-xs text-warning">
            <TriangleAlert size={13} className="shrink-0 mt-0.5" />{note}
          </p>
        )}
        {closed && (
          <p role="status" className="flex items-start gap-2 text-xs text-fg2" data-testid="waybill-locked">
            <Lock size={13} className="shrink-0 mt-0.5 text-fg3" />
            <span>
              Заперта: {closed}. Вернуть в черновик и сопоставить строки нельзя — её материалы вошли в
              закрытый период. Чтобы исправить, сначала отмените закрытие периода.
            </span>
          </p>
        )}
        {moved && (
          <p className="text-xs text-fg3" data-testid="waybill-moved">
            Введена после закрытия периода: учтена {formatDate(moved)}, а не датой отпуска.
          </p>
        )}
        {undated && (
          <p className="text-xs text-fg3" data-testid="waybill-undated">
            Учётная дата не записана — накладная восстановлена из прежней резервной копии. Период для неё
            считается по дате отпуска.
          </p>
        )}
        {posted && canEdit && !closed && (
          <p className="text-xs text-fg4">
            Проведённую накладную не правят: её строки уже в перечне материалов, отпущенных на стройку.
            Чтобы исправить — верните в черновик. Позицию номенклатуры можно выбрать и так.
          </p>
        )}
      </div>

      <div className="flex-1 min-h-0 overflow-y-auto px-5 py-4 space-y-5">
        <section className="grid grid-cols-1 md:grid-cols-3 gap-3">
          <Field label="Номер">
            <input className={input} value={draft.number} disabled={locked}
              onChange={e => edit({ number: e.target.value })} />
          </Field>
          <Field label="Дата отпуска">
            <input type="date" className={input} value={draft.issuedOn} disabled={locked}
              onChange={e => edit({ issuedOn: e.target.value })} />
          </Field>
          <Field label="Склад">
            <input className={input} value={draft.warehouse} disabled={locked}
              placeholder="Как в накладной" onChange={e => edit({ warehouse: e.target.value })} />
          </Field>
          <Field label="Получатель — стройка"
            problem={view.constructionLost && orphan ? 'Стройки больше нет — выберите заново'
              : sitesFailed ? 'Список строек не пришёл: это отказ чтения, а не пустой список' : undefined}>
            <select className={input} value={draft.construction} disabled={locked}
              aria-label="Получатель — стройка" onChange={e => edit({ construction: e.target.value })}>
              <option value="">не выбрана</option>
              {orphan && <option value={draft.construction}>{view.constructionName ?? MISSING.construction}</option>}
              {sites.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
            </select>
          </Field>
          <Field label="Получил">
            <input className={input} value={draft.receivedBy} disabled={locked}
              placeholder="Кто принял на объекте" onChange={e => edit({ receivedBy: e.target.value })} />
          </Field>
          <Field label="Примечание">
            <input className={input} value={draft.note} disabled={locked}
              onChange={e => edit({ note: e.target.value })} />
          </Field>
        </section>

        <section className="space-y-2">
          <div className="flex items-center gap-3">
            <h3 className="text-sm font-medium text-fg2">Строки накладной</h3>
            <span className="text-xs text-fg4">Денег здесь нет: сколько материал стоил, знает счёт.</span>
            <div className="flex-1" />
            {!locked && (
              <Button size="sm" variant="outlined" icon={<Plus size={13} />}
                onClick={() => edit({ lines: [...draft.lines, emptyLine()] })}>
                Строка
              </Button>
            )}
          </div>

          {draft.lines.length === 0
            ? <p className="text-xs text-fg4">Строк нет. Без строк накладную не провести.</p>
            : (
              <table className="w-full text-xs">
                <thead>
                  <tr className="text-left text-fg4">
                    <th className="w-8 py-1 font-normal">№</th>
                    <th className="w-[30%] py-1 font-normal">Позиция номенклатуры</th>
                    <th className="py-1 font-normal">Наименование в накладной</th>
                    <th className="w-20 py-1 font-normal">Ед.</th>
                    <th className="w-28 py-1 font-normal text-right pr-2">Количество</th>
                    <th className="w-8" />
                  </tr>
                </thead>
                <tbody>
                  {draft.lines.map((line, index) => (
                    <tr key={line.key} className="border-t border-stroke align-top">
                      <td className="py-1 text-fg4">{index + 1}</td>
                      <td className="py-1 pr-2">
                        {canEdit && !closed ? (
                          <NomenclaturePicker chosen={line.nomenclatureId !== null} name={line.nomenclatureName}
                            lost={line.nomenclatureLost} archived={line.nomenclatureArchived}
                            from={{ name: line.sourceText, unit: line.unit }}
                            onPick={(id, name) => posted
                              ? void matchLine(line, id)
                              : editLine(line.key, { nomenclatureId: id, nomenclatureName: name, nomenclatureLost: false, nomenclatureArchived: false })}
                            onClear={() => posted
                              ? void matchLine(line, null)
                              : editLine(line.key, { nomenclatureId: null, nomenclatureName: null, nomenclatureLost: false, nomenclatureArchived: false })} />
                        ) : (
                          <span className={line.nomenclatureId ? 'text-fg' : 'text-warning'}>
                            {line.nomenclatureLost ? LOST.position
                              : line.nomenclatureId
                                ? withArchiveWord(line.nomenclatureName ?? 'позиция без названия', !!line.nomenclatureArchived)
                                : 'не сопоставлена'}
                          </span>
                        )}
                      </td>
                      <td className="py-1 pr-2">
                        <input className={cell} value={line.sourceText} disabled={locked}
                          aria-label={`Наименование в накладной, строка ${index + 1}`}
                          onChange={e => editLine(line.key, { sourceText: e.target.value })} />
                      </td>
                      <td className="py-1 pr-2">
                        <input className={cell} value={line.unit} disabled={locked}
                          aria-label={`Единица, строка ${index + 1}`}
                          onChange={e => editLine(line.key, { unit: e.target.value })} />
                      </td>
                      <td className="py-1 pr-2">
                        <NumberInput value={line.quantity} disabled={locked}
                          label={`Количество, строка ${index + 1}`}
                          onChange={quantity => editLine(line.key, { quantity })} />
                      </td>
                      <td className="py-1">
                        {!locked && (
                          <button type="button" title="Убрать строку" aria-label={`Убрать строку ${index + 1}`}
                            className="text-fg4 hover:text-danger p-1"
                            onClick={() => edit({ lines: draft.lines.filter(l => l.key !== line.key) })}>
                            <Trash2 size={13} />
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
        </section>
      </div>
    </div>
  );
}

function Field({ label, problem, children }: { label: string; problem?: string; children: React.ReactNode }) {
  return (
    <label className="block space-y-1">
      <span className="text-xs text-fg3">{label}</span>
      {children}
      {problem && <span className="block text-xs text-danger">{problem}</span>}
    </label>
  );
}
