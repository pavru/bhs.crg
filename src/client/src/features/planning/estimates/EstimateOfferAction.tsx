import { useState } from 'react';
import { useLocation, useNavigate } from 'react-router';
import { useCan } from '@/shared/api/access';
import type { EstimateRevision } from '@/shared/api/planningEstimates';
import { Button } from '@/shared/ui/Button';
import { CreateOfferDialog } from '../offers/CreateOfferDialog';
import { offerUrl } from '../offers/offerFormat';

export function EstimateOfferAction({ estimateName, revision }: { estimateName: string; revision: EstimateRevision }) {
  const can = useCan();
  const navigate = useNavigate();
  const location = useLocation();
  const [open, setOpen] = useState(false);
  if (!can.permission('plan.offer.edit')) return null;
  return <>
    <Button variant="filled" onClick={() => setOpen(true)}>Сделать КП…</Button>
    {open && <CreateOfferDialog estimateName={estimateName} revision={revision} canOpen={can.permission('plan.offer.read')}
      onClose={() => setOpen(false)} onCreated={offer => {
        setOpen(false);
        void navigate(offerUrl(offer.id), { state: { estimatePath: location.pathname + location.search } });
      }} />}
  </>;
}
