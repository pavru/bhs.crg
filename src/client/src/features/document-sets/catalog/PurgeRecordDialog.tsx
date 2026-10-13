import { usePurgeRecord, type PurgeOffer } from '@/shared/api/recordPurge';
import { PurgeDialog } from './PurgeDialog';
import { consequences, purgeFailure, purgedToast } from './purgeRecord';

/**
 * Удаление записи, которую держат только данные выключенного или снятого модуля, — с потерей этих
 * ссылок (issue #1187).
 *
 * Открывается только из отказа обычного удаления и только вторым шагом: первым человеку предложен
 * архив. Само окно — общее с удалением уровня (`PurgeDialog`); здесь слова записи и её адрес.
 */
export function PurgeRecordDialog({ target, offer, onClose }: {
  target: { id: string; displayName: string };
  /** Предложение из отказа удаления: разбивка держателей и число. */
  offer: PurgeOffer;
  onClose: () => void;
}) {
  const purge = usePurgeRecord();
  return (
    <PurgeDialog offer={offer} pending={purge.isPending} onClose={onClose}
      subject={{
        title: `Удалить «${target.displayName}», потеряв ссылки?`,
        intro: 'На запись ссылаются данные выключенных модулей:',
        lines: consequences,
        submit: 'Удалить, потеряв ссылки',
        run: references => purge.mutateAsync({ id: target.id, references }),
        done: references => purgedToast(target.displayName, references),
        failure: purgeFailure,
      }} />
  );
}
