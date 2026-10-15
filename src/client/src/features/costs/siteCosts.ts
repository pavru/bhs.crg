import { formatCount } from '@/shared/format/format';
import type { FilterCondition, FilterGroup } from '@/shared/api/types';
import type { CostLine, SiteCosts } from '@/shared/api/siteCosts';
import { ruCount } from '@/shared/utils/pluralize';

/**
 * Ссылки отчёта «Затраты по стройке» в «Реестр счетов» (G5, issue #1098).
 *
 * У отчёта нет своих списков оснований: каждое число ведёт в реестр с готовым отбором, и число
 * отчёта обязано равняться итогу «Суммы» там — так цифры сверяются глазами. Поэтому отбор ссылки
 * собирается ТЕМИ ЖЕ словами, какими сервер отбирает счета отчёта: учётные месяцы периода, объект,
 * «не отклонён». Разойдись они — число и итог разойдутся молча; это держит серверный тест, который
 * сверяет отчёт с реестром под этим же отбором.
 *
 * ⚠️ Отбор реестра идёт по НАЗВАНИЮ. Две стройки или два поставщика с одним названием дадут ссылку,
 * которая покажет больше, чем строка отчёта.
 */
export const REGISTRY = '/tables/costs.invoices/registry';

const is = (column: string, value: string): FilterCondition => ({ type: 'condition', column, op: 'eq', value });

/** Отклонённые счета не входят ни в затраты, ни в «к оплате» — и в ссылке их тоже нет. */
const NOT_REJECTED: FilterCondition = { type: 'condition', column: 'Состояние', op: 'neq', value: 'Отклонён' };

const period = (months: string[]): FilterCondition =>
  ({ type: 'condition', column: 'УчётныйПериод', op: 'in', values: months });

/** Адрес реестра под отбором: все условия разом. */
export function registryLink(conditions: FilterCondition[]): string {
  const filter: FilterGroup = { type: 'group', logic: 'and', children: conditions };
  return `${REGISTRY}#filter=${encodeURIComponent(JSON.stringify(filter))}`;
}

type Scope = Pick<SiteCosts, 'months' | 'site'>;

/** Затраты периода — у стройки ещё и её объект: под ним «Сумма» реестра становится долей. */
function costs(report: Scope, ...more: FilterCondition[]): FilterCondition[] {
  return [
    ...(report.site ? [is('ОбъектыРазноски', report.site.name)] : []),
    period(report.months), ...more, NOT_REJECTED,
  ];
}

export const siteCostsLinks = {
  /** Итог затрат. */
  total: (report: Scope) => registryLink(costs(report)),
  /** Стройка или статья вне строек — на экране всех строек. */
  object: (report: Scope, name: string) =>
    registryLink([is('ОбъектыРазноски', name), period(report.months), NOT_REJECTED]),
  /** Контрагент — на экране стройки; без названия — «поставщик не указан». */
  supplier: (report: Scope, name: string | null) => registryLink(costs(report,
    name === null ? { type: 'condition', column: 'Поставщик', op: 'is_empty' } : is('Поставщик', name))),
  /**
   * Раздел стройки — на её экране (G5b, issue #1198). Название — то, каким раздел зовёт РЕЕСТР: вместе со
   * стройкой. Одинокое «4 эт.» нашло бы одноимённые разделы всех строек.
   */
  section: (report: Scope, registry: string) => registryLink(costs(report, is('РазделыРазноски', registry))),
  /** Из затрат — счета со строками без позиции номенклатуры. */
  unmatched: (report: Scope) =>
    registryLink(costs(report, { type: 'condition', column: 'СтрокБезПозиции', op: 'is_not_empty' })),
  /** «К оплате» — от периода не зависит: неоплаченный счёт не принадлежит ни одному. */
  payable: (report: Scope) => registryLink([
    ...(report.site ? [is('ОбъектыРазноски', report.site.name)] : []),
    is('СостояниеОплаты', 'Не оплачен'), NOT_REJECTED,
  ]),
};

/** «3 счёта», «1 счёт», «5 счетов». */
export function invoicesText(count: number): string {
  const tens = count % 100, ones = count % 10;
  const word = tens >= 11 && tens <= 14 ? 'счетов' : ones === 1 ? 'счёт' : ones >= 2 && ones <= 4 ? 'счёта' : 'счетов';
  return `${formatCount(count)} ${word}`;
}

/** Стройки, свёрнутые фильтром «Избранные стройки» в одну строку отчёта (issue #1223). */
export interface FoldedSites {
  /** Сколько строк свёрнуто. */
  count: number;
  /** Их деньги вместе. Числа счетов нет: счёт на двух стройках стоит в обеих строках и в сумму не складывается. */
  amount: number;
}

/**
 * Строки строек под фильтром: видимые — как есть, остальные — одной суммой (issue #1223, решение владельца
 * «фильтр прячет строки, но не меняет ни одного числа»). Свёрнутая строка доводит столбец до «Итого»: итог
 * не пересчитывается и не переподписывается, а без неё сумма строк была бы меньше итога и не говорила бы
 * почему.
 *
 * <p>Видимость решает общее правило (`visible`): стройка, которой нет в справочнике клиента, видна —
 * как в списках. Деньги складываются в копейках: дробная сумма с плавающей точкой разошлась бы с итогом
 * сервера в последнем знаке.</p>
 *
 * @returns `folded: null` — свёрнуто ничего: фильтр не действует или все стройки отчёта избранные
 */
export function foldSites(sites: readonly CostLine[], visible: (id: string | null) => boolean) {
  const shown = sites.filter(s => visible(s.id));
  const hidden = sites.filter(s => !visible(s.id));
  const folded: FoldedSites | null = hidden.length === 0 ? null : {
    count: hidden.length,
    amount: hidden.reduce((sum, s) => sum + Math.round(s.amount * 100), 0) / 100,
  };
  return { shown, folded };
}

/** «Скрыто фильтром: 11 строек» — название свёрнутой строки. */
export function foldedText(count: number): string {
  return `Скрыто фильтром: ${ruCount(count, 'стройка', 'стройки', 'строек')}`;
}
