import { useMemo, type ReactNode } from 'react';
import { useArchivedAmong } from '@/shared/api/commonData';
import { ArchivedMark } from '@/shared/ui/ArchivedMark';
import { ArchivedRefsContext, RefsOwnerContext, catalogRefIds, useIsArchivedRef } from './archivedRefIds';

/**
 * Узнаёт у сервера, какие ссылки формы указывают на архивные записи, и раздаёт ответ плиткам.
 *
 * <p><code>data</code> — СОХРАНЁННЫЕ данные, а не то, что сейчас набрано. Этого достаточно, и это
 * точнее: новую ссылку на архивную запись поставить нечем (в выборе её нет), значит архивной может
 * быть только та, что уже стояла. А спрашивать на каждое нажатие клавиши было бы незачем.</p>
 */
export function ArchivedRefsProvider({ data, ownerId, children }: {
  data: unknown;
  /** Сохранённый объект, которому принадлежат данные, — его называет вынос в общие данные. */
  ownerId?: string;
  children: ReactNode;
}) {
  const ids = useMemo(() => catalogRefIds(data), [data]);
  const archived = useArchivedAmong(ids);
  return (
    <RefsOwnerContext.Provider value={ownerId}>
      <ArchivedRefsContext.Provider value={archived}>{children}</ArchivedRefsContext.Provider>
    </RefsOwnerContext.Provider>
  );
}

/**
 * Пометка «в архиве» у плитки ссылки (issue #1185): плитка остаётся обычной — запись не потеряна и
 * чинить нечего, — а пометка говорит, что выбрать её заново не выйдет. `words={false}` — для ячейки
 * таблицы: только значок, слово живёт в подсказке.
 */
export function ArchivedRefMark({ value, words = true }: { value: unknown; words?: boolean }) {
  return useIsArchivedRef(value) ? <ArchivedMark words={words} /> : null;
}
