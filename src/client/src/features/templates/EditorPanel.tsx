import { useState, useEffect, useLayoutEffect, useRef } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useUserLibCompletion } from '@/shared/ui/typstUserLibCompletion';
import { useAssetCompletion } from '@/shared/ui/typstAssetCompletion';
import Editor from '@/shared/ui/CodeEditor';
import { registerTypstLanguage } from '@/shared/ui/typstLanguage';
import { markTemplateModel } from '@/shared/ui/typstDispatchCompletion';
import { Button } from '@/shared/ui/Button';
import { SplitButton } from '@/shared/ui/SplitButton';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { TextField } from '@/shared/ui/TextField';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { ruCount, ruPlural } from '@/shared/utils/pluralize';
import { Icon } from '@/shared/ui/Icon';
import { CallSplit, CheckCircle, HistoryIcon, MenuBook, Save, Star } from '@/shared/ui/icons';
import type { Template, DocumentType } from '@/shared/api/types';
import { resolveEffectiveFields } from '@/shared/api/schema';
import { useSaveTemplateContent, useCreateTemplateVersion, useSetTemplateDefault } from '@/shared/api/templates';
import { TemplateParamsPanel } from './TemplateParamsPanel';
import { TemplateAssetsPanel } from './TemplateAssetsPanel';
import { flattenFields } from './templateBlank';
import type { NewVersionPlan } from './groupTemplates';
import type * as monacoEditor from 'monaco-editor';
import { Tooltip } from '@/shared/ui/Tooltip';
import { Menu, MenuItem, MenuLabel, MenuNote, MenuSeparator } from '@/shared/ui/Menu';

// ─── Requisite picker ─────────────────────────────────────────────────────────

function RequisitePicker({ docType, allDocTypes, onInsert }: {
  docType: DocumentType; allDocTypes: DocumentType[];
  onInsert: (snippet: string) => void;
}) {
  const topFields = resolveEffectiveFields(docType, allDocTypes);
  const fields = flattenFields(topFields, allDocTypes);
  // Вставка — после закрытия меню: фокус в редактор ставит она, а не Radix на кнопку ([UI-51]).
  const chosen = useRef<string | null>(null);

  return (
    <Menu dense tooltip="Вставить реквизит" collisionPadding={8} width="w-80" maxHeight="20rem"
      trigger={
        <button type="button"
          className="state-layer px-2 py-1 text-body-medium rounded-xs text-on-surface-variant data-[state=open]:bg-primary-container data-[state=open]:text-on-primary-container">
          <span className="flex items-center gap-1 text-body-small font-mono">
            <Icon icon={MenuBook} size={18} /> Реквизит
          </span>
        </button>
      }
      onCloseAutoFocus={e => {
        const path = chosen.current;
        // Закрыли без выбора — фокус на «Реквизит» Radix вернёт сам (подсказку на нём гасит `Menu`).
        if (path == null) return;
        chosen.current = null;
        e.preventDefault();
        onInsert(`#get("${path}")`);
      }}>
      <MenuLabel>Реквизиты · {docType.name}</MenuLabel>
      <MenuSeparator />
      {fields.length === 0 ? <MenuNote>Нет реквизитов</MenuNote> : fields.map(f => {
        const pad = { paddingLeft: `${0.75 + f.depth * 1}rem` };
        const path = (
          <code className="text-body-small font-mono text-on-primary-container bg-primary-container px-1.5 py-0.5 rounded-xs">
            {f.path}
          </code>
        );
        // Составное поле — заголовок группы: выбрать его нечего, стрелки его перешагивают.
        return f.type === 'complex'
          ? <MenuLabel key={f.path} style={pad}>{path} {f.title}</MenuLabel>
          : <MenuItem key={f.path} style={pad} description={f.title} trailing={f.type}
              onSelect={() => { chosen.current = f.path; }}>{path}</MenuItem>;
      })}
    </Menu>
  );
}

// ─── New-version dialog ───────────────────────────────────────────────────────

function NewVersionDialog({ plan, saving, onCancel, onConfirm }: {
  plan: NewVersionPlan;
  saving: boolean;
  onCancel: () => void;
  onConfirm: (comment: string) => void;
}) {
  const [comment, setComment] = useState('');
  const retired = plan.retires.map(t => `v${t.version}`).join(', ');
  // Закреплённые за гаснущими версиями следуют за шаблоном (#1923, вариант А) — сервер их пересадит.
  const one = ruPlural(plan.pinnedToRetired, 'one', 'few', 'many') === 'one';
  const pinnedMove = plan.pinnedToRetired > 0 && (
    <> {ruCount(plan.pinnedToRetired, 'документ', 'документа', 'документов')}{' '}
      {one ? 'перейдёт' : 'перейдут'} на новую версию и {one ? 'вернётся' : 'вернутся'} в черновик.</>);
  return (
    <Modal open onOpenChange={o => { if (!o) onCancel(); }} title="Сохранить как новую версию"
      footer={
        <>
          <ModalExit />
          <ModalPrimary loading={saving} icon={<Icon icon={CallSplit} size={18} />}
            onClick={() => onConfirm(comment)}>
            {saving ? 'Создание…' : `Создать v${plan.next}`}
          </ModalPrimary>
        </>}>
      <div className="space-y-4 px-1">
        {plan.retires.length === 0 ? (
          <p className="text-body-medium text-on-surface-variant">
            Будет создана <strong>версия v{plan.next}</strong>. Текущая версия сохранится в истории.
            {pinnedMove}
          </p>
        ) : (
          // От исторической версии гаснет не она, а действующая (issue #1920): об этом и говорим.
          <p className="text-body-medium text-on-surface-variant">
            Будет создана <strong>версия v{plan.next}</strong> из этой. Действующая {retired} уйдёт в историю
            {plan.takesDefault && <> и передаст v{plan.next} признак «по умолчанию»</>}.
            {plan.noPinDocsMove && <> Документы без выбранного шаблона перейдут на v{plan.next} и вернутся в черновик.</>}
            {pinnedMove}
          </p>
        )}
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
  /**
   * Что сделает «новая версия» (issue #1920): номер — наибольший у шаблона плюс один, а не номер
   * открытой плюс один, и какие действующие версии погаснут. Считает страница: все версии шаблона
   * видит она, а не редактор одной.
   */
  versionPlan: NewVersionPlan;
}

export function EditorPanel({ template, docType, allDocTypes, onSaved, versionPlan }: EditorPanelProps) {
  useUserLibCompletion();
  useAssetCompletion();
  const qc = useQueryClient();
  const [content, setContent] = useState(template.content);
  const [saving, setSaving] = useState(false);
  const [savedMsg, setSavedMsg] = useState(false);
  const [error, setError] = useState('');
  const [newVersionOpen, setNewVersionOpen] = useState(false);
  const editorRef = useRef<monacoEditor.editor.IStandaloneCodeEditor | null>(null);
  const saveContentMutation = useSaveTemplateContent();
  const newVersionMutation = useCreateTemplateVersion();
  const defaultMutation = useSetTemplateDefault();
  const toast = useSnackbar();

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

  // Номер и гаснущие версии окно берёт из списка страницы, а его мог обогнать другой
  // администратор: при открытии окна список перечитывается, и окно показывает свежий план.
  function openNewVersion() {
    void qc.invalidateQueries({ queryKey: ['templates', template.documentTypeId] });
    void qc.invalidateQueries({ queryKey: ['template-usage', template.documentTypeId] });
    setNewVersionOpen(true);
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

          {template.isActive ? (
            <SplitButton onClick={handleSave} loading={saving} icon={<Icon icon={Save} size={18} />}
              title="Сохранить в текущую версию (Ctrl+S)" menuLabel="Ещё варианты сохранения"
              items={[{
                key: 'new-version',
                label: 'Сохранить как новую версию…',
                description: `Создаст v${versionPlan.next}, сохранит историю текущей`,
                icon: CallSplit,
                onSelect: openNewVersion,
              }]}>
              {saving ? 'Сохранение…' : 'Сохранить'}
            </SplitButton>
          ) : (
            // Историческую версию на месте не сохранить: основное действие и есть форк, а меню с тем же
            // окном было бы вторым входом в него — поэтому одна кнопка, без стрелки ([UI-83]).
            <Button variant="filled" onClick={openNewVersion} disabled={saving}
              icon={<Icon icon={CallSplit} size={18} />} title="Историческая версия — сохранить как новую">
              Сохранить как новую
            </Button>
          )}
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
          plan={versionPlan}
          saving={saving}
          onCancel={() => setNewVersionOpen(false)}
          onConfirm={handleSaveAsNewVersion}
        />
      )}
    </div>
  );
}
