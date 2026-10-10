import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Новая позиция номенклатуры коротким окном (задача C3, issue #1079) — дверь ЯДРА под
 * `core.nomenclature.edit`. Модуль счетов её только открывает.
 */

export interface IntakeOption {
  id: string;
  name: string | null;
}

export interface IntakeField {
  key: string;
  title: string;
  required: boolean;
  /** Поле входит в ключ идентичности: по нему позицию сверяют с лежащими. */
  identity: boolean;
  /** `null` — поле текстовое; список (хоть и пустой) — поле ВЫБОРА записи справочника. */
  options: IntakeOption[] | null;
}

export interface IntakeKind {
  typeId: string;
  code: string;
  name: string;
  fields: IntakeField[];
  /** Почему вид отсюда не завести. Пусто — можно. */
  refusals: string[];
}

export interface NomenclaturePosition {
  id: string;
  name: string | null;
  type: string;
  archived: boolean;
}

export interface SimilarPositions {
  /** Позиция с тем же ключом. Есть — вторую такую не заводят. */
  exact: NomenclaturePosition | null;
  similar: { position: NomenclaturePosition; why: string }[];
  more: boolean;
  /** Сколько позиций сверить не удалось. Это не «не похожи». */
  unreadable: number;
}

export interface IntakeBody {
  typeId: string;
  values: Record<string, string>;
  refs?: Record<string, string>;
}

/**
 * Виды позиций и что о каждом спросить.
 *
 * ⚠️ Ответ не хранится между открытиями окна (`gcTime: 0`): в нём записи на выбор и приговор «вид
 * отсюда не завести». Единицу отправили в архив, схему поправили — а окно показало бы вчерашнее:
 * выбор архивной единицы кончился бы отказом сервера, а новой в списке не было бы вовсе.
 */
export function useNomenclatureIntake() {
  return useQuery({
    queryKey: ['nomenclature-intake'] as const,
    queryFn: () => apiClient.get<{ kinds: IntakeKind[] }>('/nomenclature/intake').then(r => r.data.kinds),
    staleTime: 0,
    gcTime: 0,
  });
}

/**
 * Позиция, из-за которой сервер отказал создавать: «такая уже есть» (409, `code: "exists"`). Отказ
 * приходит полями, чтобы окно предложило ВЫБРАТЬ лежащую, а не только пересказало его словами.
 */
export function existingPosition(error: unknown): NomenclaturePosition | null {
  const data = (error as { response?: { data?: { code?: string; existing?: NomenclaturePosition } } })?.response?.data;
  return data?.code === 'exists' && data.existing ? data.existing : null;
}

/**
 * Похожие на набранное. `body = null` — спрашивать не о чем (вид не выбран, названия нет).
 *
 * ⚠️ Прошлый ответ НЕ придерживается (в отличие от поиска): он про другое набранное, и кнопка
 * «Завести» под чужим «похожих нет» завела бы дубль.
 */
export function useSimilarNomenclature(body: IntakeBody | null) {
  return useQuery({
    queryKey: ['nomenclature-similar', body] as const,
    queryFn: () => apiClient.post<SimilarPositions>('/nomenclature/similar', body).then(r => r.data),
    enabled: body !== null,
    staleTime: 0,
  });
}

export function useCreateNomenclature() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: IntakeBody) =>
      apiClient.post<NomenclaturePosition>('/nomenclature', body).then(r => r.data),
    // Поиск в окне выбора и ответ о похожих устарели оба: в справочнике новая позиция.
    onSettled: () => Promise.all([
      queryClient.invalidateQueries({ queryKey: ['costs-nomenclature'] }),
      queryClient.invalidateQueries({ queryKey: ['nomenclature-similar'] }),
    ]),
  });
}
