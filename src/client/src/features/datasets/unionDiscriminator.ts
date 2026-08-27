import type { SchemaField } from '@/shared/api/schema';
import type { MaterializeDiscriminator } from '@/shared/api/types';

// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

/**
 * Почему настройку нельзя сохранять; null — можно.
 *
 * Та же логика живёт на сервере и там же остаётся авторитетом: здесь она нужна, чтобы отказ пришёл
 * до нажатия, а не после. Дублирование намеренное и односторонне безопасное — клиент строже или
 * равен серверу, но никогда не мягче.
 */
export function discriminatorProblem(
  variants: SchemaField[],
  mapping: Record<string, string>,
  discriminator: MaterializeDiscriminator,
  typeName: (id: string) => string,
): string | null {
  if (!discriminator.column) return 'Выберите колонку, по которой определяется вариант.';

  // Владельца запоминаем ЗАГОЛОВКОМ варианта, а не ключом: сообщение читает человек, и «АОСР_ЭМ»
  // вместо «АОСР электромонтаж» заставляет искать соответствие руками.
  const owners = new Map<string, string>();
  for (const v of variants) {
    const ids = discriminator.rules[v.key] ?? [];
    if (ids.length > 0 && !mapping[v.key])
      return `Для варианта «${v.title}» назначены типы документов, но не задан маппинг колонок.`;
    for (const id of ids) {
      const other = owners.get(id);
      if (other && other !== v.title)
        return `Тип документа «${typeName(id)}» назначен сразу двум вариантам — «${other}» и «${v.title}».`;
      owners.set(id, v.title);
    }
  }

  if (owners.size === 0) return 'Ни одному варианту не назначено ни одного типа документа.';
  return null;
}
