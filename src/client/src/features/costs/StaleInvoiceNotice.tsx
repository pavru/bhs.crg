import { RefreshCw, TriangleAlert } from 'lucide-react';
import { Button } from '@/shared/ui/Button';

/**
 * «Счёт изменили, пока вы правили» — полоса над черновиком, чья часть счёта изменилась под
 * несохранёнными правками (issue #1176).
 *
 * <p>Набранное остаётся на экране: сохранение откажет, но ничего не пропадает, пока человек сам не
 * нажмёт «Перечитать счёт». Автоматически черновик не заменяется — он мог набирать его час.</p>
 *
 * <p>⚠️ Кнопка говорит, что она сделает с набранным, — «заменятся сохранёнными». «Перечитать» без
 * этой оговорки читается как безопасное действие, а стирает правки.</p>
 */
export function StaleInvoiceNotice({ what, onReread }: {
  /** Что именно набрано и не сохранится: «строки», «разноска», «поля счёта». */
  what: string;
  onReread: () => void;
}) {
  return (
    <div role="alert" className="flex items-start gap-2 rounded-lg border border-warning-border
      bg-warning-subtle px-3 py-2 text-xs text-warning">
      <TriangleAlert size={13} className="shrink-0 mt-0.5" />
      <p className="flex-1">
        Счёт тем временем изменили — {what} на экране собраны по прежнему виду и поверх чужой правки
        не сохранятся. Набранное остаётся здесь: перенесите нужное себе, затем перечитайте счёт —
        несохранённые правки заменятся сохранёнными.
      </p>
      <Button size="sm" variant="outlined" icon={<RefreshCw size={12} />} onClick={onReread}>
        Перечитать счёт
      </Button>
    </div>
  );
}
