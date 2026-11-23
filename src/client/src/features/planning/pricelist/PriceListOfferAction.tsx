import { useState } from 'react';
import { useNavigate } from 'react-router';
import { useCan } from '@/shared/api/access';
import { Button } from '@/shared/ui/Button';
import { offerUrl } from '../offers/offerFormat';
import { PriceListOfferDialog } from './PriceListOfferDialog';

export function PriceListOfferAction() {
  const [open, setOpen] = useState(false);
  const can = useCan();
  const navigate = useNavigate();
  if (!can.permission('plan.offer.read') || !can.permission('plan.offer.edit')) return null;
  const canChooseSite = can.permission('core.constructions.read');
  return <>
    <Button variant="filled" disabled={!canChooseSite}
      title={!canChooseSite ? 'Чтобы выбрать стройку, нужно право «Читать стройки» (core.constructions.read).' : undefined}
      onClick={() => setOpen(true)}>КП по прайс-листу…</Button>
    {open && canChooseSite && <PriceListOfferDialog onClose={() => setOpen(false)}
      onOpenOffer={id => { setOpen(false); void navigate(offerUrl(id)); }} />}
  </>;
}
