import { planTitle, type PlanProgress } from '@/shared/api/plans';
import { ruCount } from '@/shared/utils/pluralize';
import { Tooltip } from '@/shared/ui/Tooltip';
import { StatusText } from '@/shared/ui/StatusText';

/**
 * Готовность по плану — в шапке уровня (issue #796).
 *
 * Цвет НЕЙТРАЛЬНЫЙ, а не «зелёное/красное»: красный на этих экранах уже занят арифметикой сверки,
 * и второй красный рядом читался бы как ошибка там, где просто не всё готово. Готовность — это
 * прогресс, а не проблема.
 *
 * Плана нет — нет и бейджа. Показать «0 %» значило бы соврать: где не планировали, там ничего и
 * не должно.
 */
export function PlanProgressBadge({ progress, className = '' }: {
  progress: PlanProgress | undefined;
  className?: string;
}) {
  if (!progress?.hasPlan || progress.percent == null) return null;

  const done = progress.percent === 100;

  return (
    <Tooltip content={planTitle(progress)}>
      <span className={`inline-flex items-center gap-1.5 text-label-small ${className}`}>
        <StatusText tone={done ? 'success' : 'neutral'} className="tabular-nums">
          {progress.ready} / {progress.planned} · {progress.percent}%
        </StatusText>
        {/* Неразобранное сверкой держит процент на 99 % — без этой оговорки цифра выглядит
            необъяснимо застрявшей. Само число проблем рисует свой бейдж, здесь только причина. */}
        {progress.needsAttention > 0 && progress.ready >= progress.planned && (
          <span className="text-on-surface-variant">не разобрано: {progress.needsAttention}</span>
        )}
        {progress.setsWithoutPlan > 0 && (
          <span className="text-on-surface-variant">
            без плана: {ruCount(progress.setsWithoutPlan, 'комплект', 'комплекта', 'комплектов')}
          </span>
        )}
      </span>
    </Tooltip>
  );
}
