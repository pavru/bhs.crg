import type { PlanProgress } from '@/shared/api/plans';
import { ruCount } from '@/shared/utils/pluralize';

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
    <span className={`inline-flex items-center gap-1.5 text-[11px] ${className}`}
      title={hint(progress)}>
      <span className={`px-2 py-0.5 rounded-full tabular-nums ${
        done ? 'bg-brand-subtle text-brand' : 'bg-muted text-fg2'}`}>
        {progress.ready} / {progress.planned} · {progress.percent}%
      </span>
      {/* Неразобранное сверкой держит процент на 99 % — без этой оговорки цифра выглядит
          необъяснимо застрявшей. Само число проблем рисует свой бейдж, здесь только причина. */}
      {progress.needsAttention > 0 && progress.ready >= progress.planned && (
        <span className="text-fg4">не разобрано: {progress.needsAttention}</span>
      )}
      {progress.setsWithoutPlan > 0 && (
        <span className="text-fg4">
          без плана: {ruCount(progress.setsWithoutPlan, 'комплект', 'комплекта', 'комплектов')}
        </span>
      )}
    </span>
  );
}

function hint(p: PlanProgress): string {
  const parts = [`Закрыто позиций плана: ${p.ready} из ${p.planned}`];
  if (p.needsAttention > 0) parts.push(`не разобрано сверкой: ${p.needsAttention}`);
  if (p.setsWithoutPlan > 0) parts.push(`комплектов без плана: ${p.setsWithoutPlan} (в процент не входят)`);
  return parts.join('; ') + '.';
}
