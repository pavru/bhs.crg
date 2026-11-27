import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import {
  catalogLabels, useEntryCatalog, type EntryCatalogScope, type MaterialOption, type UnitOption, type WorkTypeOption,
} from '@/shared/api/workEntryCatalog';
import type { RequiredReportField, WorkReportLine } from '@/shared/api/workReports';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { EditGrid, type EditGridColumn } from '@/shared/ui/EditGrid';
import { NumberInput } from '@/shared/ui/NumberInput';
import { SNACKBAR_UNDO_MS, useSnackbar } from '@/shared/ui/Snackbar';
import { StatusText } from '@/shared/ui/StatusText';
import { WorkCatalogPicker } from '@/shared/ui/WorkCatalogPicker';
import { WorkTypePicker } from '@/shared/ui/WorkTypePicker';
import { LinePhotos, type ReportPhotos } from './LinePhotos';
import { ReportLineDetails } from './ReportLineDetails';
import { withDetailValue } from '../reportDetails';
import { emptyLine, type DraftLine } from './reportContents';
import { missingForId } from './reportPresentation';

export function ReportLines({ scope, lines, savedLines, photos, disabled, siteReady, errors, focus, onChange, correction, onPickType }: {
  scope: EntryCatalogScope; lines: DraftLine[]; disabled: boolean; siteReady: boolean;
  savedLines?: WorkReportLine[]; errors?: RequiredReportField[]; focus?: RequiredReportField;
  photos?: ReportPhotos;
  correction?: boolean;
  onPickType?: (type: WorkTypeOption) => void;
  onChange: (update: (lines: DraftLine[]) => DraftLine[]) => void;
}) {
  const toast = useSnackbar();
  const [expanded, setExpanded] = useState<string | null>(lines[0]?.id ?? null);
  const [seenFocus, setSeenFocus] = useState(focus);
  const [removal, setRemoval] = useState<{ id: string; answer: (removed: boolean) => void }>();
  const [pickedTypes, setPickedTypes] = useState<Record<string, WorkTypeOption>>({});
  const [pickedUnits, setPickedUnits] = useState<Record<string, UnitOption>>({});
  const [pickedMaterials, setPickedMaterials] = useState<Record<string, MaterialOption>>({});
  const details = useRef<HTMLDivElement>(null);
  const canRestore = useRef(false);
  useLayoutEffect(() => {
    canRestore.current = !disabled;
    return () => { canRestore.current = false; };
  }, [disabled]);
  const types = catalogLabels(useEntryCatalog(scope, 'work-types', '', siteReady && lines.length > 0).data);
  const units = catalogLabels(useEntryCatalog(scope, 'units', '', siteReady && lines.length > 0).data);
  const typeOf = (id: string) => types.get(id) ?? pickedTypes[id];

  if (seenFocus !== focus) {
    setSeenFocus(focus);
    if (focus) setExpanded(focus.lineId);
  }

  useEffect(() => {
    if (!focus || expanded !== focus.lineId) return;
    const field = details.current?.querySelector<HTMLElement>(`[data-report-field="${focus.field}"]`);
    field?.scrollIntoView?.({ block: 'center' });
    (field?.querySelector<HTMLElement>('input:not([disabled]), button:not([disabled])') ?? field)?.focus();
  }, [focus, expanded]);

  const update = (line: DraftLine, changes: Partial<DraftLine>) =>
    onChange(current => current.map(item => item.id === line.id ? { ...item, ...changes } : item));
  const expandedIndex = lines.findIndex(line => line.id === expanded);
  const expandedLine = lines[expandedIndex];
  const removingIndex = removal ? lines.findIndex(line => line.id === removal.id) : -1;
  const removing = removal && removingIndex >= 0 && !disabled ? removal : undefined;
  const dependents = removing ? dependentNames(savedLines?.find(line => line.id === removing.id)) : [];
  if (removal && (!removing || !dependents.length)) setRemoval(undefined);

  function drop(id: string) {
    onChange(current => current.filter(item => item.id !== id));
    if (id === expanded) setExpanded(null);
  }

  function remove(line: DraftLine, index: number): void | Promise<boolean> {
    if (dependentNames(savedLines?.find(item => item.id === line.id)).length) {
      return new Promise(answer => setRemoval({ id: line.id, answer }));
    }
    drop(line.id);
    toast.success(`Строка ${index + 1} удалена.`, { duration: SNACKBAR_UNDO_MS, action: {
      label: 'Вернуть', onClick: () => {
        if (!canRestore.current) { toast.info('Отчёт сейчас недоступен для правки.'); return; }
        onChange(current => {
          if (current.some(other => other.id === line.id)) return current;
          const restored = [...current];
          restored.splice(index, 0, line);
          return restored;
        });
      },
    } });
  }
  const columns: EditGridColumn<DraftLine>[] = [
    { key: 'type', label: 'Вид работы', cell: (line, index) => <WorkTypePicker scope={scope}
      label={`Вид работы, строка ${index + 1}`} value={line.workTypeId} type={typeOf(line.workTypeId)}
      disabled={disabled || !siteReady} onPick={type => {
        setPickedTypes(old => ({ ...old, [type.id]: type }));
        onPickType?.(type);
        if (type.id !== line.workTypeId) update(line, { workTypeId: type.id, variantId: null, unitId: type.unitId ?? '' });
      }} /> },
    { key: 'volume', label: 'Объём', width: 'w-28', cell: (line, index) => <NumberInput
      label={`Объём, строка ${index + 1}`} disabled={disabled} value={line.volume}
      onChange={volume => update(line, { volume })} /> },
    { key: 'unit', label: 'Ед.', width: 'w-28', cell: (line, index) => {
      const unit = units.get(line.unitId) ?? pickedUnits[line.unitId];
      const type = typeOf(line.workTypeId);
      const unitName = unit ? `${unit.name}${unit.archived ? ' · в архиве' : ''}` : type?.unitId === line.unitId ? type.unitName : null;
      return <WorkCatalogPicker scope={scope} kind="units" label={`Единица, строка ${index + 1}`} title="Выберите единицу"
        display={unitName || (line.unitId ? 'Недоступна' : 'Выбрать')} name={item => item.name} currentId={line.unitId} disabled={disabled || !siteReady} field
        onPick={item => { setPickedUnits(old => ({ ...old, [item.id]: item })); update(line, { unitId: item.id }); }} />;
    } },
    { key: 'details', label: 'Подробности', width: 'w-48', cell: (line, index) => {
      const missing = missingForId(line, typeOf(line.workTypeId));
      return <div className="flex flex-col items-start gap-1">
        <Button variant={line.id === expanded ? 'tonal' : 'text'} aria-expanded={line.id === expanded}
          aria-label={`Подробности строки ${index + 1}`} onClick={() => setExpanded(line.id === expanded ? null : line.id)}>
          {line.id === expanded ? 'Свернуть' : 'Открыть'}
        </Button>
        {missing.length > 0 && <StatusText tone="warning">Нужно для ИД</StatusText>}
      </div>;
    } },
  ];

  function detailsOf(line: DraftLine) {
    const savedLine = savedLines?.find(item => item.id === line.id);
    return <ReportLineDetails scope={scope} siteReady={siteReady} line={line} type={typeOf(line.workTypeId)}
      disabled={disabled} errors={errors?.filter(error => error.lineId === line.id)} savedLine={savedLine}
      pickedMaterials={pickedMaterials} onPickMaterial={material => setPickedMaterials(old => ({ ...old, [material.id]: material }))}
      onDetailChange={(key, value) => onChange(current => current.map(item => item.id === line.id
        ? { ...item, details: withDetailValue(item.details, key, value) } : item))}
      onChange={changes => update(line, changes)} />;
  }

  function move(index: number, direction: -1 | 1) {
    onChange(current => {
      const next = [...current];
      [next[index], next[index + direction]] = [next[index + direction], next[index]];
      return next;
    });
  }

  if (correction) return <div className="space-y-3">
    <div className="flex flex-wrap items-center justify-between gap-2">
      <h3 className="text-title-medium">Строки работы</h3>
      <Button variant="tonal" disabled={disabled || !siteReady}
        onClick={() => { const line = emptyLine(); onChange(current => [...current, line]); }}>Добавить строку</Button>
    </div>
    {lines.map((line, index) => <Card key={line.id} title={`Строка ${index + 1} · ${typeOf(line.workTypeId)?.displayName ?? 'Работа'}`}>
      <div className="space-y-4">
        <div className="grid items-start gap-3 md:grid-cols-[minmax(0,2fr)_minmax(0,1fr)_minmax(0,1fr)]">
          {columns.slice(0, 3).map(column => <div className="min-w-0" key={column.key}>
            {column.cell(line, index)}
          </div>)}
        </div>
        {detailsOf(line)}
        {line.photos.length > 0 && <StatusText>Фото строки · {line.photos.length}</StatusText>}
        <div className="flex flex-wrap gap-2">
          {index > 0 && <Button variant="text" disabled={disabled} aria-label={`Поднять строку ${index + 1}`}
            onClick={() => move(index, -1)}>Выше</Button>}
          {index < lines.length - 1 && <Button variant="text" disabled={disabled} aria-label={`Опустить строку ${index + 1}`}
            onClick={() => move(index, 1)}>Ниже</Button>}
          <Button variant="text" danger disabled={disabled} aria-label={`Убрать строку ${index + 1}`}
            onClick={() => drop(line.id)}>Убрать строку</Button>
        </div>
      </div>
    </Card>)}
  </div>;

  return <div className="space-y-3">
    <EditGrid label="Строки отчёта" columns={columns} rows={lines} rowKey={line => line.id} numbered fill minWidth="min-w-160"
      selected={line => line.id === expanded} add={disabled || !siteReady ? undefined : { label: 'Добавить строку', onAdd: () => {
        const line = emptyLine();
        onChange(current => [...current, line]);
        setExpanded(line.id);
      } }} onRemove={disabled ? undefined : remove} />
    {expandedLine && <Card title={`Подробности строки ${expandedIndex + 1}`}>
      <div ref={details} key={expandedLine.id}>
        {detailsOf(expandedLine)}
        {photos && <LinePhotos lineId={expandedLine.id} photos={expandedLine.photos}
          declared={savedLines?.find(line => line.id === expandedLine.id)?.photos ?? []} controls={photos}
          add={newPhotos => update(expandedLine, { photos: [...expandedLine.photos, ...newPhotos] })} />}
      </div>
    </Card>}
    {removing && !!dependents.length && <ConfirmDialog open title={`Удалить строку ${removingIndex + 1}?`}
      description={<div className="space-y-2">
        <p>{typeOf(lines[removingIndex].workTypeId)?.displayName} · объём {lines[removingIndex].volume || '0'}</p>
        <p>При сохранении вместе со строкой удалятся {dependents.join(', ')}. После сохранения их нельзя вернуть.</p>
      </div>} confirmLabel="Удалить строку" onOpenChange={open => {
        if (!open) { setRemoval(undefined); removing.answer(false); }
      }} onConfirm={() => { drop(removing.id); setRemoval(undefined); removing.answer(true); }} />}
  </div>;
}

function dependentNames(line?: WorkReportLine) {
  const items: string[] = [];
  if (line?.materials.length) items.push(`материалы (${line.materials.length})`);
  if (line?.participants.length) items.push(`участники строки (${line.participants.length})`);
  if (line?.photos.length) items.push(`фото (${line.photos.length})`);
  return items;
}
