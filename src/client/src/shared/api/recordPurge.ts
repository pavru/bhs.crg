import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Принудительное удаление записи справочника (issue #1187): её держат только данные выключенного
 * или снятого модуля, и убрать ссылку негде. Ссылки при этом теряются — поэтому свой адрес, своё
 * право и подтверждение числом.
 */

/** Строка разбивки: чьи данные, что именно и сколько строк. */
export interface PurgeHolder {
  owner: string;
  what: string;
  rows: number;
  /** Названия документов-держателей — если сервер счёл, что спрашивающему их видеть можно. */
  documents: string | null;
  /** false — после удаления эту потерю не покажет никто: модуля нет или колонку он не объявил. */
  traceable: boolean;
}

export interface PurgeOffer {
  /** Есть ли у человека право. Без него разбивка не приходит: `holders` пуст. */
  allowed: boolean;
  /** Сколько ссылок будет потеряно — это число человек вводит. */
  references: number;
  untraceable: number;
  holders: PurgeHolder[];
}

export interface PurgedRecord {
  id: string;
  name: string;
  references: number;
  untraceable: number;
}

/**
 * Предлагает ли отказ удаления принудительный выход. Читаем ПОЛЕ ответа, а не слова причины — как
 * и `archiveOffered`: текст сервер волен переписать. `null` — выхода нет: запись держит включённый
 * модуль или само ядро, либо держателей проверить не удалось.
 */
export function purgeOffered(e: unknown): PurgeOffer | null {
  const purge = (e as { response?: { data?: { purge?: Partial<PurgeOffer> | null } } })?.response?.data?.purge;
  if (!purge || typeof purge.references !== 'number') return null;
  return {
    allowed: purge.allowed === true,
    references: purge.references,
    untraceable: purge.untraceable ?? 0,
    holders: purge.holders ?? [],
  };
}

export function usePurgeRecord() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, references }: { id: string; references: number }) =>
      apiClient.post<PurgedRecord>(`/common-data/${id}/purge`, { references }).then(r => r.data),
    // Список сбрасывается и после отказа: «число не совпало» значит, что вокруг записи что-то
    // изменилось, а «не найдено» — что её удалили без нас.
    onSettled: () => qc.invalidateQueries({ queryKey: ['common-data'] }),
  });
}
