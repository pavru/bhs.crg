import { useQuery, keepPreviousData } from '@tanstack/react-query';
import { apiClient } from './client';

/** Число отчёта: сколько счетов и сколько денег. */
export interface CostFigure {
  invoices: number;
  amount: number;
}

/**
 * Строка отчёта — стройка, статья вне строек или контрагент. `id` пуст у строки не про одну запись:
 * «поставщик не указан», удалённые объекты или поставщики одной строкой.
 */
export interface CostLine extends CostFigure {
  id: string | null;
  name: string;
  /** Есть ли у реестра отбор, под которым его итог равен этому числу; нет — стрелки у строки нет. */
  linked: boolean;
  /**
   * Как строку зовёт реестр, если иначе, чем отчёт: раздел здесь — «4 эт.», а в колонке «Раздел»
   * реестра — «Комарова 36 / 4 эт.». Отбор ссылки берёт это название.
   */
  registry?: string | null;
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
  /** Деньги на удалённых объектах — одной строкой под тем названием, каким их называет реестр. */
  lost: CostLine | null;
  /** Деньги оплаченных счетов, не лёгшие ни на один объект. */
  unallocated: CostFigure | null;
  /** Контрагенты — на экране стройки. */
  suppliers: CostLine[];
  /** Разделы стройки — второй срез ТОЙ ЖЕ суммы, что `suppliers` (G5b, issue #1198); только на экране стройки. */
  sections: CostLine[];
  total: CostFigure;
  /** Из затрат — счета со строками без позиции номенклатуры. */
  unmatched: CostFigure | null;
  /** Не оплачено и не отклонено: НЕ затраты и от периода не зависит. */
  payable: CostFigure;
  /** Под «без НДС» — деньги, из которых НДС вычесть нечем: учтены полной суммой. */
  vatUnknown: CostFigure | null;
  /** То же — в «к оплате». */
  payableVatUnknown: CostFigure | null;
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
    // Отчёт обещает равенство с реестром, а с реестра сюда возвращаются кнопкой «назад» — возможно,
    // только что оплатив счёт. Числа из кеша разошлись бы с тем, что человек видел секунду назад, так
    // что каждый показ экрана спрашивает сервер заново (ревью PR #1200).
    staleTime: 0,
    refetchOnMount: 'always',
  });
}
