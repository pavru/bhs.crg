import type { OfferDrift } from '@/shared/api/planningOfferTypes';
import { Banner } from '@/shared/ui/Banner';

export function OfferDriftBanner({ drift, onOpen }: { drift: OfferDrift; onOpen?: () => void }) {
  if (drift.code === 'none') return null;
  return <Banner tone="warning" actions={onOpen ? [{ label: 'Обновить по смете…', onClick: onOpen }] : undefined}>{drift.text}</Banner>;
}
