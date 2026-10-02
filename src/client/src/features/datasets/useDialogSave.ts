import { useState } from 'react';
import { apiError } from '@/shared/utils/apiError';

/**
 * Сохранение из диалога обработки источника (отбор, вычисляемые колонки, сортировка).
 *
 * Сервер на такое сохранение может ответить отказом (issue #1137): отбор проверяется на входе, а
 * уезжает обработка целиком — так что отказ по отбору может прийти и диалогу сортировки, если отбор
 * в его копии источника разошёлся с базой. Диалог, закрывшийся сразу после нажатия, показал бы
 * такой отказ успехом. Поэтому: ответ ждём, отказ показываем в диалоге, закрываемся только при успехе.
 *
 * Пока запрос в полёте, диалог не закрывается ничем — ни «Отменой», ни Esc, ни щелчком по подложке
 * (`close`): закрытому диалогу отказ показать негде, а «Отмена» после нажатого «Сохранить» отменить
 * уже ничего не может.
 */
export function useDialogSave(onClose: () => void, fallback: string) {
  const [saving, setSaving] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);

  async function commit(save: () => void | Promise<unknown>) {
    setSaving(true);
    setRefusal(null);
    try {
      await save();
      onClose();
    } catch (e) {
      setRefusal(apiError(e, fallback));
      setSaving(false);
    }
  }

  return {
    saving,
    refusal,
    /** Отказ — про то, что отправляли: правка содержимого диалога его гасит. */
    clearRefusal: () => setRefusal(null),
    commit,
    close: () => { if (!saving) onClose(); },
  };
}
