import type { AccessInfo } from '@/shared/api/access';
import type { RecentRecord } from '@/shared/api/recentRecords';
import type { IconDef } from '@/shared/ui/icons/types';
import { settingsNav, workNav } from '@/shared/ui/navConfig';
import { RECORD_ROUTES, recordLink, recordScreen } from '@/shared/ui/recordRoutes';

/** Куда ведёт строка «Недавних» и каким знаком она помечена. */
export interface RecentTarget {
  to: string;
  /** Знак раздела записи — тот же, что у пункта меню её экрана. */
  icon: IconDef;
}

/**
 * Строка «Недавних» — ссылка на экран записи, открытый этому человеку, — либо null: экрана у типа нет или
 * он закрыт. Ссылку, по которой придёт отказ, не обещаем, как и у строки таблицы (`recordRoutes.ts`).
 */
export function recentTarget(item: RecentRecord, access: AccessInfo): RecentTarget | null {
  const link = recordLink(recordScreen(item.recordType, access), item.id);
  if (!link) return null;
  const screen = RECORD_ROUTES[item.recordType].screen;
  const icon = [...workNav, ...settingsNav].find(i => i.to === screen)?.icon;
  return icon ? { to: link.to, icon } : null;
}
