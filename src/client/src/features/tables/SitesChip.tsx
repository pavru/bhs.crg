import { useMemo } from 'react';
import { Star } from '@/shared/ui/icons';
import { Chip } from '@/shared/ui/Chip';
import { useCostsArticles } from '@/shared/api/articles';
import type { TableColumn } from '@/shared/api/tables';
import { useSiteFilterState } from '@/shared/siteFilter/siteFilterContext';
import type { ShortcutFilter } from './tableShortcuts';
import { favoriteNames, namesakes, replacesOthers, sitesChipState, sitesColumn } from './tableSites';

/**
 * Предложение «Объект: избранные стройки» над таблицей со стройками (issue #1223) — только под ВКЛЮЧЁННЫМ
 * фильтром. Сам чип отбора не ставит: ставит человек, нажатием, и дальше это обычное условие в ряду чипов.
 * Открытая под фильтром таблица показывает всё — фильтр её не урезает, поэтому отбора «Стройки» (#1432) над ней нет:
 * здесь только состояние фильтра, `useSiteFilterState`.
 *
 * <p>В реестре счетов тёзки ищутся и среди статей вне строек (ревью PR #1339).
 * Если справочник статей недоступен, чип показывает оговорку.</p>
 */
export function SitesChip({ columns, filter, checkArticles, canReadArticles, onToggle }: {
  columns: readonly TableColumn[];
  /** Отбор, который стоит на экране: по нему видно, нажат ли чип и можно ли его нажать. */
  filter: ShortcutFilter;
  checkArticles: boolean;
  canReadArticles: boolean;
  onToggle: (column: string, names: string[]) => void;
}) {
  const sites = useSiteFilterState();
  const column = sitesColumn(columns);
  const offered = sites.active && !!column;
  const articles = useCostsArticles(offered && checkArticles && canReadArticles);
  const visibleArticles = checkArticles && canReadArticles ? articles.data : undefined;
  const others = useMemo(() => (visibleArticles ?? []).map(a => a.name), [visibleArticles]);
  const names = useMemo(() => favoriteNames(sites.sites, sites.favorites), [sites.sites, sites.favorites]);
  const twins = useMemo(() => namesakes(sites.sites, sites.favorites, names, others),
    [sites.sites, sites.favorites, names, others]);
  if (!offered) return null;

  const state = sitesChipState(filter, column.key, names);
  const on = state === 'on';
  const title = [
    on ? 'Нажмите, чтобы снять отбор.'
      : `Добавить отбор по избранным стройкам (${names.length}). Сама таблица фильтром не урезается.`,
    !on && replacesOthers(filter, column.key, names) && `Прочие условия по колонке «${column.label}» будут заменены.`,
    state === 'complex' && 'Отбор сложный: условие добавляется в расширенном режиме.',
    state === 'broken' && 'Отбор из адреса не применён: сначала снимите его.',
  ].filter(Boolean).join(' ');

  return (
    <div className="mb-2 flex flex-wrap items-center gap-x-1.5 gap-y-2 text-body-small" data-testid="sites-chip">
      {/* Filter chip без пунктира ([UI-68]): это переключатель, а assist нажатым не бывает. */}
      <Chip selected={on} icon={Star} disabled={state === 'complex' || state === 'broken'} title={title}
        onClick={() => onToggle(column.key, names)}>
        {column.label}: избранные стройки
      </Chip>
      {twins.length > 0 ? (
        <span className="text-on-surface-variant">
          Отбор по названию: попадут и другие объекты с тем же названием — {twins.map(n => `«${n}»`).join(', ')}.
        </span>
      ) : null}
      {checkArticles && (!canReadArticles || articles.isError) && (
        <span className="text-on-surface-variant">Отбор по названию: статьи вне строек с тем же названием не проверены.</span>
      )}
    </div>
  );
}
