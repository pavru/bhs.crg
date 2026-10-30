import { StatusBadge, type StatusTone } from '@/shared/ui/StatusBadge';
import { STATUS_LABELS } from './constants';

/** Тон статуса документа — по смыслу ([UI-43]): генерация идёт — «в работе», ошибка — потеря. */
const STATUS_TONES: Record<string, StatusTone> = {
  Draft: 'neutral', Generating: 'brand', Generated: 'success', Failed: 'danger',
};

/**
 * Статус экземпляра документа — общая пометка (#1416). Один вид на всех экранах, где он стоит: в
 * редакторе, в комплекте, в выборе ссылки. Смена вида пометки (`StatusText`, sharedui-md3-12) —
 * правка `StatusBadge`, а не этих экранов.
 */
export function DocumentStatusBadge({ status, className }: { status: string; className?: string }) {
  return (
    <StatusBadge tone={STATUS_TONES[status] ?? 'neutral'} className={className}>
      {STATUS_LABELS[status] ?? status}
    </StatusBadge>
  );
}
