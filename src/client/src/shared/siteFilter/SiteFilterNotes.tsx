import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Star } from '@/shared/ui/icons';
import { useSiteFilterState } from './siteFilterContext';
import { useToggleFavorite } from './useToggleFavorite';
import { IconButton } from '@/shared/ui/Button';

const link = 'text-primary hover:underline focus-ring rounded-xs';

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
    <IconButton size="sm" label={on ? 'Убрать из избранного' : 'Добавить в избранное'}
      aria-pressed={on} disabled={busy} data-testid="favorite-star" className="-ml-1"
      onClick={async e => {
        e.stopPropagation();
        onToggled?.();
        setBusy(true);
        await toggle(id, !on);
        setBusy(false);
      }}>
      <Icon icon={Star} size={18} className={on ? 'text-primary fill-current' : 'text-on-surface-variant'} aria-hidden />
    </IconButton>
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
    <span className="inline-flex items-center gap-1.5 ml-2 text-xs text-on-surface-variant" data-testid="not-favorite-note">
      <Icon icon={Star} size={18} aria-hidden />
      <span>Стройка не в избранном — в списках она скрыта.</span>
      <button type="button" className={link} onClick={() => void toggle(constructionId, true)}>Добавить в избранное</button>
    </span>
  );
}

const quiet = 'underline hover:no-underline focus-ring rounded-xs';

/**
 * Пустой список, когда объекты есть, но скрыты фильтром (рейл счетов #1221, накладные #1222): «…пока нет»
 * или «Ничего не найдено» здесь были бы неправдой. Текст и выход называет экран — он знает, что известно:
 * у счетов поиск видит скрытые, у накладных — нет (их не прислал сервер).
 */
export function FilterHidesAll({ text, action, onShowAll, testId }: {
  text: string; action: string; onShowAll: () => void; testId: string;
}) {
  return (
    <div className="px-3 py-2 text-xs text-on-surface-variant space-y-1" data-testid={testId}>
      <p>{text}</p>
      <button type="button" className={quiet} onClick={onShowAll}>{action}</button>
    </div>
  );
}

/** Над формой объекта, скрытого в списке слева: без неё форма выглядела бы объектом, которого нет. */
export function NotInListNotice({ text, onShowAll, testId }: { text: string; onShowAll: () => void; testId: string }) {
  return (
    <p className="flex items-center gap-1.5 px-4 py-1.5 text-xs text-on-surface-variant border-b border-outline-variant" data-testid={testId}>
      <Icon icon={Star} size={18} aria-hidden />
      <span>{text}.</span>
      <button type="button" className={quiet} onClick={onShowAll}>Показать все</button>
    </p>
  );
}
