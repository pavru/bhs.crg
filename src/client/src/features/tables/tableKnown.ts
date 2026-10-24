import { useMemo } from 'react';
import { useCostsArticles } from '@/shared/api/articles';
import { useCostsConstructions } from '@/shared/api/invoices';
import type { TableColumn } from '@/shared/api/tables';
import type { ColumnKnown } from '@/shared/filter/rowFilterModel';
import { useSiteFilterState } from '@/shared/siteFilter/siteFilterContext';
import { objectKnown, sectionKnown } from '@/features/costs/registryKnown';

/**
 * Открытые перечни колонок таблицы — подсказка и сверка значения в условии отбора (UI-56, D2 change
 * `costs-filter-place-hints`). Общий отбор (`shared/filter`) о стройках и статьях не знает: перечень
 * приходит к нему полем `known` колонки, а собирается здесь.
 *
 * <p><b>По таблице, а не по одному флажку колонки.</b> Признак `sites` говорит «в клетках стройки», но не
 * говорит, что рядом с ними статьи модуля `costs`: вторая таблица со стройками (этап 3, `plan`) получит
 * `sites`, а статей у неё не будет, и модуль `costs` может быть выключен. Поэтому сборщики названы по
 * адресу таблицы, как экраны записей в `RECORD_ROUTES`. Появится вторая — решать, не пора ли перечню
 * приходить от модуля.</p>
 *
 * <p><b>Есть ли перечень, решает объявление колонки, а не загрузка.</b> Пока справочник едет, перечень
 * пуст и неполон (`complete: false`): иначе поле значения сменилось бы посреди ввода и потеряло фокус.
 * Избранное — только для порядка и только под включённым фильтром.</p>
 */
interface Sources {
  sites: Parameters<typeof objectKnown>[0];
  articles: Parameters<typeof objectKnown>[1];
  favorites: ReadonlySet<string> | null;
}

type Builders = { sites?: (s: Sources) => ColumnKnown; sections?: (s: Sources) => ColumnKnown };

const TABLE_KNOWN: Readonly<Record<string, Builders>> = {
  'costs.invoices': {
    sites: s => objectKnown(s.sites, s.articles, s.favorites),
    sections: s => sectionKnown(s.sites, s.favorites),
  },
};

/**
 * Перечни по ключу колонки; колонки без перечня в нём нет. `columns` — объявление таблицы из кэша
 * запроса: та же ссылка, пока описание не перечитали.
 */
export function useTableKnown(address: string, columns: readonly TableColumn[]): Readonly<Record<string, ColumnKnown>> {
  const builders = Object.hasOwn(TABLE_KNOWN, address) ? TABLE_KNOWN[address] : undefined;
  const wanted = useMemo(
    () => columns.flatMap(c => {
      const build = (c.sites ? builders?.sites : undefined) ?? (c.sections ? builders?.sections : undefined);
      return build ? [{ key: c.key, sites: !!c.sites, build }] : [];
    }),
    [columns, builders]);
  const sites = useCostsConstructions(wanted.length > 0);
  const articles = useCostsArticles(wanted.some(c => c.sites));
  const filter = useSiteFilterState();
  const favorites = filter.active ? filter.favorites : null;

  return useMemo(() => {
    const sources: Sources = { sites: sites.data, articles: articles.data, favorites };
    return Object.fromEntries(wanted.map(c => [c.key, c.build(sources)]));
  }, [wanted, sites.data, articles.data, favorites]);
}
