import type { CostsArticle } from '@/shared/api/articles';
import type { CostsConstruction } from '@/shared/api/invoices';
import type { ColumnKnown, KnownExplain } from '@/shared/filter/rowFilterModel';
import { GROUP_FAVORITES, GROUP_OTHERS } from '@/shared/siteFilter/siteChoices';
import { withArchiveWord } from '@/shared/ui/archive';
import type { SuggestItem } from '@/shared/ui/suggestMatch';
import { REGISTRY_WORDS, sectionLabel } from './registryWords';

/**
 * Перечни колонок «Объект» и «Раздел» реестра счетов для подсказки в условии отбора (COST-20.1, D1 change
 * `costs-filter-place-hints`). Правила — чистые; хук, который кормит их справочниками, — `tableKnown.ts`.
 *
 * <p><b>Перечень — то, по чему сервер находит счета</b> (`InvoiceShares`): стройки и разделы из того же
 * порта, что `/costs/constructions`, статьи — со всеми архивными, слова потерянных ссылок — из
 * `registryWords.ts`. Иначе подсказка предлагала бы то, под что не найдётся ни одного счёта, а пометка
 * «нет в справочнике» молчала бы о том, о чём должна сказать.</p>
 *
 * <p>Порядок — как в выборе места разноски: под включённым фильтром «Избранные стройки» избранные идут
 * первыми своей группой, остальные — группой «Остальные стройки»; выключен — по алфавиту, без подписей.
 * Урезать перечень фильтр не может: отбор — условие, а не список.</p>
 */

export const GROUP_OUTSIDE = 'Вне строек';
/** Приписка стройки, у которой есть тёзка-статья: под условием по названию попадут обе. */
export const TWIN_NOTE = 'и статья вне строек';

const byName = (a: string, b: string) => a.localeCompare(b, 'ru');

/**
 * Стройки в порядке перечня — с подписью группы. `favorites` — избранное под ВКЛЮЧЁННЫМ фильтром; `null`
 * — фильтр не действует. Избранные узнаются по идентификатору: тёзку избранной стройки избранной не
 * считаем.
 */
function orderedSites(sites: readonly CostsConstruction[], favorites: ReadonlySet<string> | null) {
  const sorted = [...sites].sort((a, b) => byName(a.name, b.name));
  if (!favorites || !sorted.some(s => favorites.has(s.id))) return sorted.map(site => ({ site, group: undefined }));
  return [
    ...sorted.filter(s => favorites.has(s.id)).map(site => ({ site, group: GROUP_FAVORITES })),
    ...sorted.filter(s => !favorites.has(s.id)).map(site => ({ site, group: GROUP_OTHERS })),
  ];
}

/** Каждое значение — один пункт: одинаковые подписи схлопываются, первый по порядку остаётся. */
function unique(items: SuggestItem[]): SuggestItem[] {
  const seen = new Set<string>();
  return items.filter(i => !seen.has(i.value) && !!seen.add(i.value));
}

/**
 * Перечень «Объекта»: стройки, затем «Вне строек» — действующие статьи и архивные со словом «в архиве»,
 * последним — «объект удалён». Стройка и статья с одним названием — один пункт стройки с припиской.
 *
 * @param articles `undefined` — справочник статей не пришёл: перечень неполон, пометка молчит.
 */
export function objectKnown(
  sites: readonly CostsConstruction[] | undefined, articles: readonly CostsArticle[] | undefined,
  favorites: ReadonlySet<string> | null,
): ColumnKnown {
  const articleNames = new Set((articles ?? []).map(a => a.name));
  const siteNames = new Set((sites ?? []).map(s => s.name));
  const outside = [...(articles ?? [])].filter(a => !siteNames.has(a.name))
    .sort((a, b) => Number(a.archived) - Number(b.archived) || byName(a.name, b.name));

  const items = unique([
    ...orderedSites(sites ?? [], favorites).map(({ site, group }): SuggestItem => ({
      value: site.name, group, ...(articleNames.has(site.name) ? { note: TWIN_NOTE } : {}),
    })),
    ...outside.map((a): SuggestItem => ({
      value: a.name, group: GROUP_OUTSIDE, ...(a.archived ? { label: withArchiveWord(a.name, true) } : {}),
    })),
    { value: REGISTRY_WORDS.lostObject },
  ]);
  return { items, complete: !!sites && !!articles };
}

/**
 * Перечень «Раздела»: по стройке — её разделы по алфавиту, затем «стройка / без раздела»; последним —
 * «раздел удалён». Статей нет: у доли на статью раздела не бывает. Голое название стройки — не значение
 * колонки, а частая ошибка: о нём перечень знает пояснение и два действия на выбор (`explain`).
 *
 * @param sites `undefined` — справочник строек ещё не пришёл (или не пришёл вовсе).
 */
export function sectionKnown(sites: readonly CostsConstruction[] | undefined, favorites: ReadonlySet<string> | null): ColumnKnown {
  const ordered = orderedSites(sites ?? [], favorites);
  const items = unique([
    ...ordered.flatMap(({ site, group }) => [
      ...[...site.sections].sort((a, b) => byName(a.name, b.name))
        .map((section): SuggestItem => ({ value: sectionLabel(site.name, section.name), group })),
      { value: sectionLabel(site.name, REGISTRY_WORDS.noSection), group },
    ]),
    { value: REGISTRY_WORDS.lostSection },
  ]);
  const names = [...new Set(ordered.map(({ site }) => site.name))];
  return { items, complete: !!sites, explain: names.map(siteExplain) };
}

/**
 * Пояснение к голому названию стройки в «Разделе». «Все разделы стройки» — «начинается с» и название с
 * косой чертой: без неё «Комарова 36» отобрало бы и разделы «Комарова 36А».
 */
export function siteExplain(name: string): KnownExplain {
  return {
    value: name,
    text: `это стройка, а не раздел — разделы пишутся «${sectionLabel(name, '…')}»`,
    actions: [
      { label: 'Все разделы стройки', op: 'starts_with', value: `${name} /` },
      { label: 'Без раздела', op: 'eq', value: sectionLabel(name, REGISTRY_WORDS.noSection) },
    ],
  };
}
