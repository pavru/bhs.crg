import { useState } from 'react';
import { useRereadDistribution, type DistributionRefusal } from '@/shared/api/planningDistribution';
import { ModalBanner } from '@/shared/ui/ModalBanner';
import { conflictText } from './distributionView';

/**
 * Отказ записи распределения — одной плашкой для экрана и обоих окон (в окне `ModalBanner` встаёт на место
 * сообщения окна, вне окна это обычный `Banner`). 409 — «Перечитать»: плашка снимается, когда новое распределение
 * пришло, а не сразу — иначе повтор, нажатый до ответа, ушёл бы с прежней версией и снова получил 409. Набранное
 * остаётся: оно живёт у вызывающего, плашка его не трогает.
 */
export function RefusalBanner({ refusal, revisionId, onClear }: {
  refusal: DistributionRefusal | null;
  revisionId: string;
  onClear: () => void;
}) {
  const reread = useRereadDistribution(revisionId);
  const [rereading, setRereading] = useState(false);
  if (!refusal) return null;
  if (refusal.kind === 'conflict') return (
    <ModalBanner tone="warning" actions={[{
      label: 'Перечитать', loading: rereading,
      onClick: () => { setRereading(true); void reread().finally(() => { setRereading(false); onClear(); }); },
    }]}>
      {conflictText(refusal)}
    </ModalBanner>
  );
  return <ModalBanner tone="error">{refusal.text}</ModalBanner>;
}
