import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Часовой пояс компании глазами администратора (issue #1148). Значений три, и это не избыточность:
 * «что выбрали», «что действует» и «что на сервере» расходятся, когда пояс не выбран или когда базу
 * перенесли на машину, где выбранного пояса нет.
 */
export interface CompanyTimeZoneView {
  /** Что выбрано; `null` — не выбирали, действует пояс сервера. */
  timeZoneId: string | null;
  effectiveTimeZoneId: string;
  serverTimeZoneId: string;
  /** `false` — выбранный пояс этой машине неизвестен, действует пояс сервера. */
  resolved: boolean;
  /** Смещения считает сервер по своей базе поясов — действует она, а не база браузера. */
  effectiveOffset: string;
  /** Название городом, если действующий пояс есть в списке; иначе `null`. */
  effectiveCity: string | null;
  serverOffset: string;
  /** Число и час у компании сейчас: `2026-10-13T14:32`. Этим проверяют выбор. */
  effectiveNow: string;
  /** Что предлагать: список держит сервер — пояса, которого на этой машине нет, в нём не будет. */
  timeZones: { id: string; city: string; offset: string }[];
}

export const COMPANY_TIME_ZONE_KEY = ['settings', 'company', 'time-zone'] as const;

export function useCompanyTimeZone() {
  return useQuery<CompanyTimeZoneView>({
    queryKey: COMPANY_TIME_ZONE_KEY,
    queryFn: () => apiClient.get('/settings/company').then(r => r.data),
  });
}

export function useSaveCompanyTimeZone() {
  const qc = useQueryClient();
  return useMutation({
    /** `null` — снять выбор, вернуться к поясу сервера. */
    mutationFn: (timeZoneId: string | null) => apiClient.put('/settings/company', { timeZoneId }),
    // Обновляется ВСЁ, а не одна настройка: от пояса зависит, какое сегодня число, — а значит, и
    // «Осталось дней», и «Просрочен», и всё, что сервер посчитал от «сегодня». Перечислять таких
    // запросов поимённо значило бы забыть следующий; действие редкое, цена — один круг запросов.
    onSettled: () => qc.invalidateQueries(),
  });
}
