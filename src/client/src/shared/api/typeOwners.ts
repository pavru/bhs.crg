import type { AccessInfo } from './access';
import type { DocumentType } from './types';

/** Владелец-«не модуль»: тип принадлежит ядру и не исчезает ни при каком наборе модулей. */
export const CORE_OWNER = 'core';

/** Владелец, которого можно назвать: ядро или включённый модуль. */
export interface TypeOwnerOption {
  code: string;
  title: string;
}

/**
 * Кому можно отдать тип на ЭТОМ экземпляре (ТЗ CORE-18). Список тот же, что проверяет сервер:
 * ядро плюс включённые модули. Выключенного модуля в списке нет нарочно — тип, отданный тому,
 * кого здесь нет, исчез бы из редактора тем же действием, каким его отдавали.
 */
export function ownerOptions(access: AccessInfo): TypeOwnerOption[] {
  return [
    { code: CORE_OWNER, title: 'Ядро' },
    ...access.modules.map(m => ({ code: m.code, title: m.title })),
  ];
}

/** Название владельца для человека: «Ядро», «Исполнительная документация», иначе — сам код. */
export function ownerTitle(module: string, access: AccessInfo): string {
  return ownerOptions(access).find(o => o.code === module)?.title ?? module;
}

/**
 * Предлагать ли тип к ВЫБОРУ: типы выключенного модуля не предлагаются (ТЗ TYPE-5).
 *
 * ⚠️ Функция отвечает только на вопрос «предлагать ли НОВЫЙ выбор». Из разрешения уже записанного
 * `typeId` такой тип убирать нельзя никогда: селектор, не нашедший своего значения, покажется
 * пустым — и следующее сохранение схемы запишет `typeId: null`. Между «сохранилось как было» и
 * «записался null» на экране нет никакой разницы, поэтому потеря была бы тихой.
 *
 * ⚠️ `access === undefined` — это «ещё не знаем», и прятать по нему нельзя НИЧЕГО. Первая редакция
 * подставляла сюда пустой доступ, и живой прогон показал цену: на первом кадре список типов
 * оставался без единого типа модуля, страница выбирала «первый в списке» из того, что осталось, и
 * открывался не тот тип. Пустой ответ и неполученный ответ выглядят одинаково только в коде.
 */
export function isOfferedType(type: Pick<DocumentType, 'module'>, access: AccessInfo | undefined): boolean {
  if (!access) return true;
  return type.module === CORE_OWNER || access.modules.some(m => m.code === type.module);
}

/**
 * Вернуть в список выбора УЖЕ ВЫБРАННОЕ значение, если фильтр его выбросил.
 *
 * ⚠️ Без этого пикер, который одним списком и предлагает, и разрешает своё значение
 * (`TypePickerField`), не находит выбранного типа и показывает «— без родителя —» с выключенным
 * крестиком: наследование выглядит потерянным, хотя оно на месте. Найдено ревью PR #1002 — там же,
 * где я оставил комментарий, обещавший обратное.
 */
export function keepingCurrent<T extends { id: string }>(
  offered: T[], all: T[], currentId: string | null | undefined,
): T[] {
  if (!currentId || offered.some(t => t.id === currentId)) return offered;
  const current = all.find(t => t.id === currentId);
  return current ? [...offered, current] : offered;
}

/** Те же правила для списка: удобнее в месте выбора, чем фильтр вручную. */
export function offeredTypes<T extends Pick<DocumentType, 'module'>>(
  types: T[], access: AccessInfo | undefined,
): T[] {
  return access ? types.filter(t => isOfferedType(t, access)) : types;
}
