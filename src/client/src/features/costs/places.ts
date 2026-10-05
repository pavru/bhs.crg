import { LOST } from './lostReferences';
import { useCostsArticles, type CostsArticle } from '@/shared/api/articles';
import { useCostsConstructions, type AllocationPartView, type CostsConstruction } from '@/shared/api/invoices';
import type { Place } from '@/shared/api/allocationMatrix';

/**
 * Цели разноски (задача F3, issue #1087, ТЗ COST-10.1): стройка с разделом или статья вне строек.
 *
 * <p>Статья — не «ещё одна стройка», и смешивать их в одном списке без различия нельзя: «Склад» стройкой не
 * заводится, иначе всплыл бы в назначениях, сметах и комплектах. Поэтому в выборе они идут двумя группами,
 * а значение выбора помнит, что выбрано — стройка или статья.</p>
 */

export type { Place };

/** Куда можно разнести: стройки с разделами и статьи. `undefined` — ещё не загружено. */
export interface Places {
  sites: CostsConstruction[] | undefined;
  articles: CostsArticle[] | undefined;
  /** Справочник мест не прочитан — сервер отказал. Это не «ещё грузится»: само не пройдёт. */
  unread?: boolean;
}

export const NO_PLACE: Place = { construction: null, section: null, article: null };

export function usePlaces(): Places {
  const sites = useCostsConstructions();
  const articles = useCostsArticles();
  return { sites: sites.data, articles: articles.data, unread: sites.isError || articles.isError };
}

export function placeOfPart(part: AllocationPartView): Place {
  return { construction: part.constructionId, section: part.sectionId, article: part.articleId };
}

/** Ключ цели — для сравнения и группировки: у строки не бывает двух частей на одну цель. */
export function placeKey(place: Place): string {
  return place.article ? `статья:${place.article}` : `стройка:${place.construction ?? ''}/${place.section ?? ''}`;
}

export function samePlace(a: Place, b: Place): boolean {
  return placeKey(a) === placeKey(b);
}

export function chosen(place: Place): boolean {
  return !!(place.construction || place.article);
}

/** Значение пункта выбора: что выбрано — стройка или статья, — и какая. Раздел выбирают отдельно. */
export function choiceOf(place: Place): string {
  if (place.article) return `статья:${place.article}`;
  return place.construction ? `стройка:${place.construction}` : '';
}

export function fromChoice(choice: string): Place {
  const [kind, id] = choice.split(':');
  if (kind === 'статья' && id) return { ...NO_PLACE, article: id };
  if (kind === 'стройка' && id) return { ...NO_PLACE, construction: id };
  return NO_PLACE;
}

/** Название цели, пока справочник мест грузится. */
export const LOADING_PLACE = '…';

/** Название цели, когда справочник мест не пришёл: отказ обязан выглядеть отказом, а не вечной загрузкой. */
export const UNREAD_PLACE = 'справочник не прочитан';

/** Название цели — для заголовка колонки, шапки счёта и подписей. */
export function placeName(place: Place, places: Places): string {
  // Список ещё не прочитан — это не «удалена»: на медленной сети у живой стройки мелькала бы потеря.
  const pending = places.unread ? UNREAD_PLACE : LOADING_PLACE;
  if (place.article) {
    if (!places.articles) return pending;
    const article = places.articles.find(a => a.id === place.article);
    return article ? article.name : LOST.article;
  }

  if (place.construction && !places.sites) return pending;
  const site = places.sites?.find(s => s.id === place.construction);
  if (!site) return place.construction ? LOST.construction : 'объект не выбран';
  if (!place.section) return site.name;
  return `${site.name} / ${site.sections.find(s => s.id === place.section)?.name ?? LOST.section}`;
}
