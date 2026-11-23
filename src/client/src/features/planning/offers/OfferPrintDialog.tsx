import { useQuery } from '@tanstack/react-query';
import { saveBlob } from '@/shared/api/attachments';
import { offerRefusal, previewOfferPrint } from '@/shared/api/planningOffers';
import type { Offer } from '@/shared/api/planningOfferTypes';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Modal } from '@/shared/ui/Modal';
import { PdfViewerOverlay } from '@/shared/ui/viewer/PdfViewer';

export function OfferPrintDialog({ offer, onClose }: { offer: Offer; onClose: () => void }) {
  const query = useQuery({
    queryKey: ['planning', 'offer-print', offer.id, offer.version],
    queryFn: ({ signal }) => previewOfferPrint(offer.id, signal), gcTime: 0, retry: false, refetchOnWindowFocus: false,
  });
  const failure = query.isError ? offerRefusal(query.error, 'Печатная форма КП не построилась.') : null;
  const title = `Печать ${offer.number} · версия ${offer.currentVersion}`;
  const changeOpen = (open: boolean) => { if (!open) onClose(); };
  if (failure) return <Modal open title={title} nothingToLose onOpenChange={changeOpen}>
    <Banner tone="error" actions={failure.kind !== 'forbidden' ? [{ label: 'Повторить', onClick: () => void query.refetch(), loading: query.isFetching }] : undefined}>
      {failure.message}
    </Banner>
  </Modal>;
  const pdf = query.data ?? null;
  return <PdfViewerOverlay open onOpenChange={changeOpen} source={pdf} title={title} documentKey={`${offer.id}:${offer.version}`}
    note={pdf && <div className="flex justify-end">
      <Button variant="text" onClick={() => saveBlob(pdf, `${offer.number}.pdf`)}>Скачать PDF</Button>
    </div>} />;
}
