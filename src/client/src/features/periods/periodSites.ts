import { ruPlural } from '@/shared/utils/pluralize';

/**
 * Тексты «Учётного периода» под фильтром «Избранные стройки» (issue #1220). Фильтр урезает только
 * таблицу, а закрытие компании закрывает ВСЕ стройки — и диалог обязан назвать скрытые, иначе период
 * компании выглядел бы закрытым «по трём стройкам».
 */

export interface PeriodSites {
  /** Все стройки, у которых есть граница периода. */
  total: number;
  /** Сколько из них скрыто фильтром; 0 — фильтр не действует или скрывать нечего. */
  hidden: number;
}

/** «3 из 14 строек», «1 из 21 стройки». */
export function shownSitesCount(shown: number, total: number): string {
  return `${shown} из ${total} ${ruPlural(total, 'стройки', 'строек', 'строек')}`;
}

/** «Ещё 11 строек скрыто фильтром избранного» — строка под таблицей. */
export function hiddenSitesNote(hidden: number): string {
  return `Ещё ${hidden} ${ruPlural(hidden, 'стройка', 'стройки', 'строек')} `
    + `${ruPlural(hidden, 'скрыта', 'скрыто', 'скрыто')} фильтром избранного`;
}

/** Пояснение в диалоге закрытия компании: сколько строек закроется и сколько из них не видно. */
export function companyClosingNote(sites?: PeriodSites): string {
  if (!sites) return 'Закрытие компании закрывает и все стройки.';
  const hidden = sites.hidden > 0
    ? `, в том числе ${sites.hidden}, ${ruPlural(sites.hidden, 'скрытая', 'скрытых', 'скрытых')} фильтром избранного`
    : '';
  return `Закрытие компании закрывает и все стройки — ${sites.total}${hidden}.`;
}
