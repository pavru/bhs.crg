import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
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

// ── Пакетная очистка (issue #1245) ─────────────────────────────────────────────────────────────

/** Запись, которая войдёт в пакет: разбивка та же, что у одиночного удаления. */
export interface PurgeBatchRecord {
  id: string;
  name: string;
  archived: boolean;
  references: number;
  untraceable: number;
  holders: PurgeHolder[];
}

/** Запись, которая в пакет не вошла, — с причиной словами. */
export interface PurgeBatchExcluded {
  id: string;
  name: string;
  reason: string;
}

export interface PurgeBatchPreview {
  records: PurgeBatchRecord[];
  excluded: PurgeBatchExcluded[];
  /** Общее число теряемых ссылок — его человек вводит. */
  references: number;
  untraceable: number;
  /** Сколько записей проверено: все записи типа на уровне или все названные. */
  checked: number;
  /** Сколько из названных записей уже нет. */
  missing: number;
  /** Сколько годных записей не поместилось в пакет (только у готового отбора). */
  more: number;
  limit: number;
}

/** Откуда пакет: готовый отбор (тип на уровне) или записи, названные галочками. */
export type PurgeBatchSource =
  | { typeId: string; scope: string; scopeId: string | null }
  | { ids: string[] };

export interface PurgedBatch {
  records: number;
  references: number;
  untraceable: number;
}

/**
 * Есть ли на экземпляре данные выключенных или снятых модулей. Нет — пакетной очистке нечего
 * искать, и её действий на экране нет. Спрашивается только у того, у кого есть право: без него
 * адрес закрыт.
 */
export function usePurgeBatchAvailable(enabled: boolean) {
  return useQuery({
    queryKey: ['common-data', 'purge-batch-available'],
    // `limit` — предел пакета: им владеет сервер, панели выбора он нужен до предпросмотра.
    queryFn: () => apiClient.get<{ available: boolean; limit: number }>('/common-data/purge-batch/available').then(r => r.data),
    enabled,
    staleTime: 60_000,
  });
}

export function previewPurgeBatch(source: PurgeBatchSource): Promise<PurgeBatchPreview> {
  return apiClient.post<PurgeBatchPreview>('/common-data/purge-batch/preview', source).then(r => r.data);
}

/** Свежий предпросмотр из отказа «пакет изменился» — или `null`, если отказ о другом. */
export function purgeBatchChanged(e: unknown): PurgeBatchPreview | null {
  const preview = (e as { response?: { data?: { preview?: PurgeBatchPreview | null } } })?.response?.data?.preview;
  return preview && Array.isArray(preview.records) ? preview : null;
}

export function usePurgeBatch() {
  const qc = useQueryClient();
  return useMutation({
    // Записи едут списком с числом у каждой: удаляется то, что человек видел, а не «что найдётся».
    mutationFn: (preview: PurgeBatchPreview) =>
      apiClient.post<PurgedBatch>('/common-data/purge-batch', {
        records: preview.records.map(r => ({ id: r.id, references: r.references })),
        references: preview.references,
      }).then(r => r.data),
    onSettled: () => qc.invalidateQueries({ queryKey: ['common-data'] }),
  });
}
