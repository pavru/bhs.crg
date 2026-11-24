import type { ScheduleAssignee, ScheduleWarning } from '@/shared/api/planningScheduleTypes';
import { formatNumber } from '@/shared/format/format';
import { ruCount, ruPlural } from '@/shared/utils/pluralize';

export type ScheduleSection = { id: string; name: string };

export function sectionName(sectionId: string | null, sections: ScheduleSection[]) {
  return sectionId === null ? 'По стройке в целом' : sections.find(section => section.id === sectionId)?.name ?? 'Раздел недоступен';
}

export function skippedText(unmapped: number) {
  const skipped = ruCount(unmapped, 'несопоставленная строка', 'несопоставленные строки', 'несопоставленных строк');
  return `${ruPlural(unmapped, 'Не вошла', 'Не вошли', 'Не вошло')} ${skipped} сметы.`;
}

export function volumeText(volume: number | null, unit?: string | null) {
  return volume === null ? '—' : `${formatNumber(volume, 6)}${unit ? ` ${unit}` : ''}`;
}

export function assigneeText(ids: string[], assignees: readonly ScheduleAssignee[]) {
  return ids.map(id => {
    const person = assignees.find(item => item.id === id);
    return person ? `${person.name}${person.archived ? ' (в архиве)' : ''}` : 'Сотрудник недоступен';
  }).join(', ') || '—';
}

const warningLabels: Record<string, string> = {
  'starts-before-predecessor': 'Нарушения зависимостей',
  'ends-on-day-off': 'Окончание на выходном',
  'no-dates': 'Без сроков',
  'calendar-missing': 'Пробелы календаря',
  'lag-out-of-range': 'Задержка вне диапазона',
};

export function warningFilters(warnings: ScheduleWarning[]) {
  const byCode = new Map<string, Set<string>>();
  for (const warning of warnings) {
    const key = warning.linkId && (warning.code === 'starts-before-predecessor' || warning.code === 'lag-out-of-range')
      ? warning.linkId : warning.workId;
    const matches = byCode.get(warning.code) ?? new Set<string>();
    matches.add(key);
    byCode.set(warning.code, matches);
  }
  return [...byCode].map(([code, matches]) => ({
    code, count: matches.size, label: warningLabels[code] ?? warnings.find(warning => warning.code === code)!.text,
  }));
}
