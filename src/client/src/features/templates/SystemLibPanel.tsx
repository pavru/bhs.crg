import Editor from '@monaco-editor/react';
import { registerTypstLanguage } from '@/shared/ui/typstLanguage';
import { useTheme } from '@/shared/ui/ThemeProvider';
import { useSystemTypstLib } from '@/shared/api/typstUserLib';
import { Lock } from 'lucide-react';

/**
 * Просмотр системной Typst-библиотеки (issue #344) — ХАРДКОД, только чтение. Авто-подключается к
 * каждому шаблону, импортировать не нужно. Содержит системные хелперы (напр. instance-of).
 */
export function SystemLibPanel() {
  const { resolvedTheme } = useTheme();
  const { data: content = '', isLoading } = useSystemTypstLib();

  if (isLoading) {
    return <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Загрузка...</div>;
  }

  return (
    <div className="flex flex-col h-full">
      <div className="flex items-center gap-2 px-4 py-2 border-b border-stroke bg-surface">
        <Lock size={13} className="text-fg4 shrink-0" />
        <p className="text-xs text-fg3">
          Системная библиотека — только чтение. Авто-подключается к каждому шаблону (импортировать не нужно).
        </p>
      </div>
      <div className="flex-1 overflow-hidden">
        <Editor
          height="100%"
          defaultLanguage="typst"
          theme={resolvedTheme === 'dark' ? 'vs-dark' : 'vs'}
          value={content}
          beforeMount={registerTypstLanguage}
          options={{
            readOnly: true,
            domReadOnly: true,
            minimap: { enabled: false },
            fontSize: 13,
            fontFamily: "'Cascadia Code', 'Fira Code', Consolas, monospace",
            wordWrap: 'on',
            lineNumbers: 'on',
            scrollBeyondLastLine: false,
            automaticLayout: true,
            tabSize: 2,
          }}
        />
      </div>
    </div>
  );
}
