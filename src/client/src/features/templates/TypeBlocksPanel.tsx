import Editor from '@/shared/ui/CodeEditor';
import { registerTypstLanguage } from '@/shared/ui/typstLanguage';
import { useTheme } from '@/shared/ui/ThemeProvider';
import { useTypeBlocks } from '@/shared/api/typstUserLib';
import { Lock } from 'lucide-react';

/**
 * Просмотр собранного `typeblocks.typ` (issue #770) — только чтение.
 *
 * <p>Шаблон пишут против этого файла, а увидеть его до сих пор можно было лишь обходом: скачать
 * debug-бандл (он привязан к конкретному документу и приходит ZIP) либо собрать в голове из карточек
 * «Typst-блоки» у каждого типа. Второе не работает даже в принципе: порядок в файле задаёт топосорт
 * по зависимостям (#309), а диспетч-часть (#768) не показана ни в одной карточке — её эмитит сервер.</p>
 *
 * <p>Правка отсюда не предусмотрена намеренно: файл производный, единственное место его блоков —
 * схема типа. Панель отвечает на вопрос «что реально уходит в Typst», а не заменяет редактор.</p>
 */
export function TypeBlocksPanel() {
  const { resolvedTheme } = useTheme();
  const { data: content = '', isLoading, isError } = useTypeBlocks();

  if (isLoading) {
    return <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Загрузка...</div>;
  }
  if (isError) {
    return (
      <div className="flex-1 flex items-center justify-center text-danger text-sm">
        Не удалось собрать typeblocks.typ
      </div>
    );
  }

  // Пустой файл — законное состояние (ни у одного типа нет блоков), но пустой редактор читается как
  // сбой загрузки. Говорим прямо, где эти блоки заводят.
  const empty = content.trim().length === 0;

  return (
    <div className="flex flex-col h-full">
      <div className="flex items-center gap-2 px-4 py-2 border-b border-stroke bg-surface">
        <Lock size={13} className="text-fg4 shrink-0" />
        <p className="text-xs text-fg3">
          Собранный <code className="font-mono">typeblocks.typ</code> — только чтение. Подключается к
          каждому шаблону (импортировать не нужно). Порядок функций задаёт зависимость между блоками,
          поэтому он может отличаться от порядка в схемах. Блоки правятся у типа: Типы документов →
          Typst-блоки.
        </p>
      </div>
      {empty ? (
        <div className="flex-1 flex items-center justify-center text-fg4 text-sm text-center px-6">
          Ни у одного типа нет Typst-блоков — файл пуст.
          <br />
          Блоки добавляются в схеме типа, раздел «Typst-блоки».
        </div>
      ) : (
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
      )}
    </div>
  );
}
