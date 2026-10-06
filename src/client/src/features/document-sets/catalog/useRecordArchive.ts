import { useSetCommonDataArchive } from '@/shared/api/commonData';
import type { DocumentType } from '@/shared/api/types';
import { useToast } from '@/shared/ui/Toast';

/** Код типа «Сотрудник» в ядре (зеркало серверного `CoreRecordTypes.EmployeeCode`). */
const EMPLOYEE_TYPE_CODE = 'Сотрудник';

type Target = { id: string; displayName: string; compositeTypeId: string; archived?: boolean };

/**
 * Действие «в архив» / «вернуть из архива» у записи справочника (issue #1185).
 *
 * Подтверждения нет: действие обратимо, а запись остаётся на той же странице — в «В архиве: N».
 * Отмена — кнопкой «Вернуть» в тосте.
 */
export function useRecordArchive(types: DocumentType[]) {
  const mutation = useSetCommonDataArchive();
  const toast = useToast();

  const typeOf = (entry: Target) => types.find(t => t.id === entry.compositeTypeId);

  /**
   * Сменить состояние; отказ летит наружу — его показывает тот, кто звал (диалог удаления).
   */
  async function set(entry: Target, archived: boolean): Promise<void> {
    await mutation.mutateAsync({ id: entry.id, archived });
    if (!archived) {
      toast.success(`Запись «${entry.displayName}» возвращена из архива.`);
      return;
    }
    // Сотрудник: архив — не увольнение. Оговорка нужна ровно здесь: человек, убравший сотрудника из
    // выбора, иначе решит, что заодно закрыл ему период работы.
    const tail = typeOf(entry)?.code === EMPLOYEE_TYPE_CODE
      ? 'Дата «Уволен с» не менялась.'
      : 'В списках выбора её больше не будет.';
    toast.success(`Запись «${entry.displayName}» — в архиве. ${tail}`, {
      duration: 8000,
      action: { label: 'Вернуть', onClick: () => void act(entry, false) },
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
    isEmployee: (entry: Target) => typeOf(entry)?.code === EMPLOYEE_TYPE_CODE,
  };
}
