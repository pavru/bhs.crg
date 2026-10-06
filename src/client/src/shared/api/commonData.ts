import { useMemo, useState } from 'react';
import { keepPreviousData, useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { CatalogScope, CommonDataEntry, CommonDataEntryWithScope, RecordsPurpose } from './types';

const QK = 'common-data';

/**
 * Записи одного уровня. `purpose` обязателен (issue #1185): назначение входит и в ключ кэша —
 * иначе список «на выбор» и список «на показ» делили бы одну запись кэша, и архивная запись
 * попадала бы в выбор из чужого ответа.
 */
export function useListCommonData(params: {
  purpose: RecordsPurpose;
  scope?: CatalogScope;
  scopeId?: string;
  typeId?: string;
  enabled?: boolean;
}) {
  return useQuery({
    queryKey: [QK, { purpose: params.purpose, scope: params.scope, scopeId: params.scopeId, typeId: params.typeId }],
    queryFn: () =>
      apiClient
        .get<CommonDataEntry[]>('/common-data', { params: {
          purpose: params.purpose,
          scope: params.scope,
          scopeId: params.scopeId,
          typeId: params.typeId,
        }})
        .then(r => r.data),
    enabled: params.enabled !== false,
  });
}

export function useCommonDataForSet({
  setId,
  purpose,
  typeId,
  enabled = true,
}: {
  setId: string | undefined;
  /** Зачем список — см. {@link RecordsPurpose}. Обязателен: умолчания у этого решения нет. */
  purpose: RecordsPurpose;
  typeId?: string;
  enabled?: boolean;
}) {
  return useQuery({
    queryKey: [QK, 'for-set', setId, typeId ?? null, purpose],
    queryFn: () =>
      apiClient
        .get<CommonDataEntryWithScope[]>(`/common-data/for-set/${setId}`, {
          params: { purpose, typeId },
        })
        .then(r => r.data),
    enabled: enabled && !!setId,
  });
}

/**
 * Записи, видимые из ЛЮБОГО скопа (issue #82): резолвит родительскую цепочку
 * (Раздел→Стройка→Система и т.д.) — в отличие от useCommonDataForSet, который стартует с комплекта.
 */
export function useCommonDataForScope({
  scope,
  scopeId,
  purpose,
  typeId,
  archivedOnly = false,
  enabled = true,
}: {
  scope: CatalogScope | undefined;
  scopeId?: string | null;
  /** Зачем список — см. {@link RecordsPurpose}. Обязателен: умолчания у этого решения нет. */
  purpose: RecordsPurpose;
  typeId?: string;
  /** Только архивные записи (раздел «В архиве» окна выбора). Сервер принимает лишь с `display`. */
  archivedOnly?: boolean;
  enabled?: boolean;
}) {
  return useQuery({
    queryKey: [QK, 'for-scope', scope ?? null, scopeId ?? null, typeId ?? null, purpose, archivedOnly],
    queryFn: () =>
      apiClient
        .get<CommonDataEntryWithScope[]>('/common-data/for-scope', {
          params: { scope, scopeId: scopeId ?? undefined, typeId, purpose, only: archivedOnly ? 'archived' : undefined },
        })
        .then(r => r.data),
    enabled: enabled && !!scope,
  });
}

/** Одна запись каталога по id — для показа резолвнутой $ref-ссылки в связанном поле (issue #99). */
/**
 * @param forEdit запись читают, чтобы открыть на ней форму правки (issue #1214): копия из кэша не
 *   годится, и чтение идёт заново при каждом открытии — см. `EditEntryForm`.
 */
export function useCommonDataEntry(id: string | undefined, forEdit = false) {
  return useQuery({
    queryKey: [QK, 'by-id', id],
    queryFn: () => apiClient.get<CommonDataEntry>(`/common-data/${id}`).then(r => r.data),
    enabled: !!id,
    staleTime: 60_000,
    refetchOnMount: forEdit ? 'always' : true,
  });
}

/** Проверка связок (issue #99): статус каждого @@ref-поля. */
export interface BindingCheckItem {
  fieldKey: string;
  fieldTitle: string;
  /** error (issue #715) — резолв привязки не состоялся: источник недоступен либо материализация без маппинга. */
  /**
   * `archived` — цель в архиве (issue #1185), связка стоит и работает: чинить нечего.
   * `archived-skipped` — источник называет архивную запись, которой в поле не было: ссылка НЕ
   * подставлена, поле не заполняется. Не «не найдено»: запись есть, и чинится это возвратом из архива.
   */
  status: 'matched' | 'not-found' | 'dangling' | 'drift' | 'stale' | 'archived' | 'archived-skipped' | 'error';
  linkedName: string | null;
  detail: string | null;
}

/** По требованию (кнопка «Проверить связки») — enabled:false, дёргается через refetch. */
export function useCheckBindings(id: string | undefined) {
  return useQuery({
    queryKey: [QK, 'binding-check', id],
    queryFn: () => apiClient.get<{ items: BindingCheckItem[] }>(`/common-data/${id}/binding-check`).then(r => r.data),
    enabled: false,
  });
}

export function useCreateCommonDataEntry() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: {
      displayName: string;
      compositeTypeId: string;
      data: string;
      scope: CatalogScope;
      scopeId?: string | null;
      aliases?: string[];
      /** «Такая запись есть в архиве — создать всё равно» (issue #1185); без него сервер отвечает 409. */
      createAnyway?: boolean;
      /** Вынос в общие данные: объект, из сохранённых данных которого переезжают ссылки. */
      refsStandIn?: string;
    }) => apiClient.post<CommonDataEntry>('/common-data', payload).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

/** То из записи, что правка заменяет целиком, — одной строкой: по ней видно, изменилось ли содержимое. */
const recordContent = (entry: CommonDataEntry) =>
  JSON.stringify([entry.displayName, entry.aliases, entry.data]);

/** Основа черновика формы: версия, которую он вправе назвать, и запись, по которой он собран. */
export interface SeenBase {
  version: string;
  entry: CommonDataEntry;
}

/**
 * Версия, которую форма вправе назвать при сохранении (issue #1214), — та, по которой собран её
 * черновик, а не «какая сейчас в кэше»: запись под открытой формой перечитывается сама, и свежая
 * версия под прежним черновиком затёрла бы чужую правку ровно так же, как до этой задачи.
 *
 * Версия строки движется и тогда, когда содержимое не менялось, — запись вернули из архива кнопкой
 * над этой же формой. Поэтому основа помнит и содержимое: версия новая, а оно то же — черновик
 * по-прежнему собран по лежащему в базе, и основа молча переезжает. Содержимое другое — основа
 * остаётся прежней, и сервер откажет.
 *
 * Содержимое сравнивается ТОЛЬКО когда версия сдвинулась: в данных лежат картинки на мегабайты, и
 * сериализовать их при каждом открытии формы ради редкого случая незачем (ревью PR #1231).
 */
export function seenStep(base: SeenBase, entry: CommonDataEntry | null | undefined): SeenBase {
  if (!entry || base.version === entry.version) return base;
  return recordContent(base.entry) === recordContent(entry) ? { version: entry.version, entry } : base;
}

/**
 * Правка записи. Запись — и её идентификатор, и версия — берётся из ОДНОГО места, из аргумента
 * хука: с идентификатором в переменных мутации версия одной записи могла бы уйти под адресом другой.
 *
 * @param entry запись, по которой собрана форма; у формы новой записи её нет, и правку такой хук
 *   не выполняет.
 */
export function useUpdateCommonDataEntry(entry: CommonDataEntry | null | undefined) {
  const qc = useQueryClient();
  const [base, setBase] = useState<SeenBase | null>(() => (entry ? { version: entry.version, entry } : null));
  // Шаг считается при смене записи, а не на каждый кадр: под устаревшей основой он сравнивает содержимое.
  const next = useMemo(() => (base ? seenStep(base, entry) : null), [base, entry]);
  // Состояние правится в рендере — приём React для состояния, выведенного из пропсов.
  if (next !== base) setBase(next);
  return useMutation({
    mutationFn: async ({ displayName, data, aliases }: { displayName: string; data: string; aliases?: string[] }) => {
      if (!next) throw new Error('Правка без записи: форма новой записи создаёт, а не правит.');
      const { id } = next.entry;
      const key = [QK, 'by-id', id];
      // Запись как раз перечитывается (её только что вернули из архива над этой формой) — ждём:
      // иначе ушла бы прежняя версия, и сервер отказал бы человеку на его собственное действие.
      // Какую версию назвать, решает тот же шаг: содержимое то же — новая, другое — прежняя.
      if (qc.isFetching({ queryKey: key }) > 0) await qc.refetchQueries({ queryKey: key }, { cancelRefetch: false });
      const seen = seenStep(next, qc.getQueryData<CommonDataEntry>(key)).version;
      return apiClient.put<CommonDataEntry>(`/common-data/${id}`, { displayName, data, aliases },
        { headers: { 'If-Match': seen } }).then(r => r.data);
    },
    // Отказ 409 — запись изменили: копия в кэше перечитывается, чтобы форма, открытая заново,
    // собралась по свежей записи, а не по той же устаревшей (чтение записи кэшируется на минуту).
    //
    // ⚠️ Именно перечитывается, а не выбрасывается. Выброшенная копия оставляет открытую форму без
    // записи: она пересоздаётся на свежей — и человек теряет и набранное, и сообщение об отказе
    // (наступали при проверке на стенде). Основа открытой формы при этом остаётся прежней.
    onError: error => {
      if ((error as { response?: { status?: number } })?.response?.status === 409)
        qc.invalidateQueries({ queryKey: [QK] });
    },
    onSuccess: saved => {
      const { id } = saved;
      // Ответ правки — запись целиком и с новой версией: кладём её в кэш сразу, чтобы всё, что
      // показывает запись по идентификатору, не держало прежнюю до перечитывания.
      qc.setQueryData([QK, 'by-id', id], saved);
      qc.invalidateQueries({ queryKey: [QK] });
      // Расхождения значений с типом считает сервер по СОХРАНЁННЫМ данным (issue #644) — без сброса
      // подсказка висела бы у поля, которое только что исправили.
      qc.invalidateQueries({ queryKey: ['common-data-audit', id] });
    },
  });
}

/** Запись в архиве, с чьим ключом идентичности совпала создаваемая (issue #1185). */
export interface ArchivedTwin {
  archivedId: string;
  archivedName: string;
  archivedScope: CatalogScope;
}

/**
 * Отказ создания «такая запись есть в архиве». Читаем ПОЛЯ ответа, а не слова причины — как и
 * `archiveOffered`: по ним экран предлагает вернуть запись кнопкой.
 */
export function archivedTwinOf(e: unknown): ArchivedTwin | null {
  const data = (e as { response?: { data?: Partial<ArchivedTwin> & { code?: unknown } } })?.response?.data;
  return data?.code === 'archived-twin' && typeof data.archivedId === 'string'
    ? { archivedId: data.archivedId, archivedName: data.archivedName ?? '', archivedScope: data.archivedScope ?? 'System' }
    : null;
}

const NO_IDS: ReadonlySet<string> = new Set();

/**
 * Какие из названных записей — в архиве (issue #1185). Вопрос формы про свои уже стоящие ссылки:
 * в списке выбора архивной записи нет, и узнать о ней там нечем.
 *
 * Ключ лежит под общим `common-data`, поэтому отправка в архив и возврат сбрасывают и этот ответ.
 * Прежний ответ держится, пока едет новый: иначе пометки мигали бы на каждую смену набора ссылок.
 */
export function useArchivedAmong(ids: string[]): ReadonlySet<string> {
  const { data } = useQuery({
    queryKey: [QK, 'archived-among', ids],
    queryFn: () => apiClient.post<{ archived: string[] }>('/common-data/archived-among', { ids }).then(r => r.data.archived),
    enabled: ids.length > 0,
    placeholderData: keepPreviousData,
  });
  return useMemo(() => (data && data.length > 0 ? new Set(data) : NO_IDS), [data]);
}

export interface RecordArchiveResult {
  id: string;
  displayName: string;
  archived: boolean;
  /** false — запись уже была в этом состоянии: повтор, а не событие. */
  changed: boolean;
}

/**
 * Отправить запись в архив или вернуть из него (issue #1185). Отдельные адреса, а не поле правки:
 * форма, не знающая признака, сняла бы архив обычным сохранением.
 */
export function useSetCommonDataArchive() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, archived }: { id: string; archived: boolean }) =>
      apiClient.post<RecordArchiveResult>(`/common-data/${id}/${archived ? 'archive' : 'unarchive'}`).then(r => r.data),
    // Сбрасывается ВСЁ прочитанное, а не один список общих данных (ревью PR #1227): от признака
    // зависят и списки на выбор у модулей, и состояние ссылок в уже открытом счёте. Со старым
    // списком форма счёта предложила бы архивного поставщика, а со старым счётом — назвала бы его
    // «записью другого вида». Действие редкое, перечитать лишнее дешевле, чем вести здесь перечень
    // ключей чужих экранов, который отстанет на первом же новом.
    onSuccess: () => qc.invalidateQueries(),
  });
}

/**
 * Вернуть из архива несколько записей разом (вставка таблицы, ревью PR #1228). Отдельно от
 * одиночного действия: то сбрасывает всё прочитанное на КАЖДУЮ запись, и восемь возвратов подряд
 * восемь раз перечитали бы открытый редактор посреди несохранённой правки. Здесь сброс один, после
 * всех ответов. Отдаёт идентификаторы тех, кого вернуть удалось: остальные остались в архиве.
 */
export function useReturnManyFromArchive() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (ids: string[]) => {
      const done = await Promise.allSettled(ids.map(id => apiClient.post(`/common-data/${id}/unarchive`)));
      return ids.filter((_, i) => done[i].status === 'fulfilled');
    },
    onSuccess: () => qc.invalidateQueries(),
  });
}

/**
 * Отказ в удалении говорит, что выход — архив. Читаем ПОЛЕ ответа, а не слова причины: текст
 * сервер волен переписать, а предлагать действие по совпадению фразы — значит однажды позвать туда,
 * куда пути нет.
 */
export function archiveOffered(e: unknown): boolean {
  return (e as { response?: { data?: { canArchive?: unknown } } })?.response?.data?.canArchive === true;
}

export function useDeleteCommonDataEntry() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.delete(`/common-data/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}
