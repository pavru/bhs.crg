import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Карточка позиции номенклатуры в окне выбора (issue #1516, #1517) — ПОКАЗ для человека: убедиться до
 * выбора, что позиция та. Сравнивать по этим строкам код не должен: перечисление здесь подписью,
 * ссылка — названием.
 */

export type CardLineKind = 'text' | 'number' | 'date' | 'boolean' | 'choice' | 'reference' | 'hidden';

export interface CardLine {
  /** Ключ поля в схеме: по нему, а не по заголовку, сверяется набранное в шаге «Новая позиция». */
  key: string;
  title: string;
  /** `null` — не заполнено либо не прочитано (`unreadable`). */
  text: string | null;
  /** `hidden` — поле ключа с картинкой или таблицей: заполнено, содержимое не показывается. */
  kind: CardLineKind;
  /** Поле ключа идентичности: стоит первым и всегда, даже пустое. */
  identity: boolean;
  /** Название основы, от которой пришло значение; `null` — своё. */
  inheritedFrom: string | null;
  /** Прочитать не удалось — это не «пусто». */
  unreadable: boolean;
  /** Ссылка ведёт на архивную запись. */
  archived: boolean;
}

export interface NomenclatureCard {
  id: string;
  name: string | null;
  type: string;
  typeName: string;
  archived: boolean;
  lines: CardLine[];
  aliases: string[];
  /** Заполненные картинки, файлы, составные — только заголовками. */
  notShown: string[];
  /** Сколько простых полей не заполнено — их в `lines` нет. */
  emptyCount: number;
  /** Поле с тэгом артикула; `null` — справочник не размечен. */
  article: CardLine | null;
  /** Поле с тэгом единицы; `null` — справочник не размечен. */
  unit: CardLine | null;
  /** Уровень области, если не вся система. */
  scope: 'construction' | 'section' | 'set' | null;
  /** Сколько строк счетов и накладных держат позицию — по документам, открытым читающему. */
  chosen: { times: number; lastOn: string | null };
}

export const nomenclatureCardKey = (id: string) => ['nomenclature-card', id] as const;

/**
 * Карточка по идентификатору; `null` — ничего не выделено, запроса нет.
 *
 * <p>Кэш по позиции, пять минут: стрелкой вернулись к показанной — без запроса. Прошлая карточка под
 * новым заголовком НЕ придерживается: на миг под названием одной позиции стояли бы поля другой.</p>
 */
export function useNomenclatureCard(id: string | null) {
  return useQuery({
    queryKey: nomenclatureCardKey(id ?? ''),
    queryFn: ({ signal }) => apiClient
      .get<NomenclatureCard>(`/costs/nomenclature/${id}/card`, { signal })
      .then(r => r.data),
    enabled: id !== null,
    staleTime: 5 * 60_000,
    // Удалённая позиция (404) не воскреснет от повтора, а пауза перед ним показала бы скелет вместо ответа.
    retry: (count, error) => (error as { response?: { status?: number } })?.response?.status !== 404 && count < 2,
  });
}
