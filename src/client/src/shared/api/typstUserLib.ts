import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

const QK = ['typst-userlib'] as const;

/** Файл дерева библиотеки (issue #473). Путь — относительный, от папки `userlib/`. */
export interface UserLibFile { path: string; content: string; }

/**
 * `inBuild` — входит ли файл в сборку библиотеки (достижим по импортам от точки входа). Ошибка в
 * подключённом файле останавливает генерацию ВСЕХ документов, в неподключённом — только шаблонов,
 * импортирующих его напрямую (issue #506).
 */
export interface UserLibError {
  path: string; line: number; column: number; message: string; inBuild: boolean;
}
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
 * Чтение библиотеки. Замечания приходят и здесь, а не только в ответе на сохранение: иначе дубликаты
 * имён — а Typst молча берёт объявление из файла, импортированного последним, — были бы видны лишь
 * сразу после сохранения и исчезали при перезагрузке страницы.
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
/** Один файл собранных блоков: агрегатор либо модуль типа (issue #772). */
export interface TypeBlockFile {
  path: string;
  content: string;
}

/**
 * Собранные блоки типов для просмотра (issue #770) — агрегатор `typeblocks.typ` и по файлу на тип
 * `typeblocks-<слаг>.typ` рядом (#772), плюс диспетч-часть (#768).
 *
 * `staleTime: 0` задан ЯВНО: глобальный дефолт — 30 секунд (см. App.tsx), и с ним админ, поправивший
 * блок у типа и сразу перешедший на вкладку, увидел бы файл ДО правки — ровно то противоречие с
 * редактором, ради устранения которого экран и заводился. Системная библиотека тем и отличается:
 * она константа, ей протухание безразлично.
 */
export function useTypeBlocks(enabled = true) {
  return useQuery({
    queryKey: TYPEBLOCKS_KEY,
    queryFn: async () => {
      const r = await apiClient.get<{ files?: TypeBlockFile[]; blockCount: number }>('/templates/typeblocks');
      return { files: r.data.files ?? [], blockCount: r.data.blockCount };
    },
    enabled,
    staleTime: 0,
  });
}

export const TYPEBLOCKS_KEY = ['typst-typeblocks'] as const;

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
