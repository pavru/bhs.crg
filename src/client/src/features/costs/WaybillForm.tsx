import { LOST, MISSING } from './lostReferences';
import { useEffect, useState } from 'react';
import { CircleCheck, Plus, RefreshCw, Save, Trash2, TriangleAlert, Undo2 } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { LeaveGuardDialog } from '@/shared/ui/LeaveGuardDialog';
import { useLeaveGuard } from '@/shared/ui/NavigationGuard';
import { useToast } from '@/shared/ui/Toast';
import type { CostsConstruction } from '@/shared/api/invoices';
import {
  useMatchWaybillLine, useSaveWaybill, useWaybillState, type WaybillView,
} from '@/shared/api/waybills';
import { NomenclaturePicker } from './NomenclaturePicker';
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
 * устаревшей формы сервер отвергает по версии (<c>ifMatch</c>). Уход со страницы и выбор другой
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
  const toast = useToast();

  const posted = view.state === 'Posted';
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
  const note = unmatchedNote(unmatched, posted);

  const edit = (patch: Partial<WaybillDraft>) => setDraft(d => ({ ...d, ...patch }));
  const editLine = (key: string, patch: Partial<WaybillLineDraft>) =>
    setDraft(d => ({ ...d, lines: d.lines.map(l => l.key === key ? { ...l, ...patch } : l) }));

  async function saveDraft(): Promise<boolean> {
    // Пустые заготовки уходят и с экрана: номер строки в отказе сервера обязан совпасть с номером здесь.
    const sent = withoutBlanks(draft);
    if (sent !== draft) setDraft(sent);
    try {
      adopt(await save.mutateAsync({
        id: view.id, ifMatch: view.version, header: headerPayload(sent), lines: linesPayload(sent),
      }));
      return true;
    } catch (e) { toast.apiError(e, 'Накладная не сохранена'); return false; }
  }

  async function post() {
    // Проводится то, что на экране: несохранённая правка, оставшаяся в форме, иначе пропала бы молча,
    // а проведённым оказалось бы прежнее состояние.
    if (dirty && !(await saveDraft())) return;
    try { await state.mutateAsync({ id: view.id, to: 'posted' }); }
    catch (e) { toast.apiError(e, 'Накладная не проведена'); }
  }

  async function backToDraft() {
    try { await state.mutateAsync({ id: view.id, to: 'draft' }); }
    catch (e) { toast.apiError(e, 'Накладная не возвращена в черновик'); }
  }

  async function matchLine(line: WaybillLineDraft, nomenclatureId: string | null) {
    if (!line.id) return;
    try { await match.mutateAsync({ id: view.id, lineId: line.id, nomenclatureId }); }
    catch (e) { toast.apiError(e, 'Позиция не записана'); }
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
          {canEdit && posted && (
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
        {posted && canEdit && (
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
                        {canEdit ? (
                          <NomenclaturePicker chosen={line.nomenclatureId !== null} name={line.nomenclatureName}
                            lost={line.nomenclatureLost}
                            onPick={(id, name) => posted
                              ? void matchLine(line, id)
                              : editLine(line.key, { nomenclatureId: id, nomenclatureName: name, nomenclatureLost: false })}
                            onClear={() => posted
                              ? void matchLine(line, null)
                              : editLine(line.key, { nomenclatureId: null, nomenclatureName: null, nomenclatureLost: false })} />
                        ) : (
                          <span className={line.nomenclatureId ? 'text-fg' : 'text-warning'}>
                            {line.nomenclatureLost ? LOST.position
                              : line.nomenclatureId ? line.nomenclatureName ?? 'позиция без названия' : 'не сопоставлена'}
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
