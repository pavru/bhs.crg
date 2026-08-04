import { lazy, Suspense } from 'react';
import type { EditorProps } from '@monaco-editor/react';

const MonacoEditor = lazy(() => import('./MonacoEditor'));

/**
 * Редактор кода (Monaco). Обёртка нужна затем, чтобы сам редактор жил отдельным куском сборки:
 * пакет весит несколько мегабайт, а открывают его только на страницах шаблонов и библиотеки.
 * Props — те же, что у @monaco-editor/react.
 */
export default function CodeEditor(props: EditorProps) {
  return (
    <Suspense
      fallback={<div className="h-full w-full flex items-center justify-center text-sm text-fg-muted">Загрузка редактора…</div>}
    >
      <MonacoEditor {...props} />
    </Suspense>
  );
}
