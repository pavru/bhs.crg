import { useState, useEffect, useRef } from 'react';
import Editor from '@monaco-editor/react';
import type * as monacoEditor from 'monaco-editor';
import { registerTypstLanguage } from '@/shared/ui/typstLanguage';
import { useTheme } from '@/shared/ui/ThemeProvider';
import { useUserLibCompletion } from '@/shared/ui/typstUserLibCompletion';
import { Button } from '@/shared/ui/Button';
import { Save } from 'lucide-react';
import { useTypstUserLib, useSaveTypstUserLib } from '@/shared/api/typstUserLib';
import { TemplateAssetsPanel } from './TemplateAssetsPanel';
// ─── User Typst library panel ─────────────────────────────────────────────────

export function UserLibPanel() {
  const { resolvedTheme } = useTheme();
  useUserLibCompletion();
  const { data: serverContent = '', isLoading } = useTypstUserLib();
  const saveMutation = useSaveTypstUserLib();
  const [content, setContent] = useState('');
  const [savedMsg, setSavedMsg] = useState(false);
  const [error, setError] = useState('');
  const editorRef = useRef<monacoEditor.editor.IStandaloneCodeEditor | null>(null);
  // Библиотека — это список функций; развёрнутой она читается как простыня, и нужную приходится
  // искать прокруткой. Сворачиваем ОДИН раз при первой загрузке: повтор на каждое изменение
  // схлопывал бы блок прямо во время правки.
  const foldedOnce = useRef(false);

  useEffect(() => { setContent(serverContent); }, [serverContent]);

  async function handleSave() {
    setError('');
    try {
      await saveMutation.mutateAsync(content);
      setSavedMsg(true);
      setTimeout(() => setSavedMsg(false), 2000);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка сохранения');
    }
  }

  // Ctrl+S — тем же способом, что в редакторе шаблонов: перехват на окне в фазе capture, чтобы
  // опередить браузерное «Сохранить страницу», и по e.code, потому что на русской раскладке
  // e.key даёт «ы». Команду Monaco НЕ регистрируем: она сработала бы вторым, параллельным
  // сохранением (обработчик окна делает preventDefault, но не stopPropagation).
  const handleSaveRef = useRef(handleSave);
  handleSaveRef.current = handleSave;
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if ((e.ctrlKey || e.metaKey) && e.code === 'KeyS') {
        e.preventDefault();
        if (editorRef.current?.hasTextFocus()) void handleSaveRef.current();
      }
    }
    window.addEventListener('keydown', onKeyDown, { capture: true });
    return () => window.removeEventListener('keydown', onKeyDown, { capture: true });
  }, []);

  /**
   * Свернуть тела функций, оставив видимыми объявления. Именно foldLevel1, а не foldAll: последний
   * схлопнул бы и вложенные блоки, а нужен как раз перечень сигнатур.
   *
   * Область свёртки Monaco считает после разбора модели, поэтому запускаем следующим тиком — сразу
   * после монтирования сворачивать ещё нечего.
   */
  function foldFunctions() {
    if (foldedOnce.current || !editorRef.current || !serverContent.trim()) return;
    foldedOnce.current = true;
    const editor = editorRef.current;
    setTimeout(() => editor.getAction('editor.foldLevel1')?.run(), 0);
  }

  // Монтирование и приход содержимого не упорядочены — пробуем на обоих.
  useEffect(foldFunctions, [serverContent]);

  if (isLoading) {
    return <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Загрузка...</div>;
  }

  return (
    <div className="flex flex-col h-full">
      <div className="flex items-center justify-between px-4 py-2 border-b border-stroke bg-surface gap-3">
        <p className="text-xs text-fg3">
          Доступен в каждом шаблоне через{' '}
          <code className="font-mono bg-muted px-1.5 py-0.5 rounded text-fg2">#import "userlib.typ": *</code>
        </p>
        <div className="flex items-center gap-2">
          {savedMsg && <span className="text-xs text-success">Сохранено</span>}
          {error && <span className="text-xs text-danger max-w-xs truncate">{error}</span>}
          <Button variant="filled" size="sm" onClick={handleSave} loading={saveMutation.isPending}
            icon={<Save size={12} />}>
            {saveMutation.isPending ? 'Сохранение…' : 'Сохранить'}
          </Button>
        </div>
      </div>
      <div className="flex-1 overflow-hidden">
        <Editor
          height="100%"
          defaultLanguage="typst"
          theme={resolvedTheme === 'dark' ? 'vs-dark' : 'vs'}
          value={content}
          onChange={(val) => setContent(val ?? '')}
          beforeMount={registerTypstLanguage}
          onMount={editor => { editorRef.current = editor; foldFunctions(); }}
          options={{
            minimap: { enabled: false },
            fontSize: 13,
            fontFamily: "'Cascadia Code', 'Fira Code', Consolas, monospace",
            wordWrap: 'on',
            lineNumbers: 'on',
            scrollBeyondLastLine: false,
            automaticLayout: true,
            tabSize: 2,
            renderWhitespace: 'boundary',
          }}
        />
      </div>
      {/* Ассеты шаблонов (issue #62) — системный уровень, общий для всех шаблонов */}
      <TemplateAssetsPanel scope="System" scopeId={null} title="Системные ассеты" />
    </div>
  );
}

