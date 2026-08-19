import type { SchemaField, FieldGroup, TypstRender } from '@/shared/api/schema';
import { danglingKeyRefs } from './schemaKeyChecks';
import { ruCount } from '@/shared/utils/pluralize';

/**
 * Состояние типа как объекта, а не как формы (issue #794).
 *
 * Тип может доехать до положения, когда схему нельзя сохранить, — и заметить это раньше нажатия
 * «Сохранить» негде: сообщение живёт под формой, а конфликтный ключ помечен мелкой красной
 * подписью у поля. Причём привести к такому может правка ПРЕДКА: стоит добавить в родителя поле с
 * тем же ключом, что уже есть у потомка, и схема потомка перестаёт сохраняться, хотя её никто не
 * трогал (живой случай — «Подписи» у «Титульный лист исполнительной документации»).
 *
 * Поэтому считаем по СОХРАНЁННОЙ схеме: индикатор отвечает на вопрос «в каком состоянии тип»,
 * а ошибки текущей правки показывает сама форма.
 *
 * Проблемы сборки Typst-блоков сюда не входят: их даёт компиляция по запросу, и гонять её при
 * открытии каждого типа незачем.
 */

/** Мешает сохранению схемы — ровно те проверки, на которых падает `save()` в редакторе. */
export interface BlockingIssue {
  kind: 'key-conflict' | 'empty-key' | 'bad-key' | 'empty-title' | 'missing-type' | 'duplicate-key' | 'duplicate-fn';
  /** Готовая фраза для перечисления в подсказке. */
  text: string;
}

/** Сохранению не мешает, но означает «тип нездоров». */
export interface SoftIssue {
  kind: 'dangling-ref';
  text: string;
}

export interface TypeHealth {
  blocking: BlockingIssue[];
  soft: SoftIssue[];
}

const KEY_RE = /^[A-Za-zА-Яа-яЁё0-9_]+$/;

/**
 * @param own собственные поля типа (без унаследованных)
 * @param inheritedKeys ключи ЭФФЕКТИВНЫХ полей родителя — с ними конфликтует собственное поле
 * @param chainKeys все ключи цепочки предков ДО исключений: ссылка на исключённое поле законна,
 *   и по эффективному набору собственное исключение потомка выглядело бы висячим
 */
export function typeHealth(
  own: readonly SchemaField[],
  inheritedKeys: Iterable<string>,
  chainKeys: Iterable<string>,
  refs: {
    groups?: readonly FieldGroup[];
    excludedFields?: readonly string[];
    ungroupedOrder?: readonly string[];
    fieldOverrides?: Record<string, unknown>;
  },
  typstRenders: readonly TypstRender[] = [],
): TypeHealth {
  const blocking: BlockingIssue[] = [];
  const inherited = new Set(inheritedKeys);

  for (const f of own) {
    const key = f.key.trim();
    if (!key) { blocking.push({ kind: 'empty-key', text: `Поле «${f.title || 'без названия'}»: пустой ключ` }); continue; }
    if (!KEY_RE.test(key)) blocking.push({ kind: 'bad-key', text: `Ключ «${key}»: допустимы только буквы, цифры и _` });
    if (!f.title.trim()) blocking.push({ kind: 'empty-title', text: `Поле «${key}»: пустое название` });
    if (!f.typeId && ['complex', 'array', 'doc-ref', 'doc-array', 'primitive'].includes(f.type))
      blocking.push({ kind: 'missing-type', text: `Поле «${key}»: не выбран тип` });
    if (inherited.has(key))
      blocking.push({ kind: 'key-conflict', text: `Ключ «${key}» уже есть в родительском типе` });
  }

  const keys = own.map(f => f.key.trim());
  for (const k of new Set(keys.filter((k, i) => k && keys.indexOf(k) !== i)))
    blocking.push({ kind: 'duplicate-key', text: `Ключ «${k}» задан дважды` });

  const fns = typstRenders.map(r => r.fnName.trim()).filter(Boolean);
  for (const n of new Set(fns.filter((n, i) => fns.indexOf(n) !== i)))
    blocking.push({ kind: 'duplicate-fn', text: `Имя функции «${n}» задано дважды` });

  // Ключи для сравнения со ссылками берём КАК СОХРАНЕНЫ, без trim: группы, исключения и
  // переопределения хранят ровно тот же ключ, а редактор сверяет их так же. Обрезав пробелы здесь,
  // мы объявили бы висячей живую ссылку — и предложили бы её удалить.
  const soft: SoftIssue[] = danglingKeyRefs(
    {
      groups: refs.groups,
      excludedFields: refs.excludedFields,
      ungroupedOrder: refs.ungroupedOrder,
      fieldOverrideKeys: Object.keys(refs.fieldOverrides ?? {}),
    },
    [...chainKeys, ...own.map(f => f.key)],
  ).map(r => ({ kind: 'dangling-ref' as const, text: `Ссылка на несуществующее поле «${r.key}»` }));

  return { blocking, soft };
}

/**
 * Короткая подпись чипа: счётчик, а не перечисление — детали уходят в подсказку.
 * Склонение — общим `ruCount`: свой «1 / 2-4 / остальное» врал бы на 21 («21 ошибок»), а число
 * ошибок легко переваливает за двадцать — перепривязка типа даёт по конфликту на каждое совпавшее поле.
 */
export function healthBadgeLabel(health: TypeHealth): { blocking?: string; soft?: string } {
  const n = health.blocking.length;
  const m = health.soft.length;
  return {
    blocking: n ? `не сохранится — ${ruCount(n, 'ошибка', 'ошибки', 'ошибок')}` : undefined,
    soft: m ? ruCount(m, 'висячая ссылка', 'висячие ссылки', 'висячих ссылок') : undefined,
  };
}
