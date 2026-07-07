import { useState, useEffect, useRef } from 'react';
import Editor from '@monaco-editor/react';
import { registerTypstLanguage } from '@/shared/ui/typstLanguage';
import { BookOpen, Settings, Save, Star, ChevronDown, ChevronUp, CheckCircle } from 'lucide-react';
import type { Template, DocumentType } from '@/shared/api/types';
import { resolveEffectiveFields } from '@/shared/api/schema';
import { useUpdateTemplate, useUpdateTemplateSettings, useSetTemplateDefault } from '@/shared/api/templates';
import { TemplateParamsPanel } from './TemplateParamsPanel';
import { flattenFields } from './templateBlank';
import type * as monacoEditor from 'monaco-editor';

const ISO_SIZES = ['A0', 'A1', 'A2', 'A3', 'A4', 'A5', 'A6'] as const;
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

// ─── Page settings panel ──────────────────────────────────────────────────────

function PageSettingsPanel({ template, onSaved }: { template: Template; onSaved: (t: Template) => void }) {
  const [open, setOpen] = useState(false);
  const [pageSize, setPageSize] = useState(template.pageSize);
  const [orientation, setOrientation] = useState(template.pageOrientation);
  const [marginTop, setMarginTop] = useState(template.marginTop);
  const [marginRight, setMarginRight] = useState(template.marginRight);
  const [marginBottom, setMarginBottom] = useState(template.marginBottom);
  const [marginLeft, setMarginLeft] = useState(template.marginLeft);
  const [saved, setSaved] = useState(false);

  const settingsMutation = useUpdateTemplateSettings();
  const defaultMutation = useSetTemplateDefault();

  useEffect(() => {
    setPageSize(template.pageSize);
    setOrientation(template.pageOrientation);
    setMarginTop(template.marginTop);
    setMarginRight(template.marginRight);
    setMarginBottom(template.marginBottom);
    setMarginLeft(template.marginLeft);
  }, [template.id]);

  async function handleSaveSettings() {
    const updated = await settingsMutation.mutateAsync({
      id: template.id, documentTypeId: template.documentTypeId,
      pageSize, pageOrientation: orientation,
      marginTop, marginRight, marginBottom, marginLeft,
    });
    setSaved(true);
    setTimeout(() => setSaved(false), 2000);
    onSaved(updated);
  }

  async function handleSetDefault() {
    const updated = await defaultMutation.mutateAsync({ id: template.id, documentTypeId: template.documentTypeId });
    onSaved(updated);
  }

  const inputCls = 'w-full border border-stroke-strong rounded px-2 py-1 text-sm text-fg1 bg-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-brand';

  return (
    <div className="border-t border-stroke bg-surface">
      <button onClick={() => setOpen(v => !v)}
        className="w-full flex items-center gap-2 px-4 py-2.5 hover:bg-base transition-colors text-left">
        <Settings size={13} className="text-fg4" />
        <span className="text-xs font-medium text-fg2 flex-1">Настройки страницы</span>
        {open ? <ChevronUp size={13} className="text-fg4" /> : <ChevronDown size={13} className="text-fg4" />}
      </button>
      {open && (
        <div className="px-4 pb-4 space-y-4 border-t border-muted">
          <div className="grid grid-cols-2 gap-3 pt-3">
            <div>
              <label className="block text-xs font-medium text-fg2 mb-1">Формат (ISO)</label>
              <select value={pageSize} onChange={e => setPageSize(e.target.value)} className={inputCls}>
                {ISO_SIZES.map(s => <option key={s} value={s}>{s}</option>)}
              </select>
            </div>
            <div>
              <label className="block text-xs font-medium text-fg2 mb-1">Ориентация (по умолч.)</label>
              <div className="flex gap-2 mt-1.5">
                {(['portrait', 'landscape'] as const).map(o => (
                  <label key={o} className="flex items-center gap-1.5 cursor-pointer">
                    <input type="radio" name={`orient-${template.id}`} value={o}
                      checked={orientation === o} onChange={() => setOrientation(o)}
                      className="w-3.5 h-3.5 text-brand" />
                    <span className="text-xs text-fg2">{o === 'portrait' ? 'Книжная' : 'Альбомная'}</span>
                  </label>
                ))}
              </div>
            </div>
          </div>
          <div>
            <label className="block text-xs font-medium text-fg2 mb-2">Поля (мм)</label>
            <div className="grid grid-cols-4 gap-2">
              {([['Верх', marginTop, setMarginTop], ['Право', marginRight, setMarginRight],
                 ['Низ', marginBottom, setMarginBottom], ['Лево', marginLeft, setMarginLeft]] as [string, number, (v: number) => void][]).map(([label, val, set]) => (
                <div key={label}>
                  <label className="block text-xs text-fg3 mb-1 text-center">{label}</label>
                  <input type="number" min={0} max={100} value={val}
                    onChange={e => set(Number(e.target.value))}
                    className={inputCls + ' text-center'} />
                </div>
              ))}
            </div>
          </div>
          <div className="flex items-center gap-3">
            <button onClick={handleSaveSettings} disabled={settingsMutation.isPending}
              className="flex items-center gap-1.5 px-3 py-1.5 text-xs bg-brand hover:bg-brand-hover text-white rounded disabled:opacity-50 transition-colors">
              <Save size={11} /> {settingsMutation.isPending ? 'Сохранение...' : 'Сохранить настройки'}
            </button>
            {saved && <span className="text-xs text-success">Сохранено</span>}
            {!template.isDefault && (
              <button onClick={handleSetDefault} disabled={defaultMutation.isPending}
                className="flex items-center gap-1.5 px-3 py-1.5 text-xs border border-yellow-300 text-warning hover:bg-yellow-50 rounded disabled:opacity-50 transition-colors ml-auto">
                <Star size={11} /> Сделать шаблоном по умолчанию
              </button>
            )}
            {template.isDefault && (
              <span className="ml-auto flex items-center gap-1.5 text-xs text-yellow-600">
                <Star size={11} className="fill-yellow-400 text-yellow-400" /> Шаблон по умолчанию
              </span>
            )}
          </div>
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
  const [content, setContent] = useState(template.content);
  const [saving, setSaving] = useState(false);
  const [savedMsg, setSavedMsg] = useState(false);
  const [error, setError] = useState('');
  const editorRef = useRef<monacoEditor.editor.IStandaloneCodeEditor | null>(null);
  const updateMutation = useUpdateTemplate();

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
          {template.isDefault && (
            <span className="text-xs text-yellow-600 flex items-center gap-1">
              <Star size={11} className="fill-yellow-400 text-yellow-400" /> по умолчанию
            </span>
          )}
          {savedMsg && <span className="text-xs text-success">Сохранено ✓</span>}
          {error && <span className="text-xs text-danger max-w-xs truncate">{error}</span>}
          <button onClick={handleSave} disabled={saving}
            title="Сохранить (Ctrl+S)"
            className="flex items-center gap-1.5 px-3 py-1.5 text-xs bg-brand hover:bg-brand-hover text-white rounded-md transition-colors disabled:opacity-50">
            <Save size={12} /> {saving ? 'Сохранение...' : 'Сохранить'}
          </button>
        </div>
      </div>

      {/* Monaco editor */}
      <div className="flex-1 overflow-hidden">
        <Editor
          height="100%"
          defaultLanguage="typst"
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

      {/* Page settings */}
      <PageSettingsPanel template={template} onSaved={onSaved} />
      {/* Параметры шаблона (key — чтобы стейт переинициализировался при смене шаблона) */}
      <TemplateParamsPanel key={template.id} template={template} onSaved={onSaved} />
    </div>
  );
}
