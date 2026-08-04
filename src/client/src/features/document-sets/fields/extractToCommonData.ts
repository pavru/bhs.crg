import { isFieldRef, type CatalogScope, type FieldRef } from '@/shared/api/types';
import type { SchemaField } from '@/shared/api/schema';
import type { FieldTypeDefs } from '@/shared/utils/fieldDisplay';
import { formatFieldValue } from '@/shared/utils/fieldDisplay';
import { objectSummary } from './objectSummary';

/**
 * Чистая логика выноса inline-объекта составного поля в общие данные (issue #663).
 *
 * Движение между документом и каталогом было односторонним: выбрать запись из каталога можно, а
 * превратить уже набранное значение в переиспользуемую запись — нет. Пользователь либо повторял ввод
 * в каждом следующем документе, либо заводил то же самое второй раз руками; два «одинаковых» объекта,
 * набранных дважды, расходятся пробелом или сокращением и дальше расходятся в PDF.
 *
 * Здесь собрано всё, что решается БЕЗ интерфейса: как назвать запись, что послать на поиск дубликата,
 * что мешает выносу и насколько широко запись вообще можно положить. Модалка остаётся тонкой, а
 * проверять эти правила можно текстом, а не кликами.
 */

/** Что мешает выносу: ссылка, которая вне своего комплекта не разворачивается. */
export interface BlockingRef {
  /** Путь до места ссылки — `Работы[3].Основание`; пустой путь = сам объект. */
  path: string;
  kind: 'document' | 'instance';
  displayName: string;
}

/** Порядок от узкого к широкому — им же считается «самый узкий уровень». */
const SCOPE_WIDTH: Record<CatalogScope, number> = {
  Set: 0, Section: 1, Construction: 2, System: 3,
};

/**
 * Имя, предлагаемое для новой записи.
 *
 * Поля идентичности — если они у типа объявлены: именно по ним объект и опознаётся, и имя, собранное
 * из них, совпадёт с тем, что человек считает «этим самым» объектом. Иначе — обычная сводка первых
 * заполненных полей, та же, которой объект подписан в свёрнутом виде: имя, отличающееся от подписи,
 * пришлось бы сверять глазами.
 *
 * Значения форматируем как на экране (issue #611) — иначе в имя уехала бы дата в ISO.
 */
export function suggestEntryName(
  values: Record<string, unknown>,
  fields: SchemaField[],
  identityKeys: string[],
  defs: FieldTypeDefs = {},
): string {
  const byKey = new Map(fields.map(f => [f.key, f]));
  const parts = identityKeys
    .map(key => {
      const field = byKey.get(key);
      const value = values?.[key];
      if (!field || value == null || value === '') return null;
      if (isFieldRef(value)) return value.displayName;
      if (typeof value === 'object') return null;
      return formatFieldValue(field, value, defs);
    })
    .filter((s): s is string => !!s && s.trim() !== '');

  if (parts.length > 0) return parts.join(' ');

  // «(пусто)» — служебная подпись сводки, в имя записи её пускать незачем: пустое имя форма и так
  // не пропустит, а «(пусто)» выглядело бы заполненным.
  const summary = objectSummary(values ?? {}, fields, defs);
  return summary === '(пусто)' ? '' : summary;
}

/**
 * Скаляры ВЕРХНЕГО уровня объекта — то, что уходит на поиск дубликата по составному ключу.
 *
 * ЗЕРКАЛО серверного `ObjectResolver.ReadField`: строка, число и булево читаются, объекты, массивы и
 * null — нет. Порядок identity-полей клиенту знать не нужно, его сервер берёт сам по `typeId`
 * (`ObjectResolver.IdentityKeysForType`); задача этой функции — не потерять ни одного компонента,
 * поэтому отдаём все скаляры, а не угаданное подмножество.
 */
export function scalarFieldsFor(values: Record<string, unknown>): Record<string, string | null> {
  const out: Record<string, string | null> = {};
  for (const [key, value] of Object.entries(values ?? {})) {
    if (value == null) continue;
    if (typeof value === 'string') out[key] = value;
    else if (typeof value === 'number') out[key] = String(value);
    else if (typeof value === 'boolean') out[key] = value ? 'true' : 'false';
    // объекты, массивы и ссылки — не компоненты ключа: сервер их тоже не читает
  }
  return out;
}

/**
 * Ссылки, при которых выносить нельзя, — на любой глубине (обход по образцу серверного
 * `RefReader.CollectRefIds`).
 *
 * `$ref:'document'` и `$ref:'instance'` резолвятся только внутри СВОЕГО комплекта
 * (`EntityResolver`: `ScopeLevel == Set && ScopeId == scope.SetId`). Оказавшись в записи каталога,
 * которую подключат к другому комплекту, такая ссылка уйдёт в шаблон сырым `$ref` — то есть в
 * исполнительную документацию попадёт служебная структура вместо значения.
 *
 * Тихо вырезать их нельзя: это потеря данных, о которой человек не узнает. Поэтому вынос
 * блокируется, а места ссылок называются — их видно, куда идти править.
 *
 * `$ref:'catalog'` не блокирует: он разворачивается рекурсивно откуда угодно. Он лишь ограничивает
 * уровень — см. {@link maxAllowedScope}.
 */
export function findBlockingRefs(value: unknown, path = ''): BlockingRef[] {
  const out: BlockingRef[] = [];
  walkRefs(value, path, ref => {
    if (ref.$ref === 'catalog') return;
    out.push({ path: ref.path, kind: ref.$ref, displayName: ref.displayName });
  });
  return out;
}

/**
 * Самый широкий уровень, на который запись можно положить, — он же самый УЗКИЙ уровень среди
 * вложенных ссылок на каталог.
 *
 * `EntityResolver.ResolveEntryByIdAsync` грузит запись по id БЕЗ scope-фильтра (фильтр применён
 * только к `_baseRef`). Значит ссылка на запись комплекта, оказавшись внутри записи уровня «Система»,
 * развернётся в чужом комплекте — молча и правдоподобно. Ограничение выбора, а не предупреждение:
 * широкая ошибка здесь тиха, и заметить её постфактум не по чему.
 *
 * Уровень ссылки читается из неё самой (его кладёт `RefPickerModal`). Поля может не быть у ссылок,
 * заведённых до того, как его начали писать, — такие вызывающий доопределяет по загруженному
 * каталогу; чего не доопределили, считаем «Комплект»: узкая ошибка обратима (уровень не предложат),
 * широкая — нет.
 */
export function maxAllowedScope(
  value: unknown,
  scopeOfEntry: (entryId: string) => CatalogScope | undefined = () => undefined,
): CatalogScope {
  let widest: CatalogScope = 'System';
  walkRefs(value, '', ref => {
    if (ref.$ref !== 'catalog') return;
    const scope = ref.scope ?? (ref.entryId ? scopeOfEntry(ref.entryId) : undefined) ?? 'Set';
    if (SCOPE_WIDTH[scope] < SCOPE_WIDTH[widest]) widest = scope;
  });
  return widest;
}

/** Уровни от «Комплект» до заданного включительно — то, что вообще можно предложить в выборе. */
export function scopesUpTo(max: CatalogScope): CatalogScope[] {
  return (['Set', 'Section', 'Construction', 'System'] as CatalogScope[])
    .filter(s => SCOPE_WIDTH[s] <= SCOPE_WIDTH[max]);
}

/**
 * Уровни, которые реально можно предложить: не шире разрешённого вложенными ссылками И с известным
 * идентификатором контейнера. «Система» живёт без идентификатора, остальным он обязателен — уровень,
 * которому некуда положить запись, предлагать нельзя.
 *
 * ПУСТОЙ результат — законный исход, а не «ну возьми что-нибудь»: значит все разрешённые уровни
 * недоступны, и выносить отсюда нечем. Подставлять в этом случае свой уровень владельца — ровно тот
 * тихий широкий промах, от которого ограничение и заведено: список пуст, объяснение говорит «шире
 * нельзя», а запись уходит на уровень шире разрешённого.
 */
export function offeredScopes(
  allowedMax: CatalogScope, idFor: (s: CatalogScope) => string | null,
): CatalogScope[] {
  return scopesUpTo(allowedMax).filter(s => s === 'System' || !!idFor(s));
}

/** Рекурсивный обход значений с вызовом на каждой встреченной ссылке. Общий для обеих проверок. */
function walkRefs(value: unknown, path: string, visit: (ref: FieldRef & { path: string }) => void): void {
  if (value == null || typeof value !== 'object') return;

  if (isFieldRef(value)) {
    visit({ ...value, path });
    return; // внутрь ссылки не идём: её содержимое — чужая запись, у неё свои правила
  }

  if (Array.isArray(value)) {
    value.forEach((item, i) => walkRefs(item, `${path}[${i}]`, visit));
    return;
  }

  for (const [key, v] of Object.entries(value as Record<string, unknown>))
    walkRefs(v, path ? `${path}.${key}` : key, visit);
}
