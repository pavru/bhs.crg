import { useMemo, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Database, LinkOff } from '@/shared/ui/icons';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import {
  useAvailableDataSetFiles, useCreateDataSetBinding, useUpdateDataSetBinding,
  useDeleteDataSetBinding, useAutoMapDataSetSource,
} from '@/shared/api/datasets';
import { parseSourceColumnNames } from '@/shared/api/datasetHelpers';
import type { DataSetBinding, DataSetSource, CatalogScope } from '@/shared/api/types';
import { SCOPE_LABELS } from '@/shared/api/types';
import type { SchemaField } from '@/shared/api/schema';
import { isLockedField, bindableFields, LOCKED_HINT } from '../fields';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Tooltip } from '@/shared/ui/Tooltip';

type FlatSource = DataSetSource & { fileName: string; fileScope: CatalogScope };

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
  /**
   * Запертому полю (ТЗ CORE-20.2) источник не назначается: значение кладёт код модуля, и привязка
   * вытеснила бы его при генерации.
   *
   * Модалка при этом остаётся доступной — ОТВЯЗАТЬ: привязка могла достаться от времён, когда поле
   * замка ещё не носило (модуль объявляет замок новой версией, поверх живых данных). Спрятав
   * «линзу» совсем, мы закрыли бы единственный выход и оставили поле привязанным навсегда.
   */
  const locked = isLockedField(field);

  const { data: files = [] } = useAvailableDataSetFiles(setId);
  const create = useCreateDataSetBinding();
  const update = useUpdateDataSetBinding();
  const del = useDeleteDataSetBinding();
  const autoMap = useAutoMapDataSetSource();

  const sources: FlatSource[] = useMemo(
    () => files.flatMap(f => f.sources.filter(s => !s.materializeTypeId).map(s => ({ ...s, fileName: f.name, fileScope: f.scope }))),
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
    // Запертые — вон из «покрыть остальные»: именно этот список, а не выбранное поле, был вторым
    // входом к запертому полю. Галка включена по умолчанию, и привязка уезжала в `mapping` молча.
    const siblings = bindableFields(scalarFields).filter(f => f.key !== field.key && !boundKeys.has(f.key));
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
    if (locked || !selectedSource || !column) return;
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
      <Tooltip content={locked ? 'Поле заперто модулем — привязку можно только снять'
          : isBound ? 'Заполняется из источника данных — изменить/отвязать' : 'Привязать к источнику данных'}>
        <button type="button" onClick={() => onOpenChange(true)}
          aria-label={locked ? 'Снять привязку к источнику'
            : isBound ? 'Привязка к источнику' : 'Привязать к источнику'}
          className={`inline-flex items-center justify-center rounded transition-colors ${
            isBound ? 'text-primary hover:text-brand-hover' : 'text-on-surface-variant opacity-0 group-hover:opacity-100 hover:text-on-surface-variant'}`}>
          <Icon icon={Database} size={18} />
        </button>
      </Tooltip>

      <Modal open={open} onOpenChange={onOpenChange} title={`Источник для «${field.title}»`}
        footer={
          <div className="flex items-center justify-between gap-2">
            <div>
              {isBound && (
                <Button variant="text" size="sm" danger onClick={unbind} disabled={busy} icon={<Icon icon={LinkOff} size={18} />}>
                  Отвязать
                </Button>
              )}
            </div>
            <div className="flex gap-2">
              <Button variant="text" size="sm" onClick={() => setOpen(false)}>Отмена</Button>
              {!locked && (
                <Button variant="filled" size="sm" onClick={bind} disabled={!column || busy} loading={busy}>
                  {isBound ? 'Изменить' : 'Привязать'}
                </Button>
              )}
            </div>
          </div>
        }>
        {locked ? (
          <p className="text-xs text-on-surface-variant py-2">
            {LOCKED_HINT}. Источник такому полю не назначается — значение вытеснило бы то, что кладёт
            модуль. Привязку, доставшуюся от прежней версии типа, можно только снять.
          </p>
        ) : sources.length === 0 ? (
          <p className="text-xs text-on-surface-variant py-2">
            Нет подходящих источников. Загрузите набор данных на странице «Наборы данных» или в панели уровня.
          </p>
        ) : (
          <div className="space-y-3">
            <div>
              <Select label="Источник" value={sourceId} onValueChange={pickSource} placeholder="— выберите —">
                {/* Происхождение названо ДО привязки: узнать, что источник распознан, уже привязав
                    его к полям документа, — узнать поздно. Формат файла («PDF») отвечает на другой
                    вопрос: чем файл был, а не как из него получены значения. */}
                {sources.map(s => (
                  <SelectItem key={s.id} value={s.id}>
                    [{SCOPE_LABELS[s.fileScope]}] {s.fileName} · {s.name}
                    {s.origin === 'Recognized' ? ' — распознано со скана' : ''}
                  </SelectItem>
                ))}
              </Select>
            </div>

            {selectedSource && (
              <div>
                <Select label="Колонка" value={column} onValueChange={setColumn} emptyItem="— не привязано —">
                  {columns.map(c => <SelectItem key={c} value={c}>{c}</SelectItem>)}
                </Select>
              </div>
            )}

            {selectedSource && coverTitles.length > 0 && (
              <label className="flex items-start gap-2 text-xs text-on-surface-variant cursor-pointer select-none">
                <input type="checkbox" checked={coverOn} onChange={e => setCoverOn(e.target.checked)} className="mt-0.5" />
                <span>Этот источник заполнит также: <span className="text-on-surface-variant">{coverTitles.join(', ')}</span></span>
              </label>
            )}
          </div>
        )}
      </Modal>
    </>
  );
}
