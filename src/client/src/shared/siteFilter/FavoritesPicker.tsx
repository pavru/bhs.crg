import { useMemo, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Search, Star } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { Checkbox } from '@/shared/ui/Checkbox';
import { apiError } from '@/shared/utils/apiError';
import { useSiteFilterState } from './siteFilterContext';

/** С какого числа строек в выборе появляется поиск: на коротком списке он только мешает. */
const SEARCH_FROM = 8;

/**
 * Выбор избранных строек (issue #1218) — ОДИН компонент на три входа: отбор «Стройки» списка (#1432), раздел
 * профиля и — точечно — звезда на карточке стройки. Галочка пишется сразу, без кнопки «Сохранить».
 *
 * <p>Порядок строк запоминается при открытии: отмеченные — сверху. Пересортировка на каждой галочке
 * уводила бы строку из-под курсора, и второй щелчок попадал бы в соседнюю.</p>
 */
export function FavoritesPicker({ maxHeight = 'max-h-72' }: { maxHeight?: string }) {
  const filter = useSiteFilterState();
  const [query, setQuery] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  // Отмеченные на момент, когда избранное стало ИЗВЕСТНО, — наверх; дальше порядок не меняется.
  // Не на момент открытия: профиль открывают и до прихода настроек, и снимок был бы пустым.
  const [initial, setInitial] = useState<ReadonlySet<string> | null>(null);
  if (initial === null && filter.status === 'ready') setInitial(new Set(filter.favorites));

  const ordered = useMemo(
    () => [...filter.sites].sort((a, b) => Number(!!initial?.has(b.id)) - Number(!!initial?.has(a.id))),
    [filter.sites, initial]);
  const needle = query.trim().toLowerCase();
  const shown = needle ? ordered.filter(s => s.name.toLowerCase().includes(needle)) : ordered;

  async function run(action: () => Promise<void>) {
    setError('');
    setBusy(true);
    try { await action(); }
    catch (e) { setError(apiError(e, 'Избранное не сохранено')); }
    finally { setBusy(false); }
  }

  if (filter.status === 'loading') return <p className="text-sm text-fg4">Загрузка…</p>;
  if (filter.status === 'error') {
    return <p role="alert" className="text-sm text-error">Избранные стройки не загрузились. Обновите страницу.</p>;
  }

  return (
    <div className="space-y-3" data-testid="favorites-picker">
      <p className="text-xs text-fg3">
        Отмеченные стройки показываются во всех списках, остальные скрываются. Это удобство, а не права:
        отбор «Стройки» над списком показывает и все.
      </p>
      {filter.sites.length >= SEARCH_FROM && (
        <div className="relative">
          <Icon icon={Search} size={18} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg4 pointer-events-none" />
          <input value={query} onChange={e => setQuery(e.target.value)} placeholder="Название стройки…"
            aria-label="Название стройки"
            className="w-full h-8 pl-8 pr-2 text-sm border border-outline rounded-md bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-primary" />
        </div>
      )}
      {filter.sites.length === 0 ? (
        <p className="text-sm text-fg4">Строек пока нет.</p>
      ) : (
        <ul className={`${maxHeight} overflow-y-auto -mx-1`}>
          {shown.map(s => {
            const checked = filter.favorites.has(s.id);
            return (
              <li key={s.id}>
                <label className="flex items-center gap-2 px-1 py-1 rounded hover:bg-surface-container-low cursor-pointer text-sm text-on-surface">
                  <Checkbox size="sm" checked={checked} disabled={busy}
                    onChange={() => run(() => filter.setFavorite(s.id, !checked))} />
                  <Icon icon={Star} size={18} className={checked ? 'text-primary fill-current' : 'text-fg4'} aria-hidden />
                  <span className="truncate">{s.name}</span>
                </label>
              </li>
            );
          })}
          {shown.length === 0 && <li className="px-1 py-1 text-sm text-fg4">Не найдено по «{query.trim()}»</li>}
        </ul>
      )}
      {error && <p role="alert" className="text-xs text-error">{error}</p>}
      <div className="flex items-center justify-between gap-2">
        <span className="text-xs text-fg4">
          {filter.favorites.size === 0 ? 'Избранного нет — показываются все стройки.'
            : `Отмечено: ${filter.favorites.size} из ${filter.sites.length}`}
        </span>
        {filter.favorites.size > 0 && (
          <Button variant="text" size="sm" disabled={busy} onClick={() => run(() => filter.setFavorites([]))}>
            Снять все
          </Button>
        )}
      </div>
    </div>
  );
}
