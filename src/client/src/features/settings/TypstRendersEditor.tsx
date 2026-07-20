import { useState, useEffect } from 'react';
import type * as Monaco from 'monaco-editor';
import Editor from '@monaco-editor/react';
import { registerTypstLanguage } from '@/shared/ui/typstLanguage';
import { useTheme } from '@/shared/ui/ThemeProvider';
import { Plus, Trash2, Maximize2, Code, AlertCircle, AlertTriangle } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import type { DocumentType } from '@/shared/api/types';
import { resolveEffectiveFields, type SchemaField, type TypstRender } from '@/shared/api/schema';
// ─── Typst it-field autocomplete ──────────────────────────────────────────────

type ItFieldTree = Map<string, { field: SchemaField; children?: ItFieldTree }>;

function buildItFieldTree(
  fields: SchemaField[],
  allDocTypes: DocumentType[],
  depth = 0,
): ItFieldTree {
  const tree: ItFieldTree = new Map();
  if (depth > 3) return tree;
  for (const f of fields) {
    if (f.type === 'complex' && f.typeId) {
      const composite = allDocTypes.find(dt => dt.id === f.typeId);
      if (composite) {
        const nested = resolveEffectiveFields(composite, allDocTypes);
        tree.set(f.key, { field: f, children: buildItFieldTree(nested, allDocTypes, depth + 1) });
        continue;
      }
    }
    tree.set(f.key, { field: f });
  }
  return tree;
}

let _itFieldTree: ItFieldTree = new Map();
let _itCompletionRegistered = false;

function registerItCompletionProvider(monaco: typeof Monaco) {
  if (_itCompletionRegistered) return;
  _itCompletionRegistered = true;
  monaco.languages.registerCompletionItemProvider('typst', {
    triggerCharacters: ['.'],
    provideCompletionItems(model, position) {
      const line = model.getValueInRange({
        startLineNumber: position.lineNumber,
        startColumn: 1,
        endLineNumber: position.lineNumber,
        endColumn: position.column,
      });
      // Match 'it' + chain of '.Word' (Cyrillic + Latin) + trailing '.' + partial word
      const match = line.match(/\bit((?:\.[\wЀ-ӿ]+)*)\.?([\wЀ-ӿ]*)$/);
      if (!match) return { suggestions: [] };

      const pathParts = match[1] ? match[1].slice(1).split('.') : [];
      const wordSoFar = match[2] ?? '';

      let currentTree = _itFieldTree;
      for (const part of pathParts) {
        const node = currentTree.get(part);
        if (!node?.children) return { suggestions: [] };
        currentTree = node.children;
      }

      if (currentTree.size === 0) return { suggestions: [] };

      const range = {
        startLineNumber: position.lineNumber,
        endLineNumber: position.lineNumber,
        startColumn: position.column - wordSoFar.length,
        endColumn: position.column,
      };

      return {
        suggestions: Array.from(currentTree.entries()).map(([key, node]) => ({
          label: { label: key, detail: `  ${node.field.title}` },
          kind: node.children
            ? monaco.languages.CompletionItemKind.Module
            : monaco.languages.CompletionItemKind.Field,
          insertText: key,
          range,
          detail: node.field.title,
          documentation: `Тип: ${node.field.type}${node.field.required ? ' · обязат.' : ''}`,
        })),
      };
    },
  });
}

function beforeMountTypstBlock(monaco: typeof Monaco) {
  registerTypstLanguage(monaco);
  registerItCompletionProvider(monaco);
}

// ─── Typst block dialog ────────────────────────────────────────────────────────

function TypstBlockDialog({ render, onSave, onClose }: {
  render: TypstRender;
  onSave: (r: TypstRender) => void;
  onClose: () => void;
}) {
  const { resolvedTheme } = useTheme();
  const [draft, setDraft] = useState<TypstRender>(render);
  const isDirty = draft.name !== render.name
    || draft.fnName !== render.fnName
    || draft.block !== render.block;

  return (
    <Modal
      open
      onOpenChange={o => { if (!o) onClose(); }}
      title={render.name || 'Typst-блок'}
      fullScreen
      flushBody
      isDirty={isDirty}
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="text" onClick={onClose}>Отмена</Button>
          <Button variant="filled" onClick={() => { onSave(draft); onClose(); }}>Применить</Button>
        </div>
      }
    >
      <div className="flex-1 min-h-0 flex flex-col gap-3 px-6 pt-2 pb-4">
        <div className="grid grid-cols-2 gap-3 shrink-0">
          <div>
            <label className="block text-xs font-medium text-fg2 mb-1">Название варианта</label>
            <input
              value={draft.name}
              onChange={e => setDraft(d => ({ ...d, name: e.target.value }))}
              placeholder="Название варианта"
              className="w-full border border-stroke-strong rounded-md px-2 py-1.5 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface"
            />
          </div>
          <div>
            <label className="block text-xs font-medium text-fg2 mb-1">Имя функции Typst</label>
            <input
              value={draft.fnName}
              onChange={e => setDraft(d => ({ ...d, fnName: e.target.value }))}
              placeholder="typst_fn_name"
              spellCheck={false}
              className="w-full border border-stroke-strong rounded-md px-2 py-1.5 text-sm font-mono focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface"
            />
          </div>
        </div>

        <label className="block text-xs font-medium text-fg2 shrink-0">
          Тело функции — выражение Typst (<code className="font-mono">it</code> — данные объекта):
        </label>
        <div className="flex-1 min-h-0 border border-stroke-strong rounded-md overflow-hidden">
          <Editor
            height="100%"
            defaultLanguage="typst"
            theme={resolvedTheme === 'dark' ? 'vs-dark' : 'vs'}
            value={draft.block}
            onChange={val => setDraft(d => ({ ...d, block: val ?? '' }))}
            beforeMount={beforeMountTypstBlock}
            options={{
              minimap: { enabled: false },
              fontSize: 13,
              fontFamily: "'Cascadia Code', 'Fira Code', Consolas, monospace",
              wordWrap: 'on',
              lineNumbers: 'on',
              folding: true,
              scrollBeyondLastLine: false,
              automaticLayout: true,
              padding: { top: 8, bottom: 8 },
              suggestOnTriggerCharacters: true,
              quickSuggestions: { other: true, comments: false, strings: true },
            }}
          />
        </div>

        {draft.fnName && (
          <p className="text-xs text-fg4 shrink-0">
            Импорт: <code className="font-mono text-purple-600">#import "typeblocks.typ": *</code>
            {' · '}
            Вызов: <code className="font-mono text-brand">#{draft.fnName}(data.КлючПоля)</code>
          </p>
        )}
      </div>
    </Modal>
  );
}

// ─── Typst renders editor ─────────────────────────────────────────────────────

export function TypstRendersEditor({ renders, onChange, fields, allDocTypes, onBlockCommitted, problemsByFn }: {
  renders: TypstRender[];
  onChange: (r: TypstRender[]) => void;
  fields: SchemaField[];
  allDocTypes: DocumentType[];
  /** Вызывается при коммите одного блока («Применить» в диалоге) — триггер проверки сборки (#309). */
  onBlockCommitted?: (renders: TypstRender[]) => void;
  /** fnName → severity: бейдж на карточке блока с проблемой сборки. */
  problemsByFn?: Record<string, 'error' | 'warning'>;
}) {
  const [editingIndex, setEditingIndex] = useState<number | null>(null);

  useEffect(() => {
    _itFieldTree = buildItFieldTree(fields, allDocTypes);
  }, [fields, allDocTypes]);

  function add() {
    onChange([...renders, { name: '', fnName: '', block: '' }]);
  }
  function remove(i: number) {
    onChange(renders.filter((_, idx) => idx !== i));
  }
  function update(i: number, patch: Partial<TypstRender>) {
    onChange(renders.map((r, idx) => idx === i ? { ...r, ...patch } : r));
  }

  return (
    <div className="space-y-3">
      {renders.length === 0 && (
        <p className="text-xs text-fg4 py-1">
          Нет вариантов отображения. Добавьте функцию для использования в Typst-шаблонах.
        </p>
      )}
      {renders.map((r, i) => {
        const lines = r.block ? r.block.split('\n').length : 0;
        return (
          <div key={i} className="border border-stroke rounded-lg p-3 space-y-2 bg-base">
            <div className="flex items-center gap-2">
              <div className="flex-1 grid grid-cols-2 gap-2">
                <input
                  value={r.name}
                  onChange={e => update(i, { name: e.target.value })}
                  placeholder="Название варианта"
                  className="border border-stroke-strong rounded-md px-2 py-1.5 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface"
                />
                <input
                  value={r.fnName}
                  onChange={e => update(i, { fnName: e.target.value })}
                  placeholder="typst_fn_name"
                  spellCheck={false}
                  className="border border-stroke-strong rounded-md px-2 py-1.5 text-sm font-mono focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface"
                />
              </div>
              <button type="button" onClick={() => remove(i)}
                className="p-1 text-fg4 hover:text-danger shrink-0">
                <Trash2 size={13} />
              </button>
            </div>
            {/* Код правится только в полноэкранном редакторе (не inline, issue #197 Фаза C) */}
            <button type="button" onClick={() => setEditingIndex(i)}
              className="w-full flex items-center gap-2 px-3 py-2 rounded-md border border-stroke bg-surface hover:bg-muted/50 transition-colors text-left">
              <Code size={14} className="text-fg4 shrink-0" />
              <span className="text-sm text-fg2 flex-1">Редактировать Typst-код</span>
              {problemsByFn?.[r.fnName.trim()] === 'error' && (
                <span className="inline-flex items-center gap-1 text-xs text-danger shrink-0" title="Ошибка сборки блока">
                  <AlertCircle size={13} /> не собирается
                </span>
              )}
              {problemsByFn?.[r.fnName.trim()] === 'warning' && (
                <AlertTriangle size={13} className="text-warning shrink-0" />
              )}
              <span className="text-xs text-fg4 shrink-0">{lines > 0 ? `${lines} стр.` : 'пусто'}</span>
              <Maximize2 size={13} className="text-fg4 shrink-0" />
            </button>
            {r.fnName && (
              <p className="text-xs text-fg4">
                Импорт: <code className="font-mono text-purple-600">#import "typeblocks.typ": *</code>
                {' · '}
                Вызов: <code className="font-mono text-brand">#{r.fnName}(data.КлючПоля)</code>
              </p>
            )}
          </div>
        );
      })}
      <button type="button" onClick={add}
        className="flex items-center gap-1.5 text-sm text-brand hover:text-brand-hover mt-1">
        <Plus size={14} /> Добавить вариант
      </button>

      {editingIndex !== null && renders[editingIndex] && (
        <TypstBlockDialog
          render={renders[editingIndex]}
          onSave={r => {
            const next = renders.map((rr, idx) => idx === editingIndex ? { ...rr, ...r } : rr);
            onChange(next);
            onBlockCommitted?.(next); // «Применить» — дискретный триггер проверки сборки (#309)
          }}
          onClose={() => setEditingIndex(null)}
        />
      )}
    </div>
  );
}

