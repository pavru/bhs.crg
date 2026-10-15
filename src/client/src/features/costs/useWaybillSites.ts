import { useCallback, useEffect, useLayoutEffect, useRef } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { onWaybillWritten, type WaybillList, type WaybillView } from '@/shared/api/waybills';
import { useSiteFilter } from '@/shared/siteFilter/siteFilterContext';
import { hiddenEmpty as hiddenEmptyOf } from '@/shared/siteFilter/siteFilterTexts';
import { useToast } from '@/shared/ui/Toast';

/**
 * Список накладных под фильтром «Избранные стройки» (issue #1222): что спросить у сервера, что сказать
 * у списка и над формой.
 *
 * <p>Отбирает СЕРВЕР — список страничный (предел 300), и урезанный на клиенте он показал бы три
 * накладные из трёхсот. Клиент решает свою половину правила: фильтр не действует (выключен, избранное
 * пусто после пересечения со справочником) — перечня нет вовсе; действует — перечень избранного, уже
 * пересечённого с живым справочником.</p>
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
  const filter = useSiteFilter(cuts);
  const toast = useToast();
  const qc = useQueryClient();
  const sites = cuts && filter.active ? [...filter.favorites] : null;
  const hides = (constructionId: string | null) => !!sites && !filter.visible(constructionId);

  // Своя запись, уведшая открытую накладную из списка: решение по прежнему и новому виду, а не по
  // списку — тот перечитывается позже, и чужая правка, пришедшая перечитыванием, «Сохранено» не скажет.
  const now = useRef({ selected, hides, setOn: filter.setOn });
  useLayoutEffect(() => { now.current = { selected, hides, setOn: filter.setOn }; });
  const onSaved = useCallback((before: WaybillView | undefined, after: WaybillView) => {
    const at = now.current;
    if (!before || after.id !== at.selected || !at.hides(after.constructionId) || at.hides(before.constructionId)) return;
    toast.info(SAVED_HIDDEN, { action: { label: 'Показать все', onClick: () => at.setOn(false) } });
  }, [toast]);
  useEffect(() => onWaybillWritten(qc, onSaved), [qc, onSaved]);

  return {
    /** Перечень для `sites`; `null` — отбора нет. */
    sites,
    /** Фильтр ещё не известен: запрос не уходит, иначе первым кадром пришло бы «всё». */
    ready: !cuts || filter.status !== 'loading',
    /** Открытая накладная скрыта в списке — форма говорит, почему её нет слева. */
    openHidden: !!open && hides(open.constructionId),
    showAll: () => filter.setOn(false),
  };
}

/**
 * Сколько скрыто в ЭТОМ ответе; `null` — отбор к ответу не применён и свидетельству говорить не о чем:
 * перечня не было, ответ — прежний список, удержанный на время переключения (`placeholderData`), или
 * сервер не нашёл в перечне ни одной живой стройки (справочник у клиента устарел) и ответил всем.
 */
export function appliedHidden(sites: readonly string[] | null, list: WaybillList | undefined, stale: boolean): number | null {
  return sites && !stale ? list?.hidden ?? null : null;
}

/** Пустой список, когда накладные есть, но скрыты фильтром: стоит ПЕРЕД «Накладных пока нет». */
export const hiddenEmpty = (hidden: number) => hiddenEmptyOf('накладных', hidden);

/**
 * Поиск по загруженному ничего не нашёл, а часть накладных скрыта. «Показанных» — нарочно: скрытых
 * сервер не прислал, и под пределом в 300 искомая избранная может просто не быть загруженной.
 */
export const SEARCH_HIDDEN = 'Среди показанных накладных избранных строек не найдено.';

/** Над формой накладной, скрытой в списке. */
export const NOT_IN_LIST = 'Этой накладной нет в списке слева: её стройка не из избранного';

/** Тост после своей записи, которая увела открытую накладную из списка. */
export const SAVED_HIDDEN = 'Сохранено. В списке накладная скрыта: её стройка не из избранного';
