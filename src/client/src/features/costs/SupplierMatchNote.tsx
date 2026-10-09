import { Button } from '@/shared/ui/Button';
import { ruPlural } from '@/shared/utils/pluralize';

/**
 * Полоса над строками счёта про запомненное (задача C3, issue #1079, ТЗ COST-7.1).
 *
 * <p>Три разных молчания, и путать их нельзя: поставщика нет — подставлять не по чему; спросить не
 * удалось — это не «ничего не запомнено»; запомненное есть — и ждёт кнопки.</p>
 *
 * <p>⚠️ <b>Лежащим строкам запомненное подставляется КНОПКОЙ, а не само.</b> Само оно подставляется
 * строке, которая рождается в форме: вставка из буфера и набор. Подставляй форма и лежащим — отменённая
 * подстановка возвращалась бы на каждое открытие счёта.</p>
 *
 * <p>⚠️ Строки, прочитанные со скана, для формы — ЛЕЖАЩИЕ: их пишет сервер, без позиции и без пометки, и
 * они ждут этой кнопки. Подстановка при самом распознавании — следующая часть задачи.</p>
 */
export function SupplierMatchNote({ hasSupplier, waiting, ready, blocked, failed, onApply }: {
  hasSupplier: boolean;
  /** Сколько строк ждёт позиции. */
  waiting: number;
  /** Скольким из них запомненное можно подставить. */
  ready: number;
  /** У скольких запомненная позиция в архиве или удалена. */
  blocked: number;
  /** Вопрос о запомненном не прошёл — словами отказа; `null` — прошёл либо не задавался. */
  failed: string | null;
  onApply: () => void;
}) {
  if (!hasSupplier)
    return (
      <p className="rounded-lg border border-stroke bg-surface2 px-3 py-2 text-xs text-fg3">
        Поставщик не выбран — запомненное подставить не по чему, и выбор позиций в этом счёте не
        запомнится. Выберите поставщика и сохраните шапку счёта.
      </p>
    );

  if (failed !== null)
    return (
      <p className="rounded-lg border border-warning-border bg-surface2 px-3 py-2 text-xs text-warning">
        Запомненное не проверено: {failed} Это не «ничего не запомнено» — позиции можно выбрать руками.
      </p>
    );

  if (ready === 0 && blocked === 0) return null;

  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 rounded-lg border border-stroke bg-surface2 px-3 py-2 text-xs text-fg2">
      {ready > 0 && (
        <>
          <span>
            У этого поставщика запомнено для {ready} из {waiting}{' '}
            {ruPlural(waiting, 'строки', 'строк', 'строк')} без позиции.
          </span>
          <Button size="sm" variant="outlined" onClick={onApply}>Подставить запомненное ({ready})</Button>
        </>
      )}
      {blocked > 0 && (
        <span className="text-fg4">
          {ready > 0 ? 'Ещё у ' : 'У '}{blocked} {ruPlural(blocked, 'строки', 'строк', 'строк')} запомненная
          позиция в архиве или удалена — не подставляется.
        </span>
      )}
    </div>
  );
}
