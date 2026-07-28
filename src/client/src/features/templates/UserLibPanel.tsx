import { useState, useEffect, useRef, useMemo, useCallback } from 'react';
import Editor from '@monaco-editor/react';
import type * as monacoEditor from 'monaco-editor';
import { registerTypstLanguage } from '@/shared/ui/typstLanguage';
import { useTheme } from '@/shared/ui/ThemeProvider';
import { useUserLibCompletion } from '@/shared/ui/typstUserLibCompletion';
import { useAssetCompletion } from '@/shared/ui/typstAssetCompletion';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { Save, AlertCircle, AlertTriangle } from 'lucide-react';
import {
  useTypstUserLib, useSaveTypstUserLib,
  type UserLibFile, type UserLibCheck,
} from '@/shared/api/typstUserLib';
import { TemplateAssetsPanel } from './TemplateAssetsPanel';
import { UserLibFileList } from './UserLibFileList';
import { UserLibPathDialog } from './UserLibPathDialog';
import { ENTRYPOINT, referencingFiles } from './userLibTree';

// ─── User Typst library: точка входа + дерево файлов (issue #473) ─────────────

/** Пустое дерево модульной константой: инлайновый `[]` — новая ссылка на каждый рендер (#305). */
const NO_FILES: UserLibFile[] = [];

export function UserLibPanel() {
  const { resolvedTheme } = useTheme();
  useUserLibCompletion();
  useAssetCompletion();
  const { data, isLoading } = useTypstUserLib();
  const saveMutation = useSaveTypstUserLib();

  const [entry, setEntry] = useState('');
  const [files, setFiles] = useState<UserLibFile[]>(NO_FILES);
  const [selected, setSelected] = useState(ENTRYPOINT);
  const [check, setCheck] = useState<UserLibCheck | null>(null);
  const [savedMsg, setSavedMsg] = useState(false);
  const [error, setError] = useState('');
  const [pathDialog, setPathDialog] = useState<{ mode: 'create' | 'rename'; path: string } | null>(null);
  const [deleting, setDeleting] = useState<string | null>(null);

  const editorRef = useRef<monacoEditor.editor.IStandaloneCodeEditor | null>(null);
  const foldedOnce = useRef(false);

  const serverEntry = data?.content ?? '';
  const serverFiles = data?.files ?? NO_FILES;

  useEffect(() => { setEntry(serverEntry); setFiles(serverFiles); }, [serverEntry, serverFiles]);

  // Несохранённые файлы — точка-маркер на строке, идиома вкладок редактора.
  const dirty = useMemo(() => {
    const set = new Set<string>();
    if (entry !== serverEntry) set.add(ENTRYPOINT);
    const byPath = new Map(serverFiles.map(f => [f.path, f.content]));
    for (const f of files) if (byPath.get(f.path) !== f.content) set.add(f.path);
    for (const f of serverFiles) if (!files.some(x => x.path === f.path)) set.add(f.path);
    return set;
  }, [entry, files, serverEntry, serverFiles]);

  const isDirty = dirty.size > 0;
  const currentContent = selected === ENTRYPOINT
    ? entry
    : files.find(f => f.path === selected)?.content ?? '';

  function updateCurrent(value: string) {
    if (selected === ENTRYPOINT) setEntry(value);
    else setFiles(prev => prev.map(f => (f.path === selected ? { ...f, content: value } : f)));
  }

  /**
   * Сохранение — ВСЕГО дерева разом. Пофайлового нет намеренно: правка файла и правка зовущего его
   * файла обязаны лечь вместе, иначе между двумя запросами библиотека не собирается, а её читает
   * генерация каждого документа.
   */
  const handleSave = useCallback(async () => {
    setError('');
    try {
      const res = await saveMutation.mutateAsync({ content: entry, files });
      setCheck(res.check);
      setSavedMsg(true);
      setTimeout(() => setSavedMsg(false), 2000);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка сохранения');
    }
  }, [entry, files, saveMutation]);

  // Ctrl+S — перехват на окне в фазе capture, чтобы опередить браузерное «Сохранить страницу», и по
  // e.code, потому что на русской раскладке e.key даёт «ы». Команду Monaco НЕ регистрируем: она
  // сработала бы вторым, параллельным сохранением.
  const saveRef = useRef(handleSave);
  saveRef.current = handleSave;
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if ((e.ctrlKey || e.metaKey) && e.code === 'KeyS') {
        e.preventDefault();
        if (editorRef.current?.hasTextFocus()) void saveRef.current();
      }
    }
    window.addEventListener('keydown', onKeyDown, { capture: true });
    return () => window.removeEventListener('keydown', onKeyDown, { capture: true });
  }, []);

  // Уход со страницы с несохранённым деревом — единственное место, где предупреждаем: переключение
  // между файлами это навигация внутри правки, а не коммит, и диалог на нём быстро обесценился бы.
  useEffect(() => {
    if (!isDirty) return;
    const onBeforeUnload = (e: BeforeUnloadEvent) => { e.preventDefault(); };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [isDirty]);

  // Библиотека читается как список функций; развёрнутой она простыня. Сворачиваем один раз при
  // первой загрузке — повтор схлопывал бы блок прямо во время правки.
  const foldFunctions = useCallback(() => {
    if (foldedOnce.current || !editorRef.current || !serverEntry.trim()) return;
    foldedOnce.current = true;
    const ed = editorRef.current;
    setTimeout(() => ed.getAction('editor.foldLevel1')?.run(), 0);
  }, [serverEntry]);
  useEffect(() => { foldFunctions(); }, [foldFunctions]);

  /** Ошибка в открытом файле — маркерами Monaco: там, где она есть. */
  useEffect(() => {
    const monaco = (window as unknown as { monaco?: typeof monacoEditor }).monaco;
    const model = editorRef.current?.getModel();
    if (!monaco || !model) return;
    const mine = (check?.errors ?? []).filter(e => e.path === selected);
    monaco.editor.setModelMarkers(model, 'userlib', mine.map(e => ({
      severity: monaco.MarkerSeverity.Error,
      message: e.message,
      startLineNumber: Math.max(1, e.line), startColumn: Math.max(1, e.column),
      endLineNumber: Math.max(1, e.line), endColumn: Math.max(1, e.column) + 1,
    })));
  }, [check, selected]);

  function handleCreate(path: string) {
    setFiles(prev => [...prev, { path, content: `// ${path}\n` }]);
    // Точку входа НЕ трогаем (issue #492): импорты — забота пользователя. Молчаливым этот отказ
    // не остаётся: проверка при сохранении называет неподключённый файл прямо.
    setSelected(path);
  }

  function handleRename(from: string, to: string) {
    setFiles(prev => prev.map(f => (f.path === from ? { ...f, path: to } : f)));
    if (selected === from) setSelected(to);
  }

  function handleDelete(path: string) {
    setFiles(prev => prev.filter(f => f.path !== path));
    if (selected === path) setSelected(ENTRYPOINT);
    setDeleting(null);
  }

  if (isLoading) {
    return <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Загрузка...</div>;
  }

  const errors = check?.errors ?? [];
  const warnings = check?.warnings ?? [];

  return (
    <div className="flex h-full min-h-0">
      <aside className="w-72 shrink-0 border-r border-stroke flex flex-col bg-base">
        <UserLibFileList
          files={files} selected={selected} dirty={dirty} check={check}
          onSelect={setSelected}
          onCreate={folder => setPathDialog({ mode: 'create', path: folder ? `${folder}/` : '' })}
          onRename={p => setPathDialog({ mode: 'rename', path: p })}
          onDelete={p => setDeleting(p)}
        />
        <TemplateAssetsPanel scope="System" scopeId={null} title="Системные ассеты" />
      </aside>

      <div className="flex-1 min-w-0 flex flex-col">
        <div className="flex items-center justify-between px-4 py-2 border-b border-stroke bg-surface gap-3">
          <p className="text-xs text-fg3 truncate">
            {selected === ENTRYPOINT ? (
              <>Доступен в каждом шаблоне через{' '}
                <code className="font-mono bg-muted px-1.5 py-0.5 rounded text-fg2">#import "userlib.typ": *</code>
              </>
            ) : (
              <>Файл библиотеки — <code className="font-mono bg-muted px-1.5 py-0.5 rounded text-fg2">userlib/{selected}</code></>
            )}
          </p>
          <div className="flex items-center gap-2 shrink-0">
            {savedMsg && !isDirty && <span className="text-xs text-success">Сохранено</span>}
            {error && <span className="text-xs text-danger max-w-xs truncate">{error}</span>}
            {isDirty && <span className="text-xs text-fg3">Изменено: {dirty.size}</span>}
            <Button variant="filled" size="sm" onClick={handleSave} loading={saveMutation.isPending}
              icon={<Save size={12} />}>
              {saveMutation.isPending ? 'Сохранение…' : 'Сохранить всё'}
            </Button>
          </div>
        </div>

        {/* Постоянное состояние, а не всплывающее сообщение: пока библиотека не собирается, стоит
            генерация ВСЕХ документов, и больше этого нигде не видно. Зелёной строки на каждое
            сохранение нет — «всё хорошо» каждый раз это шум. */}
        {errors.length > 0 && (
          <div className="px-4 py-2 bg-danger-subtle border-b border-stroke flex items-start gap-2">
            <AlertCircle size={14} className="text-danger shrink-0 mt-0.5" />
            <div className="min-w-0 text-xs">
              <p className="text-danger font-medium">Библиотека не собирается — генерация документов не пройдёт.</p>
              {errors.slice(0, 3).map((e, i) => (
                <button key={i} type="button" onClick={() => setSelected(e.path)}
                  className="block text-left text-fg2 hover:text-fg1 hover:underline">
                  {e.path}:{e.line} — {e.message}
                </button>
              ))}
              {errors.length > 3 && <p className="text-fg3">…и ещё {errors.length - 3}</p>}
            </div>
          </div>
        )}
        {errors.length === 0 && warnings.length > 0 && (
          <div className="px-4 py-2 bg-warning-subtle border-b border-stroke flex items-start gap-2">
            <AlertTriangle size={14} className="text-warning shrink-0 mt-0.5" />
            <div className="min-w-0 text-xs">
              {warnings.slice(0, 3).map((w, i) => (
                <button key={i} type="button" onClick={() => setSelected(w.path)}
                  className="block text-left text-fg2 hover:text-fg1 hover:underline">
                  {w.path} — {w.message}
                </button>
              ))}
              {warnings.length > 3 && <p className="text-fg3">…и ещё {warnings.length - 3}</p>}
            </div>
          </div>
        )}

        <div className="flex-1 min-h-0 overflow-hidden">
          <Editor
            key={selected}
            height="100%"
            defaultLanguage="typst"
            theme={resolvedTheme === 'dark' ? 'vs-dark' : 'vs'}
            value={currentContent}
            onChange={val => updateCurrent(val ?? '')}
            beforeMount={registerTypstLanguage}
            onMount={ed => { editorRef.current = ed; foldFunctions(); }}
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
      </div>

      {pathDialog && (
        <UserLibPathDialog
          mode={pathDialog.mode}
          initialPath={pathDialog.path}
          existing={files.map(f => f.path)}
          referencing={pathDialog.mode === 'rename' ? referencingFiles(files, pathDialog.path) : []}
          onCancel={() => setPathDialog(null)}
          onSubmit={path => {
            if (pathDialog.mode === 'create') handleCreate(path);
            else handleRename(pathDialog.path, path);
            setPathDialog(null);
          }}
        />
      )}

      <ConfirmDialog
        open={deleting != null}
        onOpenChange={open => { if (!open) setDeleting(null); }}
        title={`Удалить «${deleting ?? ''}»?`}
        description={
          deleting && referencingFiles(files, deleting).length > 0
            ? `На файл ссылаются: ${referencingFiles(files, deleting).join(', ')}. `
              + 'Пока импорт не убран, генерация ВСЕХ документов не пройдёт — библиотеку читает каждый шаблон.'
            : 'Строка подключения в точке входа будет убрана вместе с файлом. '
              + 'Изменение вступит в силу после сохранения.'
        }
        confirmLabel="Удалить"
        onConfirm={() => { if (deleting) handleDelete(deleting); }}
      />
    </div>
  );
}
