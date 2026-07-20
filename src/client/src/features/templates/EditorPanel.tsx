import { useState, useEffect, useRef } from 'react';
import { useTheme } from '@/shared/ui/ThemeProvider';
import Editor from '@monaco-editor/react';
import { registerTypstLanguage } from '@/shared/ui/typstLanguage';
import { Button } from '@/shared/ui/Button';
import { BookOpen, Save, Star, CheckCircle } from 'lucide-react';
import type { Template, DocumentType } from '@/shared/api/types';
import { resolveEffectiveFields } from '@/shared/api/schema';
import { useUpdateTemplate, useSetTemplateDefault } from '@/shared/api/templates';
import { TemplateParamsPanel } from './TemplateParamsPanel';
import { TemplateAssetsPanel } from './TemplateAssetsPanel';
import { flattenFields } from './templateBlank';
import type * as monacoEditor from 'monaco-editor';

// ─── Toolbar button ───────────────────────────────────────────────────────────

function ToolbarButton({
  active, onClick, children, title,
}: { active?: boolean; onClick: () => void; children: React.ReactNode; title?: string }) {
  return (
    <button type="button" onClick={onClick} title={title}
      className={`px-2 py-1 text-sm rounded transition-colors ${active ? 'bg-brand-subtle text-brand-hover' : 'text-fg2 hover:bg-muted'}`}>
      {children}
    </button>
  );
}

// ─── Requisite picker ─────────────────────────────────────────────────────────

function RequisitePicker({ docType, allDocTypes, onInsert }: {
  docType: DocumentType; allDocTypes: DocumentType[];
  onInsert: (snippet: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const topFields = resolveEffectiveFields(docType, allDocTypes);
  const fields = flattenFields(topFields, allDocTypes);

  useEffect(() => {
    if (!open) return;
    function handleClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [open]);

  function insertField(path: string) {
    onInsert(`#get("${path}")`);
    setOpen(false);
  }

  return (
    <div className="relative" ref={ref}>
      <ToolbarButton active={open} onClick={() => setOpen(v => !v)} title="Вставить реквизит">
        <span className="flex items-center gap-1 text-xs font-mono">
          <BookOpen size={12} /> Реквизит
        </span>
      </ToolbarButton>
      {open && (
        <div className="absolute top-full left-0 mt-1 z-50 bg-surface border border-stroke rounded-lg shadow-lg w-80 max-h-80 overflow-y-auto">
          <div className="px-3 py-2 border-b border-muted text-xs font-semibold text-fg3 uppercase tracking-wide sticky top-0 bg-surface">
            Реквизиты · {docType.name}
          </div>
          {fields.length === 0 ? (
            <p className="px-3 py-4 text-xs text-fg4 text-center">Нет реквизитов</p>
          ) : (
            <div className="py-1">
              {fields.map(f => (
                <button key={f.path} onClick={() => insertField(f.path)}
                  className={`w-full flex items-center gap-2 py-1.5 hover:bg-brand-subtle transition-colors text-left group ${f.type === 'complex' ? 'opacity-60 cursor-default pointer-events-none' : ''}`}
                  style={{ paddingLeft: `${0.75 + f.depth * 1}rem` }}>
                  <code className="text-xs font-mono text-brand-hover bg-brand-subtle group-hover:bg-brand-subtle px-1.5 py-0.5 rounded shrink-0 max-w-[160px] truncate">
                    {f.path}
                  </code>
                  <span className="text-xs text-fg3 truncate flex-1">{f.title}</span>
                  <span className="text-xs text-stroke-strong shrink-0 pr-2">{f.type}</span>
                </button>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ─── Editor panel ─────────────────────────────────────────────────────────────

interface EditorPanelProps {
  template: Template;
  docType: DocumentType;
  allDocTypes: DocumentType[];
  onSaved: (updated: Template) => void;
}

export function EditorPanel({ template, docType, allDocTypes, onSaved }: EditorPanelProps) {
  const { resolvedTheme } = useTheme();
  const [content, setContent] = useState(template.content);
  const [saving, setSaving] = useState(false);
  const [savedMsg, setSavedMsg] = useState(false);
  const [error, setError] = useState('');
  const editorRef = useRef<monacoEditor.editor.IStandaloneCodeEditor | null>(null);
  const updateMutation = useUpdateTemplate();
  const defaultMutation = useSetTemplateDefault();

  // When true, the next template.id change came from our own save — skip content reset
  // so Monaco keeps its cursor position and scroll offset.
  const justSavedRef = useRef(false);

  // Re-entrancy guard: blocks a second save while one is in flight. State alone is
  // async, so a synchronous double-trigger (e.g. Ctrl+S) could fire two PUTs before
  // `saving` updates — that branched the version history into duplicates.
  const savingRef = useRef(false);

  useEffect(() => {
    if (justSavedRef.current) {
      justSavedRef.current = false;
      return;
    }
    setContent(template.content);
  }, [template.id]); // eslint-disable-line react-hooks/exhaustive-deps

  async function handleSave() {
    if (savingRef.current) return; // a save is already in flight — ignore duplicate trigger
    // Read live content from Monaco so this function is safe to call from the
    // window-level Ctrl+S handler (avoids stale-closure on `content`).
    const currentContent = editorRef.current?.getValue() ?? content;
    savingRef.current = true;
    setError('');
    setSaving(true);
    try {
      const updated = await updateMutation.mutateAsync({ id: template.id, content: currentContent });
      setSavedMsg(true);
      setTimeout(() => setSavedMsg(false), 2000);
      justSavedRef.current = true; // prevent useEffect from resetting cursor
      onSaved(updated);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка сохранения');
    } finally {
      setSaving(false);
      savingRef.current = false;
    }
  }

  // Keep a stable ref so the Monaco command (registered once at mount) always
  // calls the latest handleSave without capturing a stale closure.
  const handleSaveRef = useRef(handleSave);
  useEffect(() => { handleSaveRef.current = handleSave; });

  // Prevent the browser's native Ctrl+S / Cmd+S ("Save page") using capture phase —
  // this runs before any other handler, including the browser's own shortcut.
  // Trigger save only when Monaco has text focus.
  // Use e.code (physical key) so it works on non-Latin layouts — on a Russian
  // layout Ctrl+S gives e.key === 'ы', so an e.key === 's' check would never fire.
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if ((e.ctrlKey || e.metaKey) && e.code === 'KeyS') {
        e.preventDefault();
        if (editorRef.current?.hasTextFocus()) {
          void handleSaveRef.current();
        }
      }
    }
    window.addEventListener('keydown', onKeyDown, { capture: true });
    return () => window.removeEventListener('keydown', onKeyDown, { capture: true });
  }, []);

  function handleEditorMount(editor: monacoEditor.editor.IStandaloneCodeEditor) {
    editorRef.current = editor;
    // Ctrl+S is handled by the window-level capture listener above. We intentionally
    // do NOT register a Monaco command for it: that fired a second, concurrent save
    // (the window handler preventDefaults but doesn't stopPropagation), branching the
    // version history into duplicate same-numbered versions.
  }

  async function handleSetDefault() {
    const updated = await defaultMutation.mutateAsync({ id: template.id, documentTypeId: template.documentTypeId });
    onSaved(updated);
  }

  function insertAtCursor(snippet: string) {
    const editor = editorRef.current;
    if (!editor) {
      setContent(prev => prev + snippet);
      return;
    }
    const selection = editor.getSelection();
    const op = selection
      ? { range: selection, text: snippet, forceMoveMarkers: true }
      : { range: editor.getModel()!.getFullModelRange(), text: content + snippet, forceMoveMarkers: true };
    editor.executeEdits('insert-requisite', [op]);
    editor.focus();
  }

  return (
    <div className="flex flex-col h-full">
      {/* Toolbar */}
      <div className="flex items-center justify-between px-4 py-2 border-b border-stroke bg-surface gap-3 flex-wrap">
        <div className="flex items-center gap-1 flex-wrap">
          <RequisitePicker docType={docType} allDocTypes={allDocTypes} onInsert={insertAtCursor} />
          <span className="w-px h-5 bg-stroke mx-1" />
          <a
            href="https://typst.app/docs"
            target="_blank"
            rel="noopener noreferrer"
            className="px-2 py-1 text-xs text-fg3 hover:text-brand hover:bg-muted rounded transition-colors"
          >
            Typst docs ↗
          </a>
        </div>
        <div className="flex items-center gap-2">
          <span className="text-xs text-fg4">v{template.version}</span>
          {template.isActive && (
            <span className="text-xs text-success flex items-center gap-1"><CheckCircle size={12} /> активный</span>
          )}
          {template.isDefault ? (
            <span className="text-xs text-yellow-600 flex items-center gap-1">
              <Star size={11} className="fill-yellow-400 text-yellow-400" /> по умолчанию
            </span>
          ) : (
            <button onClick={handleSetDefault} disabled={defaultMutation.isPending}
              className="flex items-center gap-1 px-2 py-1 text-xs border border-yellow-300 text-warning hover:bg-yellow-50 rounded disabled:opacity-50 transition-colors">
              <Star size={11} /> Сделать по умолчанию
            </button>
          )}
          {savedMsg && <span className="text-xs text-success">Сохранено ✓</span>}
          {error && <span className="text-xs text-danger max-w-xs truncate">{error}</span>}
          <Button variant="filled" size="sm" onClick={handleSave} loading={saving}
            icon={<Save size={12} />} title="Сохранить (Ctrl+S)">
            {saving ? 'Сохранение…' : 'Сохранить'}
          </Button>
        </div>
      </div>

      {/* Monaco editor */}
      <div className="flex-1 overflow-hidden">
        <Editor
          height="100%"
          defaultLanguage="typst"
          theme={resolvedTheme === 'dark' ? 'vs-dark' : 'vs'}
          value={content}
          onChange={(val) => setContent(val ?? '')}
          beforeMount={registerTypstLanguage}
          onMount={handleEditorMount}
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

      {/* Параметры шаблона (key — чтобы стейт переинициализировался при смене шаблона) */}
      <TemplateParamsPanel key={template.id} template={template} onSaved={onSaved} />
      {/* Ассеты шаблона (issue #62) — индивидуальный уровень, scoped к этой версии шаблона */}
      <TemplateAssetsPanel
        key={`assets-${template.id}`}
        scope="Template" scopeId={template.id} title="Ассеты шаблона"
        hintScopes={[
          { scope: 'DocumentType', scopeId: docType.id, label: 'на уровне типа' },
          { scope: 'System', scopeId: null, label: 'системных' },
        ]}
      />
    </div>
  );
}
