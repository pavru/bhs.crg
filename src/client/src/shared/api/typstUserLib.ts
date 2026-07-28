import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

const QK = ['typst-userlib'] as const;

/** Файл дерева библиотеки (issue #473). Путь — относительный, от папки `userlib/`. */
export interface UserLibFile { path: string; content: string; }

export interface UserLibError { path: string; line: number; column: number; message: string; }
export interface UserLibWarning { path: string; message: string; }

/**
 * Итог проверки на сервере. `ok: false` означает, что библиотека НЕ собирается — а её импортирует
 * каждый шаблон, поэтому встала генерация всех документов.
 *
 * Проверка компилирует зонд, который лишь импортирует точку входа: ловятся синтаксис и битые пути
 * импортов, но НЕ поведение. Поэтому состояние называется «библиотека собирается», а не «шаблоны
 * работают» — второе было бы обещанием, которого проверка не даёт.
 */
export interface UserLibCheck {
  ok: boolean;
  errors: UserLibError[];
  warnings: UserLibWarning[];
}

export interface UserLibState { content: string; files: UserLibFile[]; }

/**
 * Чтение библиотеки. Замечания приходят и здесь, а не только в ответе на сохранение (issue #492):
 * именно они заменили автоматическое дописывание импортов, и, исчезая после перезагрузки страницы,
 * возвращали неподключённый файл в разряд молчаливых отказов.
 */
export interface UserLibRead extends UserLibState { warnings: UserLibWarning[]; }

export function useTypstUserLib() {
  return useQuery({
    queryKey: QK,
    queryFn: async () => {
      const r = await apiClient.get<UserLibRead>('/typst-userlib');
      return {
        content: r.data.content, files: r.data.files ?? [], warnings: r.data.warnings ?? [],
      } satisfies UserLibRead;
    },
  });
}

/** Системная Typst-библиотека (issue #344) — хардкод, только чтение. */
export function useSystemTypstLib() {
  return useQuery({
    queryKey: ['typst-systemlib'],
    queryFn: async () => {
      const r = await apiClient.get<{ content: string }>('/templates/systemlib');
      return r.data.content;
    },
    staleTime: Infinity, // константа — не протухает
  });
}

/**
 * Сохранение библиотеки ЦЕЛИКОМ (issue #473). Пофайлового сохранения нет намеренно: правка файла и
 * правка зовущего его файла обязаны лечь вместе, иначе между двумя запросами библиотека не
 * собирается — а её читает генерация каждого документа.
 */
export function useSaveTypstUserLib() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (state: UserLibState) => {
      const r = await apiClient.put<UserLibState & { check: UserLibCheck }>('/typst-userlib', state);
      return r.data;
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: QK }),
  });
}
