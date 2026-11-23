import { acceptWorkReport } from '@/shared/api/workReview';
import { ReviewDecisionDialog, type ReviewDecisionProps } from './ReviewDecisionDialog';

export const ACCEPT_MARK_WARNING = 'В отчёте появится пометка «ввёл и принял один человек».';

export function AcceptDialog({ id, acceptWouldMark, ...props }: ReviewDecisionProps & { id: string; acceptWouldMark: boolean }) {
  return <ReviewDecisionDialog {...props} label="Принять" warning={acceptWouldMark ? ACCEPT_MARK_WARNING : undefined}
    submit={(version, changeId) => acceptWorkReport(id, version, changeId)} />;
}
