import { forwardRef, useState } from 'react';
import { Button } from '@/shared/ui/Button';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { Icon } from '@/shared/ui/Icon';
import { ArrowDropDown, Info, type IconDef } from '@/shared/ui/icons';
import { StatusText } from '@/shared/ui/StatusText';
import { briefOf, type InvoiceStatus, type StatusMood } from './invoiceStatus';

/**
 * Индикатор состояния счёта в сводке (costs-invoice-screen-11, D-11.3, ТЗ COST-6.4): text-кнопка с пометкой
 * тона худшего уровня и фразой — макет А «пометка в кнопке», выбор владельца 07.11.2026. Щелчок и Enter
 * открывают перечень.
 *
 * <p>Своей пилюли и точки нет — пометка `StatusText` ([UI-74]). Цвет не единственный носитель: фраза и
 * число всегда текстом, у тона — знак.</p>
 *
 * <p>Диктор слышит смену УРОВНЯ (`aria-live="polite"`), а не каждый пункт: перечень меняется с каждой
 * записью. Переход в «Мешает работе» молчит — его объявляет срочный banner сам, и дважды слышать одно не
 * нужно.</p>
 */

/** Знак тона там, где у `StatusText` своего нет: «К сведению» и разобранный без пунктов. */
const MOOD_ICON: Partial<Record<StatusMood, IconDef>> = { info: Info, none: Info };

export const InvoiceStatusIndicator = forwardRef<HTMLButtonElement, {
  status: InvoiceStatus;
  open: boolean;
  onOpen: () => void;
}>(function InvoiceStatusIndicator({ status, open, onOpen }, ref) {
  const said = useLevelAnnouncement(status);
  const label = status.phrase || 'Состояние счёта';
  return (
    <>
      <span aria-live="polite" className="sr-only" data-testid="invoice-status-live">{said}</span>
      <Button ref={ref} variant="text" aria-haspopup="dialog" aria-expanded={open} data-testid="invoice-status" data-count={status.count}
        aria-label={`${label} — открыть перечень`} title={briefOf(status)} onClick={onOpen}
        className="shrink-0" trailingIcon={<Icon icon={ArrowDropDown} size={18} className="shrink-0 text-on-surface-variant" />}>
        {status.mood === 'running'
          ? <StatusText tone="primary"><CircularProgress size={18} />{status.phrase}</StatusText>
          : <StatusText tone={status.tone} icon={MOOD_ICON[status.mood]}>{status.phrase}</StatusText>}
      </Button>
    </>
  );
});

/**
 * Что сказать диктору: фраза — когда сменился уровень; при том же уровне — то, что сказано раньше (повтор
 * той же строки живой регион не объявляет). Первый показ молчит: человек сам открыл счёт.
 */
function useLevelAnnouncement(status: InvoiceStatus): string {
  const [said, setSaid] = useState<{ mood: StatusMood; text: string }>({ mood: status.mood, text: '' });
  if (said.mood !== status.mood)
    setSaid({ mood: status.mood, text: status.mood === 'blocking' ? '' : status.phrase || 'Сказать о счёте нечего' });
  return said.text;
}
