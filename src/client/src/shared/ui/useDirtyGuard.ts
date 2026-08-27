import { useState } from 'react';

/**
 * Отдельным файлом от `ListDetailShell` (issue #858): модуль, экспортирующий и компонент, и хук,
 * теряет горячую перезагрузку — правка компонента перезагружает страницу целиком.
 *
 * Гард несохранённых изменений при смене выбранного элемента. Generic по ключу выбора (`string` у типов
 * документов, `{mode,id}` у типов полей). Возвращает `request(next)` для перехвата выбора и `dialogProps`
 * для `LeaveGuardDialog`. `onCommit` применяет переход (страница владеет своим selectedKey).
 */
export function useDirtyGuard<TKey>({ isDirty, saving, saveAll, onCommit }: {
  isDirty: boolean; saving: boolean; saveAll: () => Promise<void>; onCommit: (next: TKey) => void;
}) {
  const [pending, setPending] = useState<{ next: TKey } | null>(null);
  const request = (next: TKey) => { if (isDirty) setPending({ next }); else onCommit(next); };
  const dialogProps = {
    open: pending !== null,
    saving,
    onCancel: () => setPending(null),
    onDiscard: () => { if (pending) onCommit(pending.next); setPending(null); },
    onSave: async () => {
      // При ошибке валидации/сохранения закрываем диалог, чтобы ошибка формы стала видна
      // (иначе «Сохранить и перейти» молча висит и выглядит сломанным). Переход отменяется.
      try { await saveAll(); if (pending) onCommit(pending.next); }
      catch { /* ошибка уже показана в форме (setError) */ }
      finally { setPending(null); }
    },
  };
  return { request, dialogProps };
}
