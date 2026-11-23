import { useState } from 'react';
import { returnWorkReport } from '@/shared/api/workReview';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { ReviewDecisionDialog, type ReviewDecisionProps } from './ReviewDecisionDialog';

export function ReturnDialog({ id, ...props }: ReviewDecisionProps & { id: string }) {
  const [comment, setComment] = useState('');
  return <ReviewDecisionDialog {...props} label="Вернуть" comment={comment.trim()}
    submit={(version, changeId, text) => returnWorkReport(id, version, changeId, text)}>
    <TextAreaField label="Что исправить" required maxLength={2000} value={comment}
      onChange={event => setComment(event.target.value)} hint="Комментарий обязателен. Его увидит сотрудник." />
  </ReviewDecisionDialog>;
}
