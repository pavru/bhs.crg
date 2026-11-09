import { useEffect, useRef, useState } from 'react';

import { useLeaveGuard } from './NavigationGuard';
import { LeaveGuardDialog } from './LeaveGuardDialog';
import { ConfirmDialog } from './ConfirmDialog';

/**
 * Охрана ухода со страницы с правками и её вопрос — одним компонентом (ревью PR #1817). Связка «состояние
 * вопроса + `useLeaveGuard` + `LeaveGuardDialog`» жила копиями по формам, и в копиях вопрос закрывался ДО конца
 * записи: форма на время запроса снова принимала правки, а удачная запись уводила со страницы, унося их без
 * вопроса.
 *
 * <p><b>Вопрос стоит, пока идёт запись</b> — своя («Сохранить и перейти») или начатая кнопкой страницы
 * (`saving`): диалог занят, правок под ним нет. Отказ записи уход отменяет: вопрос закрывается, правки на
 * экране, причину назвал отказ.</p>
 *
 * <p><b>Терять стало нечего, пока вопрос открыт</b> (кончилась запись, начатая кнопкой страницы), — уход, о
 * котором человек просил, идёт сам: вопрос висел бы о том, чего уже нет.</p>
 *
 * <p><b>Страница, которая пишет сама</b> (`flush`, форма счёта — costs-invoice-screen-12-autosave, D-12.7), не
 * спрашивает «сохранить ли»: уход сначала дописывает и уходит. Вопрос — только когда запись не дошла: «Запись
 * не дошла — уйти без неё?», «Остаться» — выход, «Уйти без записи» — подтверждение.</p>
 */
export function LeaveGuard({ active, save, saving = false, message, saveBlocked, flush, failedMessage }: {
  /** Есть несохранённое — охрана держит уход, перезагрузку и шаг истории. */
  active: boolean;
  /** Запись перед уходом: `true` — записано всё, можно уходить; `false` — отказ (о нём уже сказано). Нет —
   *  сохранять этим вопросом нечем: у него только «Не сохранять» и «Отмена», записи он не обещает. */
  save?: () => Promise<boolean>;
  /** Запись, начатая помимо вопроса (кнопка «Сохранить» страницы): вопрос ждёт её, как свою. */
  saving?: boolean;
  /** Что именно не сохранено. Нет — общий вопрос. */
  message?: string;
  /** Сохранить сейчас нельзя — почему (картинка ещё едет в хранилище, issue #532). */
  saveBlocked?: string;
  /** Дописать перед уходом (страница пишет сама): `true` — дошло, уход идёт без вопроса. С ним `save` не нужен. */
  flush?: () => Promise<boolean>;
  /** Что сказать, когда запись перед уходом не дошла (`flush`). Нет — общее «уйдя, вы его потеряете». */
  failedMessage?: string;
}) {
  const [leave, setLeave] = useState<(() => void) | null>(null);
  const [writing, setWriting] = useState(false);
  // Уход, ждущий записи, и уход, запись которого не дошла, — у страницы, что пишет сама.
  const [failed, setFailed] = useState<(() => void) | null>(null);
  const [settled, setSettled] = useState<(() => void) | null>(null);
  // Куда уйти после записи. Уход, попросившийся во время неё (вторая ссылка, «Назад»), заменяет ждущий: последний
  // и есть тот, о котором человек просит сейчас, — молча проглоченный, он пропал бы без следа.
  const target = useRef<(() => void) | null>(null);
  async function flushThenGo(write: () => Promise<boolean>, go: () => void) {
    target.current = go;
    setWriting(true);
    let ok: boolean;
    try { ok = await write(); } finally { setWriting(false); }
    const to = target.current;
    target.current = null;
    if (!to) return;
    if (ok) to(); else setFailed(() => to);
  }
  useLeaveGuard(active, proceed => {
    if (!flush) { setLeave(() => proceed); return; }
    if (target.current) { target.current = proceed; return; }
    void flushThenGo(flush, proceed);
  });
  // Запись дошла, пока вопрос открыт (носитель «Повторить»), — уход, о котором просили, идёт сам.
  if (failed && !active) {
    setFailed(null);
    setSettled(() => failed);
  }

  // Терять нечего, а вопрос открыт: закрыть его — поправкой состояния при отрисовке, а сам уход — эффектом (это
  // уже действие наружу). Свой уход у каждого вопроса свой (`proceed` охраны — новое замыкание), и эффект
  // срабатывает на каждый ровно раз.
  if (leave && !active && !writing && !saving) {
    setLeave(null);
    setSettled(() => leave);
  }
  useEffect(() => { settled?.(); }, [settled]);

  async function saveAndGo(write: () => Promise<boolean>) {
    const go = leave;
    setWriting(true);
    let ok: boolean;
    try { ok = await write(); } finally {
      setWriting(false);
      setLeave(null);
    }
    if (ok) go?.();
  }

  if (flush)
    return (
      <ConfirmDialog open={failed !== null} onOpenChange={open => { if (!open) setFailed(null); }}
        title="Запись не дошла — уйти без неё?"
        description={failedMessage ?? 'Набранное не записано: уйдя, вы его потеряете.'}
        confirmLabel="Уйти без записи" cancelLabel="Остаться"
        onConfirm={() => { const go = failed; setFailed(null); go?.(); }} />
    );

  return (
    <LeaveGuardDialog open={leave !== null} saving={writing || saving} message={message} saveBlocked={saveBlocked}
      onSave={save && (() => void saveAndGo(save))}
      onCancel={() => setLeave(null)}
      onDiscard={() => { const go = leave; setLeave(null); go?.(); }} />
  );
}
