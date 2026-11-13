import type { AccessInfo } from '@/shared/api/access';
import type { RecentRecord } from '@/shared/api/recentRecords';
import type { IconDef } from '@/shared/ui/icons';
import { RECORD_GROUPS } from '@/shared/ui/paletteRecords';
import { RECORD_ROUTES, hitRoute, openTo, recordHref } from '@/shared/ui/recordRoutes';

/** Куда ведёт строка «Недавних» и каким знаком она помечена. */
export interface RecentTarget {
  to: string;
  /** Знак записи — тот же, что у её группы в палитре: что это за запись, а не где она лежит. */
  icon: IconDef;
}

/**
 * Строка «Недавних» — ссылка на экран записи, открытый этому человеку, — либо null: экрана у типа нет или
 * он закрыт. Ссылку, по которой придёт отказ, не обещаем, как и у строки таблицы (`recordRoutes.ts`).
 *
 * Экран — как у найденной записи палитры (`hitRoute`): у набора данных он свой на каждом уровне и закрыт
 * правом СВОЕГО раздела — набор стройки открывается правом строек, а не правом «Наборов данных».
 */
export function recentTarget(item: RecentRecord, access: AccessInfo): RecentTarget | null {
  const group = RECORD_GROUPS.find(g => g.recordType === item.recordType);
  // Свой ключ, а не унаследованный: код типа приходит с сервера (как в `recordScreen`).
  const type = Object.hasOwn(RECORD_ROUTES, item.recordType) ? RECORD_ROUTES[item.recordType] : undefined;
  if (!group || !type) return null;
  // Экран под другой записью (комплект, шаблон): адрес целиком из карточки; без `opens` его не построить, и
  // адрес без стройки вёл бы на чужую страницу — строки нет (shell-09, Р-4).
  if (type.href) {
    return item.opens && openTo(type, access) ? { to: type.href(item.id, item.opens), icon: group.icon } : null;
  }
  const route = type.at ? hitRoute(type, item.level, access) : openTo(type, access);
  return route ? { to: recordHref(route, item.id), icon: group.icon } : null;
}

