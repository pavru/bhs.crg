import type { WorkTypeChoice } from '@/shared/api/planningMatches';
import type { PriceListWork } from '@/shared/api/planningPriceList';

export const CATALOG_ACCESS = 'Для выбора нового вида работы нужно право «Читать справочники» (core.catalog.read).';

export function pricedChoice(work: PriceListWork): WorkTypeChoice {
  return { id: work.workTypeId, code: work.code, name: work.name ?? 'Вид работы недоступен', unitId: work.unitId, unitName: work.unit };
}

export function workLabel(work: WorkTypeChoice) {
  return work.code && !work.name.startsWith(work.code) ? `${work.code} · ${work.name}` : work.name;
}

export function hasWorkUnit(work: WorkTypeChoice) {
  return Boolean(work.unitId && work.unitName);
}
