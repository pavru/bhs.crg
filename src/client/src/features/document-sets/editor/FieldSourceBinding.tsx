import { useMemo, useState } from 'react';
import { Database, Link2Off } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import {
  useAvailableDataSetFiles, useCreateDataSetBinding, useUpdateDataSetBinding,
  useDeleteDataSetBinding, useAutoMapDataSetSource,
} from '@/shared/api/datasets';
import { parseSourceColumnNames } from '@/shared/api/datasetHelpers';
import type { DataSetBinding, DataSetSource } from '@/shared/api/types';
import type { SchemaField } from '@/shared/api/schema';

type FlatSource = DataSetSource & { fileName: string };

/**
 * Per-field привязка скалярного поля к источнику данных (issue #296, фаза 1 — «линза»). Поле — точка
 * ВХОДА; под капотом хранение остаётся источнико-центричным (#55): find-or-create единственного
 * (owner, source)-скалярного binding, правится его срез `{ключ поля: колонка}`, prune-on-empty. При
 * выборе источника — авто-предложение покрыть остальные поля, которые он заполняет.
 *
 * Модалка (а не Popover): редактор документа рендерится в Radix Dialog (modal), который через
 * react-remove-scroll делает `pointer-events: none` для всего вне контента диалога — портал Popover
 * в body оказывался инертным (клики не ловились). Вложенный Dialog (Modal) координируется корректно.
 */
export function FieldSourceBinding({ instanceId, setId, field, scalarFields, bindings }: {
  instanceId: string;
  setId: string;
  field: SchemaField;
  scalarFields: SchemaField[];
  bindings: DataSetBinding[];
}) {
  const [open, setOpen] = useState(false);

  const currentBinding = bindings.find(b => !b.targetFieldKey && b.mapping?.[field.key]);
  const isBound = !!currentBinding;

  const { data: files = [] } = useAvailableDataSetFiles(setId);
  const create = useCreateDataSetBinding();
  const update = useUpdateDataSetBinding();
  const del = useDeleteDataSetBinding();
  const autoMap = useAutoMapDataSetSource();

  const sources: FlatSource[] = useMemo(
    () => files.flatMap(f => f.sources.filter(s => !s.materializeTypeId).map(s => ({ ...s, fileName: f.name }))),
    [files]);

  const [sourceId, setSourceId] = useState('');
  const [column, setColumn] = useState('');
  const [cover, setCover] = useState<Record<string, string>>({});
  const [coverOn, setCoverOn] = useState(true);
  const [busy, setBusy] = useState(false);

  const selectedSource = sources.find(s => s.id === sourceId);
  const columns = useMemo(() => {
    if (!selectedSource) return [];
    const computed = (selectedSource.computedColumns ?? []).map(c => c.alias).filter(Boolean);
    return [...new Set([...parseSourceColumnNames(selectedSource.cachedSchema), ...computed])];
  }, [selectedSource]);

  const boundKeys = useMemo(() => {
    const s = new Set<string>();
    for (const b of bindings) if (!b.targetFieldKey) for (const k of Object.keys(b.mapping ?? {})) s.add(k);
    return s;
  }, [bindings]);

  function onOpenChange(o: boolean) {
    setOpen(o);
    if (o) {
      setSourceId(currentBinding?.sourceId ?? '');
      setColumn(currentBinding?.mapping?.[field.key] ?? '');
      setCover({}); setCoverOn(true);
    }
  }

  async function pickSource(id: string) {
    setSourceId(id); setColumn(''); setCover({});
    const src = sources.find(s => s.id === id);
    if (!src) return;
    const siblings = scalarFields.filter(f => f.key !== field.key && !boundKeys.has(f.key));
    if (siblings.length === 0) return;
    try {
      const { mapping } = await autoMap.mutateAsync({ sourceId: id, fields: siblings.map(f => ({ key: f.key, title: f.title })) });
      if (mapping[field.key] && !column) setColumn(mapping[field.key]);
      const others: Record<string, string> = {};
      for (const [k, c] of Object.entries(mapping)) if (k !== field.key && c) others[k] = c;
      setCover(others);
    } catch { /* авто-маппинг необязателен */ }
  }

  const coverTitles = Object.keys(cover).map(k => scalarFields.find(f => f.key === k)?.title ?? k);

  async function bind() {
    if (!selectedSource || !column) return;
    setBusy(true);
    try {
      const existing = bindings.find(b => !b.targetFieldKey && b.sourceId === selectedSource.id);
      const mapping: Record<string, string> = { ...(existing?.mapping ?? {}) };
      mapping[field.key] = column;
      if (coverOn) for (const [k, c] of Object.entries(cover)) mapping[k] = c;
      if (existing) await update.mutateAsync({ id: existing.id, ownerId: instanceId, targetFieldKey: null, mapping });
      else await create.mutateAsync({ ownerId: instanceId, sourceId: selectedSource.id, targetFieldKey: null, mapping });
      setOpen(false);
    } finally { setBusy(false); }
  }

  async function unbind() {
    if (!currentBinding) return;
    setBusy(true);
    try {
      const mapping = { ...currentBinding.mapping };
      delete mapping[field.key];
      if (Object.keys(mapping).length === 0) await del.mutateAsync({ id: currentBinding.id, ownerId: instanceId });
      else await update.mutateAsync({ id: currentBinding.id, ownerId: instanceId, targetFieldKey: null, mapping });
      setOpen(false);
    } finally { setBusy(false); }
  }

  return (
    <>
      <button type="button" onClick={() => onOpenChange(true)}
        title={isBound ? 'Заполняется из источника данных — изменить/отвязать' : 'Привязать к источнику данных'}
        aria-label={isBound ? 'Привязка к источнику' : 'Привязать к источнику'}
        className={`inline-flex items-center justify-center rounded transition-colors ${
          isBound ? 'text-brand hover:text-brand-hover' : 'text-fg4 opacity-0 group-hover:opacity-100 hover:text-fg2'}`}>
        <Database size={13} />
      </button>

      <Modal open={open} onOpenChange={onOpenChange} title={`Источник для «${field.title}»`}
        footer={
          <div className="flex items-center justify-between gap-2">
            <div>
              {isBound && (
                <Button variant="text" size="sm" danger onClick={unbind} disabled={busy} icon={<Link2Off size={14} />}>
                  Отвязать
                </Button>
              )}
            </div>
            <div className="flex gap-2">
              <Button variant="text" size="sm" onClick={() => setOpen(false)}>Отмена</Button>
              <Button variant="filled" size="sm" onClick={bind} disabled={!column || busy} loading={busy}>
                {isBound ? 'Изменить' : 'Привязать'}
              </Button>
            </div>
          </div>
        }>
        {sources.length === 0 ? (
          <p className="text-xs text-fg4 py-2">
            Нет подходящих источников. Загрузите набор данных на странице «Наборы данных» или в панели уровня.
          </p>
        ) : (
          <div className="space-y-3">
            <div>
              <label className="block text-[11px] text-fg4 mb-1">Источник</label>
              <select value={sourceId} onChange={e => pickSource(e.target.value)}
                className="w-full border border-stroke rounded-md px-2 py-1.5 text-sm bg-surface text-fg1">
                <option value="">— выберите —</option>
                {sources.map(s => <option key={s.id} value={s.id}>{s.fileName} · {s.name}</option>)}
              </select>
            </div>

            {selectedSource && (
              <div>
                <label className="block text-[11px] text-fg4 mb-1">Колонка</label>
                <select value={column} onChange={e => setColumn(e.target.value)}
                  className="w-full border border-stroke rounded-md px-2 py-1.5 text-sm bg-surface text-fg1">
                  <option value="">— не привязано —</option>
                  {columns.map(c => <option key={c} value={c}>{c}</option>)}
                </select>
              </div>
            )}

            {selectedSource && coverTitles.length > 0 && (
              <label className="flex items-start gap-2 text-xs text-fg2 cursor-pointer select-none">
                <input type="checkbox" checked={coverOn} onChange={e => setCoverOn(e.target.checked)} className="mt-0.5" />
                <span>Этот источник заполнит также: <span className="text-fg3">{coverTitles.join(', ')}</span></span>
              </label>
            )}
          </div>
        )}
      </Modal>
    </>
  );
}
