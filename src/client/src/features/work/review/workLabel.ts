import type { ReviewFullCard, ReviewReportLine } from '@/shared/api/workReview';

export function workLabel(card: ReviewFullCard, line: ReviewReportLine) {
  return [card.workTypes[line.workTypeId] ?? 'Вид работы недоступен', line.variantId && card.workVariants[line.variantId]]
    .filter(Boolean).join(' · ');
}
