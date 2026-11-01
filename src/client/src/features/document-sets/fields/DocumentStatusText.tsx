import { StatusText, type StatusTone } from '@/shared/ui/StatusText';
import { STATUS_LABELS } from './constants';

/** Тон статуса документа — по смыслу ([UI-43]): генерация идёт — «в работе», ошибка — потеря. */
const STATUS_TONES: Record<string, StatusTone> = {
  Draft: 'neutral', Generating: 'primary', Generated: 'success', Failed: 'error',
};

/**
 * Статус экземпляра документа — общая пометка (#1416). Один вид на всех экранах, где он стоит: в
 * редакторе, в комплекте, в выборе ссылки. Смена вида пометки (`StatusText`, sharedui-md3-12) —
 * правка `StatusText`, а не этих экранов.
 */
export function DocumentStatusText({ status, className }: { status: string; className?: string }) {
  return (
    <StatusText tone={STATUS_TONES[status] ?? 'neutral'} className={className}>
      {STATUS_LABELS[status] ?? status}
    </StatusText>
  );
}
