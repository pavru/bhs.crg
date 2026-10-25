import { withArchiveWord } from '@/shared/ui/archive';
import { revealText, type SiteChoices } from '@/shared/siteFilter/siteChoices';
import type { CostsConstruction } from '@/shared/api/invoices';
import { ruCount } from '@/shared/utils/pluralize';
import { headerPlace, type HeaderObject } from './matrix';
import { OUTSIDE_SITES, missingSection, placeName, type Place, type Places } from './places';
import { sectionLabel } from './registryWords';

/**
 * Пункты и слова поля «стройка / раздел» в шапке счёта (вариант Б, COST-12.1) — без React, чтобы правило
 * списка и подписи проверялись без отрисовки.
 *
 * <p>Группы строек — те же, что у `PlaceSelect`: их строит `siteChoices` (фильтр «Избранные стройки»,
 * «Не в избранном», выход последним). Своего правила группировки здесь нет — только раздел вторым шагом и
 * поиск по обеим частям подписи.</p>
 */

export interface PlaceRow {
  /** Ключ пункта: место целиком (у раздела — со стройкой). */
  key: string;
  place: Place;
  label: string;
  /** Сколько разделов у стройки; больше нуля — у пункта есть «›» на второй шаг. */
  sections: number;
}

export interface PlaceGroup { label: string; rows: PlaceRow[] }

export interface PlaceList {
  /** Стоящая цель, которой нет в справочнике (или он ещё грузится), — первым пунктом, без группы. */
  lost: PlaceRow | null;
  groups: PlaceGroup[];
  /** Текст выхода «Показать все стройки»; `null` — выхода нет. */
  reveal: string | null;
}

/** Пункт «вся стройка» на втором шаге. */
export const WHOLE_SITE = '— вся стройка —';

/** Группа найденных разделов. */
export const FOUND_SECTIONS = 'Разделы';

const siteRow = (site: CostsConstruction): PlaceRow => ({
  key: site.id, place: { construction: site.id, section: null, article: null }, label: site.name,
  sections: site.sections.length,
});

const sectionRow = (site: CostsConstruction, id: string, name: string, label = name): PlaceRow => ({
  key: `${site.id}/${id}`, place: { construction: site.id, section: id, article: null }, label, sections: 0,
});

function articleRows(places: Places, current: Place | null, match: (label: string) => boolean = () => true): PlaceRow[] {
  return (places.articles ?? [])
    .filter(a => !a.archived || a.id === current?.article)
    .map(a => ({
      key: a.id, place: { construction: null, section: null, article: a.id }, label: withArchiveWord(a.name, a.archived),
      sections: 0,
    }))
    .filter(row => match(row.label));
}

/**
 * Стоящая цель, которой нет в списке, — пунктом с честной подписью (как у `PlaceSelect`): без него поле
 * показало бы «— выберите —», хотя деньги лежат на удалённой стройке. Список не пришёл — «…», а не «удалена».
 */
function lostRow(current: Place | null, places: Places): PlaceRow | null {
  if (!current || (!current.construction && !current.article)) return null;
  const list = current.article ? places.articles : places.sites;
  const id = current.article ?? current.construction;
  if (list?.some(item => item.id === id)) return null;
  const place = { ...current, section: null };
  return { key: `lost:${id}`, place, label: placeName(place, places), sections: 0 };
}

/** Шаг 1: стройки группами фильтра, затем статьи вне строек, выход — последним. */
export function firstStep(places: Places, choices: SiteChoices<CostsConstruction>, current: Place | null): PlaceList {
  const groups = choices.groups.map(g => ({ label: g.label, rows: g.sites.map(siteRow) }));
  const articles = articleRows(places, current);
  if (articles.length) groups.push({ label: OUTSIDE_SITES, rows: articles });
  return { lost: lostRow(current, places), groups, reveal: choices.hidden > 0 ? revealText(choices.hidden) : null };
}

/**
 * Шаг 2: «вся стройка» первым, разделы — в порядке справочника, как в матрице и строке разноски. Стоящий
 * раздел, которого у стройки нет, — пунктом с подписью `missingSection`.
 */
export function sectionStep(site: CostsConstruction, places: Places, current: Place | null): PlaceList {
  const rows = [{ ...siteRow(site), label: WHOLE_SITE, sections: 0 }];
  const stale = current?.construction === site.id && current.section !== null
    && !site.sections.some(s => s.id === current.section);
  if (stale) rows.push(sectionRow(site, current.section!, missingSection(current.section!, places)));
  rows.push(...site.sections.map(s => sectionRow(site, s.id, s.name)));
  return { lost: null, groups: [{ label: site.name, rows }], reveal: null };
}

/**
 * Поиск по обеим частям: каждое слово набора — подстрока подписи «стройка / раздел» («4 эт» находит
 * «Комарова 36 / 4 эт.», «ком 4» — тоже). Ищет по ВСЕМ стройкам, и спрятанным фильтром: найти набором и
 * не увидеть — хуже, чем увидеть лишнее.
 */
export function searchStep(query: string, places: Places, current: Place | null): PlaceList {
  const words = query.toLocaleLowerCase('ru').split(/\s+/).filter(Boolean);
  const match = (label: string) => words.every(w => label.toLocaleLowerCase('ru').includes(w));
  const sites = places.sites ?? [];
  const groups: PlaceGroup[] = [
    { label: 'Стройки', rows: sites.filter(s => match(s.name)).map(siteRow) },
    {
      label: FOUND_SECTIONS,
      rows: sites.flatMap(site => site.sections
        .map(s => sectionRow(site, s.id, s.name, sectionLabel(site.name, s.name)))
        .filter(row => match(row.label))),
    },
    { label: OUTSIDE_SITES, rows: articleRows(places, current, match) },
  ];
  return { lost: null, groups: groups.filter(g => g.rows.length > 0), reveal: null };
}

/** Что показывает закрытое поле. `muted` — значения нет или оно неточно («несколько разделов»). */
export function fieldText(current: HeaderObject, places: Places): { text: string; muted: boolean } {
  if (current.kind === 'none') return { text: '— выберите —', muted: true };
  if (current.kind === 'many') return { text: '— несколько объектов —', muted: true };
  const at = headerPlace(current);
  if (at === null) return { text: `${placeName(current.place, places)} · несколько разделов`, muted: true };
  return { text: placeName(at, places), muted: false };
}

/**
 * Состояние разноски словами (D5). Несколько разделов одной стройки — законное состояние, а не недоделка:
 * тоном предупреждения окрашены только «не разнесён», «не полностью» и несколько объектов.
 */
export function statusOf(current: HeaderObject): { status: string; calm: boolean } {
  if (current.kind === 'none') return { status: 'не разнесён', calm: false };
  if (current.kind === 'many') return { status: `разнесён на ${ruCount(current.count, 'объект', 'объекта', 'объектов')}`, calm: false };
  if (!current.complete) return { status: 'разнесено не полностью', calm: false };
  if (current.section === 'many')
    return {
      status: `разнесён на ${ruCount(current.sections, 'раздел', 'раздела', 'разделов')}${current.bare ? ' и без раздела' : ''}`,
      calm: true,
    };
  return { status: 'весь счёт на этот объект', calm: true };
}
