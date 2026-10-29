import { useState, useEffect, useLayoutEffect, useRef } from 'react';
import { useTheme } from '@/shared/ui/themeContext';
import { useUserLibCompletion } from '@/shared/ui/typstUserLibCompletion';
import { useAssetCompletion } from '@/shared/ui/typstAssetCompletion';
import Editor from '@/shared/ui/CodeEditor';
import { registerTypstLanguage } from '@/shared/ui/typstLanguage';
import { markTemplateModel } from '@/shared/ui/typstDispatchCompletion';
import { Button } from '@/shared/ui/Button';
import { Modal } from '@/shared/ui/Modal';
import { TextField } from '@/shared/ui/TextField';
import { useToast } from '@/shared/ui/Toast';
import { ruCount } from '@/shared/utils/pluralize';
import { Icon } from '@/shared/ui/Icon';
import { CallSplit, CheckCircle, HistoryIcon, KeyboardArrowDown, MenuBook, Save, Star } from '@/shared/ui/icons';
import type { Template, DocumentType } from '@/shared/api/types';
import { resolveEffectiveFields } from '@/shared/api/schema';
import { useSaveTemplateContent, useCreateTemplateVersion, useSetTemplateDefault } from '@/shared/api/templates';
import { TemplateParamsPanel } from './TemplateParamsPanel';
import { TemplateAssetsPanel } from './TemplateAssetsPanel';
import { flattenFields } from './templateBlank';
import type * as monacoEditor from 'monaco-editor';
import { Tooltip } from '@/shared/ui/Tooltip';

// ─── Toolbar button ───────────────────────────────────────────────────────────

function ToolbarButton({
  active, onClick, children, title,
}: { active?: boolean; onClick: () => void; children: React.ReactNode; title?: string }) {
  return (
    <Tooltip content={title}>
      <button type="button" onClick={onClick}
        className={`state-layer px-2 py-1 text-body-medium rounded-xs ${active ? 'bg-primary-container text-on-primary-container' : 'text-on-surface-variant'}`}>
        {children}
      </button>
    </Tooltip>
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
        <span className="flex items-center gap-1 text-body-small font-mono">
          <Icon icon={MenuBook} size={18} /> Реквизит
        </span>
      </ToolbarButton>
      {open && (
        <div className="absolute top-full left-0 mt-1 z-50 bg-surface border border-outline-variant rounded-md shadow-elevation-2 w-80 max-h-80 overflow-y-auto">
          <div className="px-3 py-2 border-b border-surface-container text-body-small font-semibold text-on-surface-variant uppercase tracking-wide sticky top-0 bg-surface">
            Реквизиты · {docType.name}
          </div>
          {fields.length === 0 ? (
            <p className="px-3 py-4 text-body-small text-on-surface-variant text-center">Нет реквизитов</p>
          ) : (
            <div className="py-1">
              {fields.map(f => (
                <button key={f.path} onClick={() => insertField(f.path)}
                  className={`w-full flex items-center gap-2 py-1.5 hover:bg-primary-container transition-colors text-left group ${f.type === 'complex' ? 'opacity-60 cursor-default pointer-events-none' : ''}`}
                  style={{ paddingLeft: `${0.75 + f.depth * 1}rem` }}>
                  <code className="text-body-small font-mono text-on-primary-container bg-primary-container group-hover:bg-primary-container px-1.5 py-0.5 rounded-xs shrink-0 max-w-[160px] truncate">
                    {f.path}
                  </code>
                  <span className="text-body-small text-on-surface-variant truncate flex-1">{f.title}</span>
                  <span className="text-body-small text-on-surface-variant shrink-0 pr-2">{f.type}</span>
                </button>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ─── New-version dialog ───────────────────────────────────────────────────────

function NewVersionDialog({ nextVersion, saving, onCancel, onConfirm }: {
  nextVersion: number;
  saving: boolean;
  onCancel: () => void;
  onConfirm: (comment: string) => void;
}) {
  const [comment, setComment] = useState('');
  return (
    <Modal open onOpenChange={o => { if (!o) onCancel(); }} title="Сохранить как новую версию"
      footer={
        <div className="flex justify-end gap-2">
          <Button type="button" variant="text" onClick={onCancel}>Отмена</Button>
          <Button type="button" variant="filled" loading={saving} icon={<Icon icon={CallSplit} size={18} />}
            onClick={() => onConfirm(comment)}>
            {saving ? 'Создание…' : `Создать v${nextVersion}`}
          </Button>
        </div>
      }>
      <div className="space-y-4 px-1">
        <p className="text-body-medium text-on-surface-variant">
          Будет создана <strong>версия v{nextVersion}</strong>. Текущая версия сохранится в истории.
        </p>
        <TextField label="Примечание к версии (необязательно)" value={comment}
          onChange={e => setComment(e.target.value)} autoFocus />
      </div>
    </Modal>
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
  useUserLibCompletion();
  useAssetCompletion();
  const [content, setContent] = useState(template.content);
  const [saving, setSaving] = useState(false);
  const [savedMsg, setSavedMsg] = useState(false);
  const [error, setError] = useState('');
  const [saveMenuOpen, setSaveMenuOpen] = useState(false);
  const [newVersionOpen, setNewVersionOpen] = useState(false);
  const saveMenuRef = useRef<HTMLDivElement>(null);
  const editorRef = useRef<monacoEditor.editor.IStandaloneCodeEditor | null>(null);
  const saveContentMutation = useSaveTemplateContent();
  const newVersionMutation = useCreateTemplateVersion();
  const defaultMutation = useSetTemplateDefault();
  const toast = useToast();

  // Неблокирующее предупреждение: сколько документов ушло в черновик из-за устаревшего PDF (issue #362).
  function notifyReset(count: number) {
    if (count > 0)
      toast.info(`${ruCount(count, 'документ', 'документа', 'документов')} переведено в черновик — PDF устарел`);
  }

  // Есть ли несохранённые правки (dirty). После сохранения onSaved() подставляет
  // обновлённый шаблон с тем же content → снова false.
  const dirty = content !== template.content;

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

  // Простое сохранение (Ctrl+S) — правит текущую версию на месте. Историческую
  // (неактивную) версию так править нельзя — её можно только форкнуть.
  async function handleSave() {
    if (savingRef.current) return; // a save is already in flight — ignore duplicate trigger
    if (!template.isActive) {
      setError('Историческая версия — сохраните как новую версию');
      return;
    }
    // Read live content from Monaco so this function is safe to call from the
    // window-level Ctrl+S handler (avoids stale-closure on `content`).
    const currentContent = editorRef.current?.getValue() ?? content;
    savingRef.current = true;
    setError('');
    setSaving(true);
    try {
      const res = await saveContentMutation.mutateAsync({ id: template.id, content: currentContent });
      setSavedMsg(true);
      setTimeout(() => setSavedMsg(false), 2000);
      justSavedRef.current = true; // prevent useEffect from resetting cursor
      onSaved(res.template);
      notifyReset(res.resetDocuments);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка сохранения');
    } finally {
      setSaving(false);
      savingRef.current = false;
    }
  }

  // Явное «Сохранить как новую версию» — форк новой версии, опц. с примечанием.
  async function handleSaveAsNewVersion(comment: string) {
    if (savingRef.current) return;
    const currentContent = editorRef.current?.getValue() ?? content;
    savingRef.current = true;
    setError('');
    setSaving(true);
    try {
      const res = await newVersionMutation.mutateAsync({
        id: template.id, content: currentContent, comment: comment.trim() || null,
      });
      setSavedMsg(true);
      setTimeout(() => setSavedMsg(false), 2000);
      justSavedRef.current = true;
      onSaved(res.template);
      notifyReset(res.resetDocuments);
      setNewVersionOpen(false);
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
  // Эффект СЛОЙНЫЙ: обычный отложен до после отрисовки, и в щель между коммитом и ним Ctrl+S молча
  // сохранил бы прежнее содержимое шаблона (issue #858).
  useLayoutEffect(() => { handleSaveRef.current = handleSave; });

  // Закрытие меню сохранения по клику вне.
  useEffect(() => {
    if (!saveMenuOpen) return;
    function onClick(e: MouseEvent) {
      if (saveMenuRef.current && !saveMenuRef.current.contains(e.target as Node)) setSaveMenuOpen(false);
    }
    document.addEventListener('mousedown', onClick);
    return () => document.removeEventListener('mousedown', onClick);
  }, [saveMenuOpen]);

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
    // Имена из typeblocks.typ (render-by-type, type-renders) доступны ТОЛЬКО шаблону — редактор
    // блоков и библиотека их не видят. Помечаем модель, чтобы автокомплит #768 не предлагал их там,
    // где вызов упадёт на генерации (см. typstDispatchCompletion).
    markTemplateModel(editor.getModel());
    // Ctrl+S is handled by the window-level capture listener above. We intentionally
    // do NOT register a Monaco command for it: that fired a second, concurrent save
    // (the window handler preventDefaults but doesn't stopPropagation), branching the
    // version history into duplicate same-numbered versions.
  }

  async function handleSetDefault() {
    const res = await defaultMutation.mutateAsync({ id: template.id, documentTypeId: template.documentTypeId });
    onSaved(res.template);
    notifyReset(res.resetDocuments);
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
      <div className="flex items-center justify-between px-4 py-2 border-b border-outline-variant bg-surface gap-3 flex-wrap">
        <div className="flex items-center gap-1 flex-wrap">
          <RequisitePicker docType={docType} allDocTypes={allDocTypes} onInsert={insertAtCursor} />
          <span className="w-px h-5 bg-outline-variant mx-1" />
          <a
            href="https://typst.app/docs"
            target="_blank"
            rel="noopener noreferrer"
            className="px-2 py-1 text-body-small text-on-surface-variant hover:text-primary state-layer rounded-xs"
          >
            Typst docs ↗
          </a>
        </div>
        <div className="flex items-center gap-2">
          {/* Индикатор версии + dirty-точка */}
          <Tooltip content={
            dirty ? 'Есть несохранённые правки' : 'Сохранено'
          }>
            <span className="text-body-small text-on-surface-variant flex items-center gap-1">
              v{template.version}
              {dirty
                ? <span className="w-1.5 h-1.5 rounded-full bg-warning" aria-label="есть правки" />
                : <span className="w-1.5 h-1.5 rounded-full bg-success/60" aria-label="сохранено" />}
            </span>
          </Tooltip>
          {template.isActive ? (
            <span className="text-body-small text-success flex items-center gap-1"><Icon icon={CheckCircle} size={18} /> активный</span>
          ) : (
            <Tooltip content="Не активная версия — правится только форком в новую">
              <span className="text-body-small text-on-surface-variant flex items-center gap-1">
                <Icon icon={HistoryIcon} size={18} /> историческая
              </span>
            </Tooltip>
          )}
          {template.isDefault ? (
            <span className="text-body-small text-warning flex items-center gap-1">
              <Icon icon={Star} size={18} className="fill-warning text-warning" /> по умолчанию
            </span>
          ) : (
            <button onClick={handleSetDefault} disabled={defaultMutation.isPending}
              className="flex items-center gap-1 px-2 py-1 text-body-small border border-outline-variant text-warning hover:bg-warning-container rounded-xs disabled-outlined transition-colors">
              <Icon icon={Star} size={18} /> Сделать по умолчанию
            </button>
          )}
          {savedMsg && <span className="text-body-small text-success">Сохранено ✓</span>}
          {error && <span className="text-body-small text-error max-w-xs truncate" title={error}>{error}</span>}

          {/* Сплит-кнопка: слева in-place (Ctrl+S), справа каретка → меню «новая версия» */}
          <div className="relative flex items-center" ref={saveMenuRef}>
            {template.isActive ? (
              <Tooltip content="Сохранить в текущую версию (Ctrl+S)">
                <button type="button" onClick={handleSave} disabled={saving}
                  className="inline-flex items-center gap-1.5 h-8 pl-3 pr-2.5 text-body-small font-medium rounded-l-full bg-primary text-on-primary shadow-elevation-1 state-layer disabled-container">
                  <Icon icon={Save} size={18} /> {saving ? 'Сохранение…' : 'Сохранить'}
                </button>
              </Tooltip>
            ) : (
              // Историческую версию in-place не сохранить — левая кнопка ведёт на форк.
              <Tooltip content="Историческая версия — сохранить как новую">
                <button type="button" onClick={() => setNewVersionOpen(true)} disabled={saving}
                  className="inline-flex items-center gap-1.5 h-8 pl-3 pr-2.5 text-body-small font-medium rounded-l-full bg-primary text-on-primary shadow-elevation-1 state-layer disabled-container">
                  <Icon icon={CallSplit} size={18} /> Сохранить как новую
                </button>
              </Tooltip>
            )}
            <Tooltip content="Ещё варианты сохранения" name>
              <button type="button" onClick={() => setSaveMenuOpen(v => !v)} disabled={saving}
                className="inline-flex items-center h-8 px-1.5 rounded-r-full border-l border-on-primary/25 bg-primary text-on-primary shadow-elevation-1 state-layer disabled-container">
                <Icon icon={KeyboardArrowDown} size={18} />
              </button>
            </Tooltip>
            {saveMenuOpen && (
              <div className="absolute top-full right-0 mt-1 z-50 w-72 bg-surface border border-outline-variant rounded-md shadow-elevation-2 py-1">
                <button type="button"
                  onClick={() => { setSaveMenuOpen(false); setNewVersionOpen(true); }}
                  className="w-full flex items-start gap-2 px-3 py-2 text-left state-layer">
                  <Icon icon={CallSplit} size={18} className="text-primary shrink-0 mt-0.5" />
                  <span>
                    <span className="block text-body-medium text-on-surface">Сохранить как новую версию…</span>
                    <span className="block text-body-small text-on-surface-variant">Создаст v{template.version + 1}, сохранит историю текущей</span>
                  </span>
                </button>
              </div>
            )}
          </div>
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

      {newVersionOpen && (
        <NewVersionDialog
          nextVersion={template.version + 1}
          saving={saving}
          onCancel={() => setNewVersionOpen(false)}
          onConfirm={handleSaveAsNewVersion}
        />
      )}
    </div>
  );
}
