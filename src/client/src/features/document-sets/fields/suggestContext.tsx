import { createContext, useContext, useMemo, type ReactNode } from 'react';
import type { SuggestSource } from '@/shared/api/fieldSuggestions';
import type { SchemaField } from '@/shared/api/schema';
import { suggestReason } from '@/shared/api/suggestable';
import type { CatalogScope } from '@/shared/api/types';

/**
 * Чьё поле рисует форма — для подсказки по введённому ранее (issue #1533, эпик #1527, D3 зонтика
 * `suggest-00-epic`). Адрес подсказок спрашивает тип, чьей схеме принадлежит поле, и уровень записи;
 * ни того ни другого поле само не знает: его рисует общий `PrimitiveInput` или клетка таблицы.
 *
 * <p>Тип — тип самой записи, составного или строки массива, а не объявивший поле предок: объявившего
 * сервер находит сам (решение В-2).</p>
 */
interface SuggestOwnerValue {
  type: string;
  scope?: CatalogScope;
  scopeId?: string;
}

const SuggestOwnerContext = createContext<SuggestOwnerValue | null>(null);

/**
 * Задаёт тип-владелец полям внутри.
 *
 * <ul>
 *   <li><b>С `scope`</b> — корень: форма записи называет её тип и уровень (документ — комплект, запись
 *     каталога — свой уровень, шаг «Новая позиция» — «вся система»).</li>
 *   <li><b>Без `scope`</b> — вложенное составное или строка массива: меняет только тип, уровень берёт у
 *     внешнего. Без внешнего корня подсказки нет вовсе — так поля, нарисованные вне формы записи
 *     (дописанные поля счёта, D9), адрес не зовут без единой проверки «это счёт».</li>
 * </ul>
 */
export function SuggestOwner({ type, scope, scopeId, children }: {
  /** Тип-владелец; пусто — тип ещё не выбран или у составного его нет: подсказки нет. */
  type: string | null | undefined;
  scope?: CatalogScope;
  scopeId?: string | null;
  children: ReactNode;
}) {
  const outer = useContext(SuggestOwnerContext);
  const value = useMemo<SuggestOwnerValue | null>(() => {
    if (!type) return null;
    if (scope !== undefined) return { type, scope, scopeId: scopeId ?? undefined };
    return outer && { ...outer, type };
  }, [type, scope, scopeId, outer]);
  return <SuggestOwnerContext.Provider value={value}>{children}</SuggestOwnerContext.Provider>;
}

/**
 * Источник подсказки поля — или `null`: нет контекста типа-владельца либо правило `Suggestable` говорит
 * «не подсказывается». Решает вид поля СХЕМЫ, а не контрола: `text` в клетке рисуется однострочным
 * полем, но абзацы не подсказываются и там.
 */
// eslint-disable-next-line react-refresh/only-export-components -- хук читает контекст этого файла
export function useSuggestSource(
  field: Pick<SchemaField, 'key' | 'type' | 'computed' | 'locked' | 'tags'>,
): SuggestSource | null {
  const owner = useContext(SuggestOwnerContext);
  if (!owner || suggestReason(field) !== 'suggested') return null;
  return { type: owner.type, field: field.key, scope: owner.scope, scopeId: owner.scopeId };
}
