import { useState, type ReactNode } from 'react';

import { useLeaveGuard } from '@/shared/ui/NavigationGuard';
import { LeaveGuardDialog } from '@/shared/ui/LeaveGuardDialog';

/**
 * Уход из формы счёта с несохранённым (G4, issue #1097; строки — #1653, `costs-invoice-lines-leave-guard`).
 *
 * <p><b>Охрана держит то же, что и очередь записи</b> (D1): реквизиты, строки, исправление. Прежде она видела
 * только реквизиты, и уход молча уносил набранные строки.</p>
 *
 * <p><b>Вопрос называет, что не сохранено, и не обещает лишнего</b> (D2, решение владельца 08.11.2026 — «(а)
 * Сохранить и строки»): «Сохранить и перейти» пишет реквизиты, затем строки — той же очередью, что кнопка
 * «Сохранить» сводки; отказ любой записи отменяет уход. Исправление этой кнопкой не пишется вовсе (у него
 * одна запись — «Проверить исправление…»), поэтому в исправлении она недоступна, с причиной.</p>
 */

/** Что в форме не сохранено — для вопроса ухода. */
export interface InvoiceUnsaved {
  header: boolean;
  lines: boolean;
  /** Форма в режиме исправления: всё набранное — черновик исправления, обычной записи у него нет. */
  correcting: boolean;
}

/** Почему «Сохранить и перейти» в исправлении недоступна — подсказка у кнопки. */
export const CORRECTION_SAVE_BLOCKED = 'Исправление записывается кнопкой «Проверить исправление…» — после проверки';

/** Текст вопроса: что именно не сохранено и что сделает «Сохранить и перейти». Чистая — её проверяют тесты. */
export function leaveQuestion({ header, lines, correcting }: InvoiceUnsaved): string {
  if (correcting)
    return 'Исправление не записано: оно записывается только кнопкой «Проверить исправление…». Уйдя, вы потеряете набранное в нём.';
  if (header && lines)
    return 'Не сохранены реквизиты и строки счёта. Сохранить их перед переходом? Сначала запишутся реквизиты, затем строки.';
  if (lines) return 'Не сохранены строки счёта. Сохранить их перед переходом?';
  return 'Не сохранены реквизиты счёта. Сохранить их перед переходом?';
}

/**
 * Охрана ухода формы счёта и её вопрос. Возвращает диалог — форма рисует его у себя.
 *
 * @param held есть ли что терять — охрана молчит, когда нечего (и когда счёт удалён: сохранять некуда).
 * @param save очередь записи формы: `true` — записано всё, можно уходить.
 */
export function useInvoiceLeave({ held, unsaved, saving, save }: {
  held: boolean;
  unsaved: InvoiceUnsaved;
  saving: boolean;
  save: () => Promise<boolean>;
}): ReactNode {
  const [leave, setLeave] = useState<(() => void) | null>(null);
  useLeaveGuard(held, proceed => setLeave(() => proceed));
  return (
    <LeaveGuardDialog open={leave !== null} saving={saving}
      message={leaveQuestion(unsaved)}
      saveBlocked={unsaved.correcting ? CORRECTION_SAVE_BLOCKED : undefined}
      onCancel={() => setLeave(null)}
      onDiscard={() => { const go = leave; setLeave(null); go?.(); }}
      onSave={async () => {
        const go = leave;
        setLeave(null);
        // Отказ записи уход отменяет: правки остаются на экране, причину назвал отказ.
        if (await save()) go?.();
      }} />
  );
}
