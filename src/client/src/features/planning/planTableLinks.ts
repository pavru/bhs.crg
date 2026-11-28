import type { FilterCondition, FilterNode } from '@/shared/api/types';
import { DEFAULT_VIEW, viewHash } from '@/features/tables/tableViewState';

function tableLink(path: string, conditions: FilterCondition[]): string {
  const filter: FilterNode | null = conditions.length === 0 ? null : conditions.length === 1
    ? conditions[0] : { type: 'group', logic: 'and', children: conditions };
  return `/tables/plan.${path}${viewHash({ ...DEFAULT_VIEW, filter })}`;
}

function equals(column: string, value: string): FilterCondition {
  return { type: 'condition', column, op: 'eq', value };
}

export function offerTableLink(number: string): string {
  return tableLink('offers/registry', [equals('Номер', number)]);
}

export function offersTableLink(siteIds: string[]): string {
  return tableLink('offers/registry', siteIds.length === 0
    ? [] : [{ type: 'condition', column: 'КлючСтройки', op: 'in', values: siteIds }]);
}

export function scheduleTableLink(
  scheduleId: string, sectionId: string | null | undefined, warning: string | null,
): string {
  const conditions = [equals('КлючГрафика', scheduleId)];
  if (sectionId === null) conditions.push({ type: 'condition', column: 'КлючРаздела', op: 'is_empty' });
  else if (sectionId !== undefined) conditions.push(equals('КлючРаздела', sectionId));
  if (warning !== null) conditions.push({ type: 'condition', column: 'Предупреждения', op: 'contains', value: warning });
  return tableLink('schedule-works/works', conditions);
}
