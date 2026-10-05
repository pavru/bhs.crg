import { useQuery, keepPreviousData } from '@tanstack/react-query';
import { apiClient } from './client';

/** Число отчёта: сколько счетов и сколько денег. */
export interface CostFigure {
  invoices: number;
  amount: number;
}

/** Строка отчёта — стройка, статья вне строек или контрагент; `id` пуст у «поставщик не указан». */
export interface CostLine extends CostFigure {
  id: string | null;
  name: string;
}

/**
 * «Затраты по стройке» (G5, issue #1098, ТЗ COST-20). Только свёрнутые числа: оснований у отчёта нет —
 * каждое число ведёт в «Реестр счетов» с готовым отбором и равно итогу «Суммы» там.
 */
export interface SiteCosts {
  /** Выбранная стройка; null — все стройки. */
  site: CostLine | null;
  /** Учётные месяцы периода: `2026-09`. */
  from: string;
  to: string;
  withVat: boolean;
  /** Те же месяцы так, как их называет реестр («09.2026») — из них собирается отбор ссылки. */
  months: string[];
  /** Стройки и статьи вне строек — на экране всех строек; на экране стройки пусто. */
  sites: CostLine[];
  articles: CostLine[];
  /** Деньги оплаченных счетов, не лёгшие ни на один объект. */
  unallocated: CostFigure | null;
  /** Контрагенты — на экране стройки. */
  suppliers: CostLine[];
  total: CostFigure;
  /** Из затрат — счета со строками без позиции номенклатуры. */
  unmatched: CostFigure | null;
  /** Не оплачено и не отклонено: НЕ затраты и от периода не зависит. */
  payable: CostFigure;
  /** Под «без НДС» — деньги, из которых НДС вычесть нечем: учтены полной суммой. */
  vatUnknown: CostFigure | null;
}

export interface SiteCostsQuery {
  site: string | null;
  /** `2026-09`; null — текущий месяц компании, его называет сервер. */
  from: string | null;
  to: string | null;
  withVat: boolean;
}

export function useSiteCosts(query: SiteCostsQuery) {
  return useQuery({
    queryKey: ['costs-site-costs', query],
    queryFn: () => apiClient.get<SiteCosts>('/costs/site-costs', {
      params: {
        site: query.site ?? undefined, from: query.from ?? undefined, to: query.to ?? undefined,
        vat: query.withVat ? undefined : 'without',
      },
    }).then(r => r.data),
    // Смена периода не должна гасить экран: прежние числа стоят, пока не пришли новые.
    placeholderData: keepPreviousData,
  });
}
