import { Check, Star } from 'lucide-react';
import type { TableColumn } from '@/shared/api/tables';
import { useSiteFilterState } from '@/shared/siteFilter/siteFilterContext';
import type { ShortcutFilter } from './tableShortcuts';
import { favoriteNames, namesakes, sitesChipState, sitesColumn } from './tableSites';

/**
 * Предложение «Объект: избранные стройки» над таблицей со стройками (issue #1223) — только под ВКЛЮЧЁННЫМ
 * фильтром. Сам чип отбора не ставит: ставит человек, нажатием, и дальше это обычное условие в ряду чипов.
 * Открытая под фильтром таблица показывает всё — фильтр её не урезает, поэтому и в шапке он здесь не «горит»
 * (состояние без регистрации, `useSiteFilterState`).
 */
export function SitesChip({ columns, filter, onToggle }: {
  columns: readonly TableColumn[];
  /** Отбор, который стоит на экране: по нему видно, нажат ли чип и можно ли его нажать. */
  filter: ShortcutFilter;
  onToggle: (column: string, names: string[]) => void;
}) {
  const sites = useSiteFilterState();
  const column = sitesColumn(columns);
  if (!sites.active || !column) return null;

  const names = favoriteNames(sites.sites, sites.favorites);
  const state = sitesChipState(filter, column.key, names);
  const twins = namesakes(sites.sites, sites.favorites);
  const on = state === 'on';
  const title = [
    on ? 'Нажмите, чтобы снять отбор.'
      : `Добавить отбор по избранным стройкам (${names.length}). Сама таблица фильтром не урезается.`,
    state === 'complex' && 'Отбор сложный: условие добавляется в расширенном режиме.',
    state === 'broken' && 'Отбор из адреса не применён: сначала снимите его.',
  ].filter(Boolean).join(' ');

  return (
    <div className="mb-2 flex flex-wrap items-center gap-1.5 text-xs" data-testid="sites-chip">
      <button type="button" aria-pressed={on} disabled={state === 'complex' || state === 'broken'} title={title}
        onClick={() => onToggle(column.key, names)}
        className={`inline-flex items-center gap-1 rounded-full border px-2.5 py-1 bg-surface disabled:opacity-50 ${
          on ? 'border-brand text-brand bg-brand-subtle' : 'border-dashed border-stroke text-fg3 hover:text-fg1'}`}>
        {on ? <Check size={12} aria-hidden /> : <Star size={12} aria-hidden />}
        {column.label}: избранные стройки
      </button>
      {twins.length > 0 && (
        <span className="text-fg3">
          Отбор по названию: попадут и неизбранные стройки с тем же названием — {twins.map(n => `«${n}»`).join(', ')}.
        </span>
      )}
    </div>
  );
}
