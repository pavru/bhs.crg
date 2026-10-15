import { useState } from 'react';
import { Star } from 'lucide-react';
import { useSiteFilterState } from './siteFilterContext';
import { useToggleFavorite } from './useToggleFavorite';

const link = 'text-brand hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand rounded';

/**
 * Свидетельство у урезанного списка (§62.1): «почему тут мало» отвечает строка у самого списка, а не
 * шапка — та одна на все экраны. Закрыть строку нельзя нарочно: забытое «включено» прячет данные, и
 * эта строка — всё напоминание о нём. «Показать все» переключает ГЛОБАЛЬНОЕ состояние: шапка меняется
 * на глазах, второго состояния поверх первого нет.
 *
 * @param partial список усечён пределом (накладные, #1222): число скрытых верно, но сверить его с
 *     разницей против «Все» по усечённым спискам нельзя — и строка говорит «часть скрыта», без числа.
 */
export function SiteFilterNote({ hidden, partial = false }: { hidden: number; partial?: boolean }) {
  const filter = useSiteFilterState();
  if (!filter.active) return null;
  return (
    <p className="flex items-center gap-1.5 text-xs text-fg3" data-testid="site-filter-note">
      <Star size={12} className="text-brand fill-current" aria-hidden />
      <span>Избранные стройки · {partial && hidden > 0 ? 'часть скрыта' : `скрыто: ${hidden}`}</span>
      <span aria-hidden>·</span>
      <button type="button" className={link} onClick={() => filter.setOn(false)}>Показать все</button>
    </p>
  );
}

/**
 * Звезда стройки (§62.2). Стоит вне hover-группы действий: она ещё и признак, а невидимый признак
 * ничего не помечает. Клик не проваливается в карточку.
 */
export function FavoriteStar({ id, onToggled }: { id: string; onToggled?: () => void }) {
  const filter = useSiteFilterState();
  const toggle = useToggleFavorite();
  const [busy, setBusy] = useState(false);
  if (filter.status !== 'ready') return null;
  const on = filter.favorites.has(id);
  return (
    <button type="button" aria-pressed={on} disabled={busy} data-testid="favorite-star"
      aria-label={on ? 'Убрать из избранного' : 'Добавить в избранное'}
      title={on ? 'Убрать из избранного' : 'Добавить в избранное'}
      onClick={async e => {
        e.stopPropagation();
        onToggled?.();
        setBusy(true);
        await toggle(id, !on);
        setBusy(false);
      }}
      className="shrink-0 -ml-1 flex items-center justify-center w-7 h-7 rounded-full hover:bg-black/5 dark:hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
      <Star size={16} className={on ? 'text-brand fill-current' : 'text-fg4'} aria-hidden />
    </button>
  );
}

/**
 * Прямая ссылка на неизбранную стройку, её раздел или комплект (§62.5): объект открыт полностью, а
 * над ним — одна тихая строка. Без неё человек «теряет» объект, выйдя по крошке «Стройки» в список,
 * где его нет. «Добавить» предлагается: это ровно тот момент, когда стройка оказалась нужна.
 */
export function NotFavoriteNote({ constructionId }: { constructionId: string }) {
  const filter = useSiteFilterState();
  const toggle = useToggleFavorite();
  if (!filter.active || !constructionId || filter.favorites.has(constructionId)) return null;
  return (
    <span className="inline-flex items-center gap-1.5 ml-2 text-xs text-fg3" data-testid="not-favorite-note">
      <Star size={12} aria-hidden />
      <span>Стройка не в избранном — в списках она скрыта.</span>
      <button type="button" className={link} onClick={() => void toggle(constructionId, true)}>Добавить в избранное</button>
    </span>
  );
}
