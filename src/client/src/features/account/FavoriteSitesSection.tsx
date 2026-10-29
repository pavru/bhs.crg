import { Icon } from '@/shared/ui/Icon';
import { Star } from '@/shared/ui/icons';
import { FavoritesPicker } from '@/shared/siteFilter/FavoritesPicker';
import { useSiteFilterState } from '@/shared/siteFilter/siteFilterContext';

/**
 * «Избранные стройки» в профиле (issue #1218) — постоянный адрес выбора. Не единственный вход: в
 * профиль ходят через пилюлю внизу рейла, и страница про учётную запись, а не про работу, — основной
 * вход в шапке, рядом с переключателем.
 *
 * <p>Раздела нет — вместе с заголовком, — когда вошедшему не открыт справочник строек: выбирать не
 * из чего. Решает это одно место, здесь, как у пространства по умолчанию.</p>
 */
export function FavoriteSitesSection() {
  const filter = useSiteFilterState();
  if (filter.status === 'unavailable') return null;
  return (
    <div className="mt-8 pt-6 border-t border-outline-variant" data-testid="favorite-sites-section">
      <h2 className="flex items-center gap-2 text-body-medium font-semibold text-on-surface mb-3">
        <Icon icon={Star} size={18} className="text-on-surface-variant" /> Избранные стройки
      </h2>
      <FavoritesPicker maxHeight="max-h-96" />
    </div>
  );
}
