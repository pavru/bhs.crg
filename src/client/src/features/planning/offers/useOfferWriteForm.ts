import { useRef, useState } from 'react';
import { offerRefusal, useWriteOffer, type OfferCommand, type OfferRefusal } from '@/shared/api/planningOffers';
import type { Offer } from '@/shared/api/planningOfferTypes';

export type ReloadOffer = () => Promise<Offer>;

/** Версия принадлежит открытой форме. Фоновое чтение карточки не обновляет её под введёнными данными. */
export function useOfferWriteForm(offer: Offer, reloadOffer: ReloadOffer) {
  const [base, setBase] = useState<Offer | null>(null);
  const [refusal, setRefusal] = useState<OfferRefusal | null>(null);
  const [reviewed, setReviewed] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const mutation = useWriteOffer(offer.id);
  const running = useRef(false);

  const seen = base ?? offer;
  const capture = () => setBase(previous => previous ?? offer);

  async function apply(command: OfferCommand) {
    if (running.current || refusal?.kind === 'conflict' || refusal?.kind === 'forbidden') return;
    running.current = true;
    try {
      const updated = await mutation.mutateAsync({ version: seen.version, command });
      setBase(null);
      setRefusal(null);
      setReviewed(false);
      return updated;
    } catch (error) {
      setBase(seen);
      setRefusal(offerRefusal(error, 'Изменения КП не сохранены.'));
    } finally {
      running.current = false;
    }
  }

  async function reload() {
    if (running.current) return;
    running.current = true;
    setRefreshing(true);
    try {
      setBase(await reloadOffer());
      setRefusal(null);
      setReviewed(true);
    } catch (error) {
      const failure = offerRefusal(error, 'КП не перечиталось.');
      setRefusal(failure.kind === 'forbidden' ? failure : { kind: 'conflict', message: failure.message });
    } finally {
      setRefreshing(false);
      running.current = false;
    }
  }

  function reset() {
    setBase(null);
    setRefusal(null);
    setReviewed(false);
  }

  return { seen, capture, apply, reload, reset, refusal, reviewed, busy: mutation.isPending || refreshing,
    blocked: refusal?.kind === 'conflict' || refusal?.kind === 'forbidden' };
}
