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
  serverOffset: string;
  /** Число и час у компании на момент ответа: `2026-10-13T14:32`. Этим проверяют выбор. */
  effectiveNow: string;
  /** Что предлагать: список держит сервер — пояса, которого на этой машине нет, в нём не будет. */
  timeZones: { id: string; city: string; offset: string }[];
}

export const COMPANY_TIME_ZONE_KEY = ['settings', 'company', 'time-zone'] as const;

export function useCompanyTimeZone() {
  return useQuery<CompanyTimeZoneView>({
    queryKey: COMPANY_TIME_ZONE_KEY,
    queryFn: () => apiClient.get('/settings/company').then(r => r.data),
    // Ответ несёт «сейчас у компании», и экран так его и подписывает: значение из кэша или
    // получасовой давности было бы неправдой, а на границе суток — неверным числом, по которому
    // проверяют выбор пояса (ревью PR #1314). Перечитываем при каждом показе и раз в полминуты.
    staleTime: 0,
    refetchOnMount: 'always',
    refetchInterval: 30_000,
  });
}

export function useSaveCompanyTimeZone() {
  const qc = useQueryClient();
  return useMutation({
    /** `null` — снять выбор, вернуться к поясу сервера. */
    mutationFn: (timeZoneId: string | null) => apiClient.put('/settings/company', { timeZoneId }),
    // После успеха обновляется ВСЁ, а не одна настройка: от пояса зависит, какое сегодня число, —
    // а значит, и «Осталось дней», и «Просрочен», и всё, что сервер посчитал от «сегодня».
    // Перечислять такие запросы поимённо значило бы забыть следующий; действие редкое.
    // Круг запросов НЕ ждём: сохранение считается завершённым по ответу сервера, а не когда
    // перечитается последний список оболочки. Сама настройка — исключение: её ждём, иначе поле на
    // миг показало бы прежний пояс.
    onSuccess: () => {
      void qc.invalidateQueries();
      return qc.invalidateQueries({ queryKey: COMPANY_TIME_ZONE_KEY });
    },
    // После отказа пояс не изменился — перечитывать приложение незачем, только саму настройку.
    onError: () => qc.invalidateQueries({ queryKey: COMPANY_TIME_ZONE_KEY }),
  });
}
