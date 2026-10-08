import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { QK, type InvoiceView } from './invoices';

/**
 * Распознавание скана счёта (ТЗ COST-8, задача B1b, issue #1077): состояние, запуск, счёт из скана и
 * заведение организации, которой нет в справочнике.
 */

/** Организация справочника — совпавшая по ИНН либо та, чей ИНН прочитать не удалось. */
export interface InvoicePartyCandidate {
  id: string;
  name: string | null;
  /** Код типа записи — у подтипа (роли) свой. */
  type: string;
  archived: boolean;
  /** Запись, от которой ИНН унаследован: по нему видно, что совпадения — организация и её роли. */
  inheritedFrom: string | null;
}

/**
 * Состояние стороны — слово сервера (`InvoicePartyStates`).
 *
 * ⚠️ Неизвестное слово — не «нет»: новый сервер может прислать состояние, которого клиент не знает, и
 * показать его как «организации нет» значило бы предложить завести дубль. См. `partyNote`.
 */
export type InvoicePartyState =
  | 'matched' | 'several' | 'archived' | 'absent' | 'unknown' | 'noTaxId' | 'badTaxId' | 'unavailable';

export interface InvoiceParty {
  state: InvoicePartyState;
  /** Название из скана, как прочитано. */
  name: string | null;
  /** ИНН: проверенный — цифрами, непрочитанный — текстом из скана. */
  taxId: string | null;
  /** Почему состояние такое — словами сервера; у `matched` пусто. */
  why: string | null;
  /** Записи с таким ИНН — все: и роли найденной организации, и архивные. */
  candidates: InvoicePartyCandidate[];
  /** Записи справочника, чей ИНН прочитать не удалось, — поимённо. */
  unreadable: InvoicePartyCandidate[];
  /** Найденная организация — только у `matched`. */
  match: string | null;
}

export type InvoicePartySide = 'supplier' | 'payer';

/** Предложение по ссылочному полю: текст скана и найденная запись. */
export interface InvoicePartyOffer {
  text: string;
  entryId: string;
}

export type RecognitionState = 'none' | 'running' | 'done' | 'failed';

export interface InvoiceRecognition {
  state: RecognitionState;
  /** Вид отказа: NotConfigured, Unavailable, NoAnswer, Interrupted, Refused. */
  reason: string | null;
  /** Причина словами — текст сервера, для человека. */
  error: string | null;
  engine: string | null;
  progress: string | null;
  /** Что прочитано в шапке, как есть: «ключ профиля → текст». */
  values: Record<string, string | null> | null;
  /** Прочитано, но в поле не записано: поле было занято или значение не разобрать. */
  offers: Record<string, string | InvoicePartyOffer> | null;
  /** Распознанные строки, которые в счёт не легли: у него уже были свои. */
  lines: Record<string, string | null>[] | null;
  notes: string[];
  startedAt: string | null;
  finishedAt: string | null;
  /** Можно ли запустить сейчас — словами сервера; причина — в `whyNot`. */
  canStart: boolean;
  whyNot: string | null;
  /** Стороны из скана; есть только у `done` и только пока счёт — черновик. */
  parties: Record<InvoicePartySide, InvoiceParty | null> | null;
}

export const recognitionKey = (id: string) => [QK, id, 'recognition'] as const;

/**
 * Состояние распознавания. Опрашивается, только пока оно ИДЁТ: исход сам не меняется, а стороны
 * пересчитываются при каждом чтении — их перечитывает возврат на экран.
 *
 * @param enabled спрашивать ли: у счёта без скана распознавать нечего.
 */
export function useInvoiceRecognition(id: string, enabled: boolean) {
  return useQuery({
    queryKey: recognitionKey(id),
    queryFn: () => apiClient.get<InvoiceRecognition>(`/costs/invoices/${id}/recognition`).then(r => r.data),
    enabled,
    retry: false,
    staleTime: 0,
    refetchInterval: query => (query.state.data?.state === 'running' ? 2000 : false),
  });
}

export interface InvoiceFromScan {
  invoice: InvoiceView;
  recognition: InvoiceRecognition;
}

/**
 * Счёт из скана: черновик заводится всегда, когда файл годен, распознавание ставится в фон.
 * Ответ кладётся в кэш обоими видами — форма открывается без второго запроса и сразу знает, что скан
 * читается.
 */
export function useInvoiceFromScan() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (file: File) => {
      const form = new FormData();
      form.append('file', file);
      return apiClient.post<InvoiceFromScan>('/costs/invoices/from-scan', form).then(r => r.data);
    },
    onSuccess: ({ invoice, recognition }) => {
      qc.setQueryData([QK, invoice.id], invoice);
      qc.setQueryData(recognitionKey(invoice.id), recognition);
      void qc.invalidateQueries({ queryKey: [QK, 'list'] });
      void qc.invalidateQueries({ queryKey: [QK, 'queues'] });
    },
  });
}

/**
 * «Распознать ещё раз». Версию счёта не называет — и не должен: в счёт запуск не пишет, только ставит
 * фоновую задачу (исключение названо в CLAUDE.md).
 */
export function useStartRecognition() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      apiClient.post<InvoiceRecognition>(`/costs/invoices/${id}/recognition`).then(r => r.data),
    onSuccess: (recognition, id) => {
      qc.setQueryData(recognitionKey(id), recognition);
      void qc.invalidateQueries({ queryKey: [QK, 'list'] });
      void qc.invalidateQueries({ queryKey: [QK, 'queues'] });
    },
    // Отказ — тоже новость о состоянии: «уже распознаётся», «счёт уже не черновик».
    onError: (_error, id) => { void qc.invalidateQueries({ queryKey: recognitionKey(id) }); },
  });
}

export interface PartyOrganizationResult {
  /** Заведённая запись; `null` — её тем временем завёл кто-то ещё, сторона показывает найденное. */
  created: string | null;
  party: InvoiceParty;
}

/**
 * Завести организацию из скана в справочнике. В СЧЁТ не пишет: поставить её в поле — обычная правка
 * шапки, которую человек сохраняет сам.
 */
export function useCreatePartyOrganization() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, side, name }: { id: string; side: InvoicePartySide; name?: string }) =>
      apiClient.post<PartyOrganizationResult>(
        `/costs/invoices/${id}/recognition/parties/${side}/organization`, name ? { name } : {}).then(r => r.data),
    // И после отказа: 409 «её уже завели» значит, что сторона теперь другая.
    // ⚠️ Обещание ВОЗВРАЩАЕТСЯ: вызов дожидается перечитанного списка организаций. Заведённую ставят
    // в поле сразу, и без неё в списке выбор на кадр показал бы «запись больше не организация».
    onSettled: (_result, _error, { id }) => Promise.all([
      qc.invalidateQueries({ queryKey: recognitionKey(id) }),
      qc.invalidateQueries({ queryKey: ['costs-organizations'] }),
    ]),
  });
}
