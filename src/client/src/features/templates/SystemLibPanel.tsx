import Editor from '@/shared/ui/CodeEditor';
import { registerTypstLanguage } from '@/shared/ui/typstLanguage';
import { useTheme } from '@/shared/ui/themeContext';
import { useSystemTypstLib } from '@/shared/api/typstUserLib';
import { Icon } from '@/shared/ui/Icon';
import { LockIcon } from '@/shared/ui/icons';

/**
 * Просмотр системной Typst-библиотеки (issue #344) — ХАРДКОД, только чтение. Авто-подключается к
 * каждому шаблону, импортировать не нужно. Содержит системные хелперы (напр. instance-of).
 */
export function SystemLibPanel() {
  const { resolvedTheme } = useTheme();
  const { data: content = '', isLoading } = useSystemTypstLib();

  if (isLoading) {
    return <div className="flex-1 flex items-center justify-center text-on-surface-variant text-body-medium">Загрузка...</div>;
  }

  return (
    <div className="flex flex-col h-full">
      <div className="flex items-center gap-2 px-4 py-2 border-b border-outline-variant bg-surface">
        <Icon icon={LockIcon} size={18} className="text-on-surface-variant shrink-0" />
        <p className="text-body-small text-on-surface-variant">
          {/* Было «авто-подключается, импортировать не нужно» — неверно: шаблон пишется в tmpDir
              ДОСЛОВНО (issue #353), преамбулу компиляция не подставляет. Импорт живёт в самом
              шаблоне; в новые его кладёт buildBlankTypst, старым и рукописным нужен свой. */}
          Системная библиотека — только чтение. Шаблон подключает её импортом{' '}
          <code className="font-mono">#import "systemlib.typ": *</code> (в новых шаблонах строка уже стоит).
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
