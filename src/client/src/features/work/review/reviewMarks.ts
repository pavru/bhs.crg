import { useQueries } from '@tanstack/react-query';
import { WORK_MARK_CODES, type ReviewQueuePage, type WorkMarkCode } from '@/shared/api/work';
import { readReviewQueue, REVIEW_QUEUE_KEY, type ReviewQueueFilters } from '@/shared/api/workReview';
import { Apartment, Edit, Group, HistoryIcon, ImageIcon, Person, Schedule, Warning, type IconDef } from '@/shared/ui/icons';
import type { StatusTone } from '@/shared/ui/StatusText';

const catalog: Record<WorkMarkCode, { label: string; icon: IconDef; tone: StatusTone }> = {
  'edited-after-submit': { label: 'правка после подачи', icon: Edit, tone: 'warning' },
  'same-person': { label: 'ввёл и принял один человек', icon: Person, tone: 'warning' },
  'manager-edit': { label: 'правка менеджером', icon: Edit, tone: 'warning' },
  'site-not-assigned': { label: 'стройка не назначена на дату', icon: Apartment, tone: 'warning' },
  'photos-pending': { label: 'фото не загружены', icon: ImageIcon, tone: 'neutral' },
  'crew-mismatch': { label: 'расхождение в бригаде', icon: Group, tone: 'warning' },
  'accepted-changed': { label: 'изменение принятого', icon: HistoryIcon, tone: 'warning' },
  'late-delivery': { label: 'поздняя доставка', icon: Schedule, tone: 'neutral' },
  'fields-missing': { label: 'не заполнены поля', icon: Warning, tone: 'warning' },
  'required-missing': { label: 'не заполнены место или материалы', icon: Warning, tone: 'warning' },
};

function knownMark(code: string): code is WorkMarkCode {
  return Object.hasOwn(catalog, code);
}

export function reviewMarkStyle(code: string) {
  return knownMark(code) ? catalog[code] : { icon: Warning, tone: 'neutral' as const };
}

/** Числа берём из всего отбора. Неизвестные коды идут после каталога, нулевые не показываются. */
export function useReviewMarkFilters(
  page: ReviewQueuePage | undefined, scope: Omit<ReviewQueueFilters, 'mark'>, confirmed: boolean,
) {
  const counts = page?.counts ?? {};
  const positive = [...WORK_MARK_CODES, ...Object.keys(counts).filter(code => !knownMark(code))]
    .filter(code => counts[code] > 0);
  const labels = new Map(page?.items.flatMap(row => row.marks.map(mark => [mark.code, mark.text] as const)));
  const unknown = positive.filter(code => !knownMark(code));

  // У нового кода счётчик может быть ненулевым за пределами страницы. Берём его подпись из строки
  // сервера, сохраняя отбор, от которого получен счётчик. Известные коды дополнительных запросов не требуют.
  const lookup = useQueries({
    queries: unknown.map(code => ({
      queryKey: [...REVIEW_QUEUE_KEY, 'mark-label', scope, code],
      initialData: confirmed ? labels.get(code) : undefined,
      enabled: confirmed && !labels.has(code),
      queryFn: ({ signal }: { signal: AbortSignal }) => readReviewQueue({ ...scope, mark: code, limit: 1 }, signal)
        .then(result => result.items[0]?.marks.find(mark => mark.code === code)?.text ?? null),
    })),
  });
  unknown.forEach((code, index) => {
    const text = lookup[index].data;
    if (text && !labels.has(code)) labels.set(code, text);
  });

  return {
    items: positive.flatMap(code => {
      const label = knownMark(code) ? catalog[code].label : labels.get(code);
      return label ? [{ code, label, count: counts[code], icon: reviewMarkStyle(code).icon }] : [];
    }),
    unavailable: confirmed && unknown.some((code, index) =>
      !labels.has(code) && (lookup[index].isError || lookup[index].data === null)),
  };
}
