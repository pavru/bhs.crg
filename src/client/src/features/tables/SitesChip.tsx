import { useMemo } from 'react';
import { Star } from 'lucide-react';
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
 * <p>Тёзки ищутся и среди статей вне строек: колонка со стройками сейчас одна — «Объект» реестра счетов, и
 * прочие объекты в ней — статьи (ревью PR #1339). Справочник статей не прочитан (нет права, сбой) — об этом
 * сказано: молчание читалось бы как «тёзок нет».</p>
 */
export function SitesChip({ columns, filter, onToggle }: {
  columns: readonly TableColumn[];
  /** Отбор, который стоит на экране: по нему видно, нажат ли чип и можно ли его нажать. */
  filter: ShortcutFilter;
  onToggle: (column: string, names: string[]) => void;
}) {
  const sites = useSiteFilterState();
  const column = sitesColumn(columns);
  const offered = sites.active && !!column;
  const articles = useCostsArticles(offered);
  const others = useMemo(() => (articles.data ?? []).map(a => a.name), [articles.data]);
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
    <div className="mb-2 flex flex-wrap items-center gap-1.5 text-xs" data-testid="sites-chip">
      <Chip selected={on} icon={Star} dashed disabled={state === 'complex' || state === 'broken'} title={title}
        onClick={() => onToggle(column.key, names)}>
        {column.label}: избранные стройки
      </Chip>
      {twins.length > 0 ? (
        <span className="text-fg3">
          Отбор по названию: попадут и другие объекты с тем же названием — {twins.map(n => `«${n}»`).join(', ')}.
        </span>
      ) : articles.isError && (
        <span className="text-fg3">Отбор по названию: статьи вне строек с тем же названием не проверены.</span>
      )}
    </div>
  );
}
