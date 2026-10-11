import { RefreshCw, TriangleAlert } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { apiError } from '@/shared/utils/apiError';

/**
 * «Не перечитался» — полоса над формой документа, который открыт, а перечитать его не удалось
 * (issue #1287). Форма под ней остаётся с набранным: раньше на её месте вставал отказ чтения, и
 * набранное пропадало молча.
 *
 * <p>Полоса говорит две вещи, которые иначе пришлось бы угадывать: на экране ПРЕЖНЕЕ состояние (сосед
 * мог его изменить) и набранное цело. Сохранить не запрещено: устаревшую правку сервер отвергнет по
 * версии сам, а запрет здесь запер бы человека с набранным на всё время обрыва.</p>
 */
export function RereadFailedNotice({ what, error, retrying, onRetry }: {
  /** «Счёт не перечитался» / «Накладная не перечиталась». */
  what: string;
  error: unknown;
  retrying: boolean;
  onRetry: () => void;
}) {
  return (
    <div role="alert" data-testid="reread-failed" className="shrink-0 flex items-start gap-2 border-b
      border-warning-border bg-warning-subtle px-5 py-2 text-xs text-warning">
      <TriangleAlert size={13} className="shrink-0 mt-0.5" />
      <p className="flex-1">
        {what}: {apiError(error, 'сервер не ответил')}. На экране — прежнее состояние, набранное цело.
        Сохранять можно: если документ тем временем изменили, сервер откажет и ничего не затрёт.
      </p>
      <Button size="sm" variant="outlined" icon={<RefreshCw size={13} />} loading={retrying} onClick={onRetry}>
        Повторить
      </Button>
    </div>
  );
}
