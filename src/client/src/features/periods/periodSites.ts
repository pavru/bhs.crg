import { ruPlural } from '@/shared/utils/pluralize';

/**
 * Тексты «Учётного периода» под фильтром «Избранные стройки» (issue #1220). Фильтр урезает только
 * таблицу, а закрытие компании и его отмена касаются ВСЕХ строек — и диалоги обязаны назвать скрытые,
 * иначе период компании выглядел бы закрытым (или открытым) «по трём стройкам».
 */

export interface PeriodSites {
  /** Стройки, которых касается операция. */
  total: number;
  /** Сколько из них скрыто фильтром; 0 — фильтр не действует или скрывать нечего. */
  hidden: number;
}

/** «3 из 14 строек», «1 из 21 стройки». */
export function shownSitesCount(shown: number, total: number): string {
  return `${shown} из ${total} ${ruPlural(total, 'стройки', 'строек', 'строек')}`;
}

/** «— 14, в том числе 11, скрытых фильтром избранного»; строек нет — пусто: «— 0» ничего не говорит. */
function counted(sites?: PeriodSites): string {
  if (!sites?.total) return '';
  const hidden = sites.hidden > 0
    ? `, в том числе ${sites.hidden}, ${ruPlural(sites.hidden, 'скрытая', 'скрытых', 'скрытых')} фильтром избранного`
    : '';
  return ` — ${sites.total}${hidden}`;
}

/** Пояснение в диалоге закрытия компании: сколько строек закроется и сколько из них не видно. */
export function companyClosingNote(sites?: PeriodSites): string {
  return `Закрытие компании закрывает и все стройки${counted(sites)}.`;
}

/**
 * Пояснение в диалоге отмены закрытия компании. Считаются стройки, которым отмена откроет дни, —
 * закрытые своим закрытием названы в диалоге отдельно.
 */
export function companyReopenNote(sites: PeriodSites): string | null {
  return sites.total ? `Дни откроются и у строек${counted(sites)}.` : null;
}
