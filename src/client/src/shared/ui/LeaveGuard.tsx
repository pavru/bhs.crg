import { useEffect, useState } from 'react';

import { useLeaveGuard } from './NavigationGuard';
import { LeaveGuardDialog } from './LeaveGuardDialog';

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
 */
export function LeaveGuard({ active, save, saving = false, message, saveBlocked }: {
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
}) {
  const [leave, setLeave] = useState<(() => void) | null>(null);
  const [writing, setWriting] = useState(false);
  useLeaveGuard(active, proceed => setLeave(() => proceed));

  // Терять нечего, а вопрос открыт: закрыть его — поправкой состояния при отрисовке, а сам уход — эффектом (это
  // уже действие наружу). Свой уход у каждого вопроса свой (`proceed` охраны — новое замыкание), и эффект
  // срабатывает на каждый ровно раз.
  const [settled, setSettled] = useState<(() => void) | null>(null);
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

  return (
    <LeaveGuardDialog open={leave !== null} saving={writing || saving} message={message} saveBlocked={saveBlocked}
      onSave={save && (() => void saveAndGo(save))}
      onCancel={() => setLeave(null)}
      onDiscard={() => { const go = leave; setLeave(null); go?.(); }} />
  );
}
