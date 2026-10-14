import { useToast } from '@/shared/ui/Toast';
import { useSiteFilterState } from './siteFilterContext';

/**
 * Отметить или снять стройку — с тостами по §62.7. Общее у звезды карточки и строки «не в избранном».
 * Возвращает false, если запись не удалась (отказ уже сказан тостом).
 */
export function useToggleFavorite() {
  const filter = useSiteFilterState();
  const toast = useToast();

  async function toggle(id: string, favorite: boolean): Promise<boolean> {
    const first = favorite && filter.favorites.size === 0;
    const last = !favorite && filter.favorites.size === 1 && filter.favorites.has(id);
    try {
      await filter.setFavorite(id, favorite);
    } catch (e) {
      toast.apiError(e, favorite ? 'Стройка не добавлена в избранное' : 'Стройка не убрана из избранного');
      return false;
    }
    if (first && filter.on) {
      // Первая звезда при включённом фильтре прячет всё остальное на всех экранах — сказать прямо.
      toast.success('Стройка в избранном. Остальные скрыты на всех экранах',
        { action: { label: 'Показать все', onClick: () => filter.setOn(false) } });
    } else if (favorite) {
      toast.success('Добавлено в избранное');
    } else if (last) {
      toast.info('Избранное пусто — показываются все стройки');
    } else {
      // «Вернуть» — та же запись, и её отказ (предел, сеть) говорится так же, а не теряется
      // необработанным промисом: кнопка обещала вернуть (ревью PR #1331).
      toast.success('Убрано из избранного', { action: { label: 'Вернуть', onClick: () => void toggle(id, true) } });
    }
    return true;
  }

  return toggle;
}
