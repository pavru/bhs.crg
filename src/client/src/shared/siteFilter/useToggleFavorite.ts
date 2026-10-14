import { useToast } from '@/shared/ui/Toast';
import { useSiteFilterState, type SiteFilterValue } from './siteFilterContext';

/**
 * Отметить или снять стройку — с тостами по §62.7. Общее у звезды карточки и строки «не в избранном».
 * Возвращает false, если запись не удалась (отказ уже сказан тостом).
 */
export function useToggleFavorite() {
  const filter = useSiteFilterState();
  const toast = useToast();
  return async (id: string, favorite: boolean, value: SiteFilterValue = filter) => {
    const first = favorite && value.favorites.size === 0;
    const last = !favorite && value.favorites.size === 1 && value.favorites.has(id);
    try {
      await value.setFavorite(id, favorite);
    } catch (e) {
      toast.apiError(e, favorite ? 'Стройка не добавлена в избранное' : 'Стройка не убрана из избранного');
      return false;
    }
    if (first && value.on) {
      // Первая звезда при включённом фильтре прячет всё остальное на всех экранах — сказать прямо.
      toast.success('Стройка в избранном. Остальные скрыты на всех экранах',
        { action: { label: 'Показать все', onClick: () => value.setOn(false) } });
    } else if (favorite) {
      toast.success('Добавлено в избранное');
    } else if (last) {
      toast.info('Избранное пусто — показываются все стройки');
    } else {
      toast.success('Убрано из избранного', { action: { label: 'Вернуть', onClick: () => void value.setFavorite(id, true) } });
    }
    return true;
  };
}
