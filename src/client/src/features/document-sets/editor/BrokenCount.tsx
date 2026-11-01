import { Badge } from '@/shared/ui/Badge';
import { StatusText } from '@/shared/ui/StatusText';
import { Tooltip } from '@/shared/ui/Tooltip';

// ── Счётчик битых ссылок ────────────────────────────────────────────────────

/**
 * Свод числа битых ссылок (issue #334) — на вкладке, пункте раздела и у подписи контейнерного поля ([UI-74]).
 * На вкладке и пункте навигации (`nav`) — `Badge`: битая ссылка — ошибка, красный ей впору. У подписи поля — пометка `error`
 * с числом: badge у подписи не ставят, а голое число (`CountText`) не сказало бы, что ссылки битые.
 */
export function BrokenCount({ count, nav = false, className = '' }: { count: number; nav?: boolean; className?: string }) {
  if (count <= 0) return null;
  const title = `Битых ссылок: ${count}`;
  if (!nav) return <StatusText tone="error" title={title} className={`tabular-nums ${className}`}>{count}</StatusText>;
  return (
    <Tooltip content={title}>
      <span className={className}><Badge count={count} label={title} /></span>
    </Tooltip>
  );
}
