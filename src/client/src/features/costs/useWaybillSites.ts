import { onWaybillWritten, useWaybills, type WaybillList, type WaybillView } from '@/shared/api/waybills';
import { useSiteFilter } from '@/shared/siteFilter/siteFilterContext';
import { hiddenEmpty as hiddenEmptyOf } from '@/shared/siteFilter/siteFilterTexts';
import { useSavedHiddenSnackbar } from '@/shared/siteFilter/useSavedHiddenSnackbar';

/**
 * Список накладных под фильтром «Избранные стройки» (issue #1222): запрос списка, что сказать у списка
 * и над формой, тост своей записи.
 *
 * <p>Отбирает СЕРВЕР — список страничный (предел 300), и урезанный на клиенте он показал бы три
 * накладные из трёхсот. Клиент решает свою половину правила: фильтр не действует (выключен, избранное
 * пусто после пересечения со справочником) — перечня нет вовсе; действует — перечень избранного, уже
 * пересечённого с живым справочником. Пока фильтр загружается, запрос не уходит: первым кадром пришло
 * бы «всё».</p>
 *
 * <p>Очередь «Сопоставить» не урезается, как «Разобрать» у счетов (#1221): из неё накладные выходят
 * работой, а не фильтром, и накладная, которую надо свести со справочником, не прячется за звездой.</p>
 */
export function useWaybillSites({ unmatched, selected, open }: {
  unmatched: boolean;
  selected: string | null;
  /** Открытая накладная — её вид, если прочитан. */
  open: WaybillView | undefined;
}) {
  const cuts = !unmatched;
  const filter = useSiteFilter();
  const sites = cuts && filter.active ? [...filter.favorites] : null;
  const waybills = useWaybills(unmatched, sites, !cuts || filter.status !== 'loading');
  const stale = waybills.isPlaceholderData;
  const hidden = appliedHidden(sites, waybills.data, stale);

  // Скрыта ли накладная — тем знанием, каким решал СЕРВЕР, а не справочником клиента (ревью PR #1335):
  // стройку, заведённую после загрузки страницы, клиент счёл бы удалённой, а значит видимой, а сервер
  // её накладную скрыл. Вид накладной сам несёт серверное «стройки больше нет» (`constructionLost`), и
  // перечень — тот, что ушёл запросом. Отбор к ответу не применён — не скрыто ничего.
  const hides = (view: WaybillView) => hidden !== null && viewHidden(view, filter.favorites);

  const onSaved = useSavedHiddenSnackbar<WaybillView>(onWaybillWritten, {
    selected, hides, text: SAVED_HIDDEN, showAll: () => filter.setOn(false),
  });

  return {
    waybills,
    /** Список — прежний, удержанный на время переключения фильтра, а не ответ на текущий перечень. */
    stale,
    /** Сколько скрыто в этом ответе; `null` — отбор не применён, свидетельству говорить не о чем. */
    hidden,
    /** Открытая накладная скрыта в списке — форма говорит, почему её нет слева. */
    openHidden: !!open && hides(open),
    showAll: () => filter.setOn(false),
    onSaved,
  };
}

/** Накладная с такой стройкой скрыта перечнем: стройка жива по слову сервера и не в перечне. */
export function viewHidden(view: Pick<WaybillView, 'constructionId' | 'constructionLost'>, favorites: ReadonlySet<string>) {
  return !!view.constructionId && !view.constructionLost && !favorites.has(view.constructionId);
}

/**
 * Сколько скрыто в ЭТОМ ответе; `null` — отбор к ответу не применён: перечня не было, ответ — прежний
 * список, удержанный на время переключения (`placeholderData`), или сервер не нашёл в перечне ни одной
 * живой стройки (справочник у клиента устарел) и ответил всем.
 */
export function appliedHidden(sites: readonly string[] | null, list: WaybillList | undefined, stale: boolean): number | null {
  return sites && !stale ? list?.hidden ?? null : null;
}

/** Пустой список, когда накладные есть, но скрыты фильтром: стоит ПЕРЕД «Накладных пока нет». */
export const hiddenEmpty = (hidden: number) => hiddenEmptyOf('накладных', hidden);

/**
 * Поиск по загруженному ничего не нашёл, а часть накладных скрыта. О поиске, а не о стройках, и без
 * обещания найти: скрытых сервер не прислал, и нашлось бы среди них что-нибудь — неизвестно (ревью PR
 * #1335).
 */
export const SEARCH_HIDDEN = 'Ничего не найдено. Накладные неизбранных строек скрыты фильтром — среди них не искали.';

/** Над формой накладной, скрытой в списке. */
export const NOT_IN_LIST = 'Этой накладной нет в списке слева: её стройка не из избранного';

/** Тост после своей записи, которая увела открытую накладную из списка. */
export const SAVED_HIDDEN = 'Сохранено. В списке накладная скрыта: её стройка не из избранного';
