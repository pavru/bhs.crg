import { useState, useEffect } from 'react';
import Editor from '@monaco-editor/react';
import { registerTypstLanguage } from '@/shared/ui/typstLanguage';
import { Save } from 'lucide-react';
import { useTypstUserLib, useSaveTypstUserLib } from '@/shared/api/typstUserLib';
import { TemplateAssetsPanel } from './TemplateAssetsPanel';
// ─── User Typst library panel ─────────────────────────────────────────────────

export function UserLibPanel() {
  const { data: serverContent = '', isLoading } = useTypstUserLib();
  const saveMutation = useSaveTypstUserLib();
  const [content, setContent] = useState('');
  const [savedMsg, setSavedMsg] = useState(false);
  const [error, setError] = useState('');

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
          <button onClick={handleSave} disabled={saveMutation.isPending}
            className="flex items-center gap-1.5 px-3 py-1.5 text-xs bg-brand hover:bg-brand-hover text-white rounded-md transition-colors disabled:opacity-50">
            <Save size={12} /> {saveMutation.isPending ? 'Сохранение...' : 'Сохранить'}
          </button>
        </div>
      </div>
      <div className="flex-1 overflow-hidden">
        <Editor
          height="100%"
          defaultLanguage="typst"
          value={content}
          onChange={(val) => setContent(val ?? '')}
          beforeMount={registerTypstLanguage}
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

