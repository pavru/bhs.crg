import { RefreshCw, TriangleAlert } from 'lucide-react';
import { Button } from './Button';
import { apiError } from '@/shared/utils/apiError';

/**
 * «Не перечитался» — полоса над экраном, который открыт, а перечитать его не удалось (issue #1287:
 * счёт и накладная; ревью PR #1319: сопоставление полей). Экран под ней остаётся с набранным:
 * раньше на его месте вставал отказ чтения, и набранное пропадало молча.
 *
 * <p>Лежит в общих компонентах: это не свойство счетов, а общая ловушка — упавший перезапрос
 * оставляет прежний ответ, а состояние запроса уже «ошибка», и экран, проверяющий ошибку раньше
 * данных, сносит сам себя.</p>
 *
 * <p>Полоса говорит две вещи, которые иначе пришлось бы угадывать: на экране ПРЕЖНЕЕ состояние (сосед
 * мог его изменить) и набранное не пропало. Сохранить не запрещено: устаревшую правку сервер отвергнет
 * по версии сам, а запрет здесь запер бы человека с набранным на всё время обрыва.</p>
 *
 * <p>⚠️ О сохранении полоса МОЛЧИТ (ревью PR #1300): её видит и тот, кто сохранить не может, — читатель
 * без права правки, проведённая накладная, счёт закрытого периода. Действие называет только сама
 * форма, своей кнопкой, там, где оно есть.</p>
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
        {what}: {apiError(error, 'сервер не ответил')}. На экране — прежнее состояние: его могли тем
        временем изменить. Набранное не пропало.
      </p>
      <Button size="sm" variant="outlined" icon={<RefreshCw size={13} />} loading={retrying} onClick={onRetry}>
        Повторить
      </Button>
    </div>
  );
}
