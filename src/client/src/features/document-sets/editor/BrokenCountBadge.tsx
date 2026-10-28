import { Tooltip } from '@/shared/ui/Tooltip';

// ── Счётчик битых ссылок ────────────────────────────────────────────────────

/** Свод-бейдж числа битых ссылок (issue #334) — на пункте раздела / контейнерном поле / вкладке. */

export function BrokenCountBadge({ count, className = '' }: { count: number; className?: string }) {
  if (count <= 0) return null;
  return (
    <Tooltip content={`Битых ссылок: ${count}`}>
      <span
        className={`inline-flex items-center justify-center min-w-[16px] h-4 px-1 rounded-full bg-error text-on-error text-caption font-semibold leading-none ${className}`}>
        {count}
      </span>
    </Tooltip>
  );
}
