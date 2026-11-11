import { EMPLOYEE_CODE, ownDoorCode, useSetCommonDataArchive } from '@/shared/api/commonData';
import type { DocumentType } from '@/shared/api/types';
import { SNACKBAR_UNDO_MS, useSnackbar } from '@/shared/ui/Snackbar';

/** Версия нужна двери видов работ: в архив вид работы уходит только с ней (WORK-59). */
type Target = { id: string; displayName: string; compositeTypeId: string; archived?: boolean; version?: string };

/**
 * Действие «в архив» / «вернуть из архива» у записи справочника (issue #1185).
 *
 * Подтверждения нет: действие обратимо, а запись остаётся на той же странице — в «В архиве: N».
 * Отмена — кнопкой «Вернуть» в тосте.
 */
export function useRecordArchive(types: DocumentType[]) {
  const mutation = useSetCommonDataArchive();
  const toast = useSnackbar();

  const typeOf = (entry: Target) => types.find(t => t.id === entry.compositeTypeId);
  // Сотрудник — вся семья справочника, с подтипами: их пишет та же дверь (ревью PR #1908).
  const isEmployee = (entry: Target) => ownDoorCode(entry.compositeTypeId, types) === EMPLOYEE_CODE;

  /**
   * Сменить состояние; отказ летит наружу — его показывает тот, кто звал (диалог удаления).
   */
  async function set(entry: Target, archived: boolean): Promise<void> {
    const done = await mutation.mutateAsync({ id: entry.id, archived, doorCode: ownDoorCode(entry.compositeTypeId, types), version: entry.version });
    // Архив сдвигает версию: «Вернуть» из тоста называет уже новую, иначе дверь откажет на своё же действие.
    const after = { ...entry, version: done.version ?? entry.version };
    if (!archived) {
      toast.success(`Запись «${entry.displayName}» возвращена из архива.`);
      return;
    }
    // Сотрудник: архив — не увольнение. Оговорка нужна ровно здесь: человек, убравший сотрудника из
    // выбора, иначе решит, что заодно закрыл ему период работы.
    const tail = isEmployee(entry)
      ? 'Дата «Уволен с» не менялась.'
      : 'В списках выбора её больше не будет.';
    toast.success(`Запись «${entry.displayName}» — в архиве. ${tail}`, {
      duration: SNACKBAR_UNDO_MS,
      action: { label: 'Вернуть', onClick: () => void act(after, false) },
    });
  }

  /** То же, но отказ показывает сам — тостом: для кнопки в строке, у которой своего места нет. */
  async function act(entry: Target, archived: boolean): Promise<void> {
    try { await set(entry, archived); }
    catch (e) { toast.apiError(e, archived ? 'Не удалось отправить в архив.' : 'Не удалось вернуть из архива.'); }
  }

  return {
    set, act, pending: mutation.isPending,
    /**
     * Есть ли у записи действие. Справочник модуля общим путём в архив не уходит (сервер откажет) —
     * кнопку для него не рисуем вовсе: отключённой с объяснением её держать незачем. Вернуть из
     * архива можно любую: снять признак — всегда благо, и сервер на этом пути владельца не спрашивает.
     *
     * Это подсказка экрану, а не правило: решает сервер. Сравнение без учёта регистра и «тип не
     * найден — запись ядра» повторяют его нарочно, чтобы кнопка не обещала отказ.
     */
    allowed: (entry: Target) => {
      const t = typeOf(entry);
      return !!entry.archived || !t || t.module.toLowerCase() === 'core';
    },
    isEmployee,
  };
}
