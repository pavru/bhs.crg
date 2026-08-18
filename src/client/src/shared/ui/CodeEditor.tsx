import { lazy, Suspense } from 'react';
import type { EditorProps } from '@monaco-editor/react';
import { CYRILLIC_ALLOWED } from './editorUnicode';

const MonacoEditor = lazy(() => import('./MonacoEditor'));

/**
 * Общие настройки всех наших редакторов кода (issue #776).
 *
 * <p>Кириллица объявлена ожидаемой (см. <see cref="CYRILLIC_ALLOWED" />), а сама проверка остаётся
 * включённой: подмена буквы из чужого алфавита по-прежнему видна. Невидимые символы тоже
 * подсвечиваются — неразрывный пробел или zero-width в шаблоне Typst молча уедет в PDF, а глазами
 * его не найти.</p>
 *
 * <p>Заметить дефект можно было не везде: Monaco разрешает символы локали окружения, поэтому на
 * русской системе рамок нет, а на английской они есть. Проверять такое надо в браузере с
 * `locale: 'en-US'` — иначе исправление «работает» ровно потому, что и ломаться было нечему.</p>
 */
const BASE_OPTIONS: EditorProps['options'] = {
  unicodeHighlight: {
    ambiguousCharacters: true,
    allowedCharacters: CYRILLIC_ALLOWED,
    invisibleCharacters: true,
  },
};

/**
 * Редактор кода (Monaco). Обёртка нужна затем, чтобы сам редактор жил отдельным куском сборки:
 * пакет весит несколько мегабайт, а открывают его только на страницах шаблонов и библиотеки.
 * Props — те же, что у @monaco-editor/react.
 *
 * Настройки вызывающего накладываются поверх общих: место одно на все пять редакторов, иначе
 * каждый новый пришлось бы вспоминать отдельно.
 */
export default function CodeEditor({ options, ...props }: EditorProps) {
  return (
    <Suspense
      fallback={<div className="h-full w-full flex items-center justify-center text-sm text-fg-muted">Загрузка редактора…</div>}
    >
      <MonacoEditor {...props} options={{ ...BASE_OPTIONS, ...options }} />
    </Suspense>
  );
}
