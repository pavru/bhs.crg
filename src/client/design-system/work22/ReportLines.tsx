import { useEffect, useState } from 'react';
import { Button, IconButton } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { Checkbox } from '@/shared/ui/Checkbox';
import { EditGrid, type EditGridColumn } from '@/shared/ui/EditGrid';
import { Icon } from '@/shared/ui/Icon';
import { Add, Delete, ErrorIcon } from '@/shared/ui/icons';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { TextField } from '@/shared/ui/TextField';
import { LinePhotos } from './LinePhotos';
import { blankLine, crew, materials, workTypes, type Variant, type WorkLine } from './model';

type Update = (patch: Partial<WorkLine>) => void;

function WorkType({ line, label, disabled, update }: { line: WorkLine; label: string; disabled: boolean; update: Update }) {
  return <Select label={label} value={line.type} disabled={disabled} onValueChange={type =>
    update({ type, option: '', unit: workTypes.find(item => item.id === type)?.unit ?? '' })}>
    {workTypes.map(type => <SelectItem key={type.id} value={type.id}>{type.name}</SelectItem>)}
  </Select>;
}

function LineBasics({ line, disabled, update }: { line: WorkLine; disabled: boolean; update: Update }) {
  return <div className="work22-line-basics">
    <WorkType line={line} label="Вид работы" disabled={disabled} update={update} />
    <TextField label="Объём" inputMode="decimal" value={line.volume} readOnly={disabled}
      onChange={event => update({ volume: event.target.value })} />
    <Select label="Единица" value={line.unit} disabled={disabled} onValueChange={unit => update({ unit })}>
      {['м', 'шт.', 'компл.'].map(unit => <SelectItem key={unit} value={unit}>{unit}</SelectItem>)}
    </Select>
  </div>;
}

function LineDetails({ line, index, locked, closed, error, update }: {
  line: WorkLine; index: number; locked: boolean; closed: boolean; error?: string; update: Update;
}) {
  const type = workTypes.find(item => item.id === line.type);
  const missing = [
    type?.requiresLocation && !line.place ? 'место' : '',
    type?.requiresMaterials && !line.material ? 'материалы' : '',
  ].filter(Boolean);
  return <div className="space-y-4">
    {missing.length > 0 && <StatusText icon={ErrorIcon} tone="warning">Нужно для ИД · {missing.join(', ')}</StatusText>}
    <div className="work22-detail-fields">
      {type && type.variants.length > 0 && <Select label="Вариант" disabled={locked} value={line.option}
        onValueChange={option => update({ option })}>
        {type.variants.map(option => <SelectItem key={option} value={option}>{option}</SelectItem>)}
      </Select>}
      <Select label="Раздел стройки" disabled={locked} value={line.section} onValueChange={section => update({ section })}>
        {['Корпус 1', 'Корпус 2', 'Наружные сети'].map(section => <SelectItem key={section} value={section}>{section}</SelectItem>)}
      </Select>
      <TextField label="Место" value={line.place} readOnly={locked} onChange={event => update({ place: event.target.value })} />
      <div id={`work22-materials-${index + 1}`} tabIndex={-1}>
        <Select label="Материалы · Справочник" disabled={locked} value={line.material}
          onValueChange={material => update({ material })}>
          {materials.map(material => <SelectItem key={material} value={material}>{material}</SelectItem>)}
        </Select>
        {error && <p className="mt-1 text-body-small text-error">{error}</p>}
      </div>
      <TextField label="Комментарий" value={line.note} readOnly={locked} onChange={event => update({ note: event.target.value })} />
    </div>
    <div className="space-y-2">
      <Checkbox label="Бригадная работа" checked={line.isCrew} disabled={locked}
        onChange={isCrew => update({ isCrew, participants: isCrew ? line.participants : [] })} />
      {line.isCrew && <fieldset className="flex flex-wrap gap-x-4 gap-y-1">
        <legend className="mb-1 text-label-large">Участники бригады</legend>
        {crew.map(name => <Checkbox key={name} label={name} disabled={locked} checked={line.participants.includes(name)}
          onChange={checked => update({
            participants: checked ? [...line.participants, name] : line.participants.filter(item => item !== name),
          })} />)}
      </fieldset>}
    </div>
    <LinePhotos photos={line.photos} locked={locked} closed={closed} onChange={photos => update({ photos })} />
  </div>;
}

export function ReportLines({ variant, lines, locked, closed, missing, onChange }: {
  variant: Variant; lines: WorkLine[]; locked: boolean; closed: boolean;
  missing?: { line: number; message: string }; onChange: (lines: WorkLine[]) => void;
}) {
  const [expanded, setExpanded] = useState<string | null>(lines[0]?.id ?? null);
  const errorLine = missing ? lines[missing.line - 1]?.id : undefined;
  useEffect(() => {
    if (!missing || !errorLine) return;
    const field = document.getElementById(`work22-materials-${missing.line}`);
    field?.scrollIntoView({ block: 'center' });
    (field?.querySelector<HTMLElement>('[role="combobox"]') ?? field)?.focus();
  }, [missing, errorLine]);
  const selected = errorLine ?? expanded;

  const update = (line: WorkLine): Update => patch =>
    onChange(lines.map(item => item.id === line.id ? { ...item, ...patch } : item));
  const add = () => {
    const line = blankLine();
    onChange([...lines, line]);
    setExpanded(line.id);
  };
  const remove = (line: WorkLine) => onChange(lines.filter(item => item.id !== line.id));
  const details = (line: WorkLine, index: number) => <LineDetails key={line.id} line={line} index={index}
    locked={locked} closed={closed} error={missing?.line === index + 1 ? missing.message : undefined} update={update(line)} />;

  if (variant === 'a') {
    const columns: EditGridColumn<WorkLine>[] = [
      { key: 'type', label: 'Вид работы', cell: (line, index) =>
        <WorkType label={`Вид работы, строка ${index + 1}`} line={line} disabled={locked} update={update(line)} /> },
      { key: 'volume', label: 'Объём', width: 'w-28', cell: (line, index) => <TextField
        label={`Объём, строка ${index + 1}`} inputMode="decimal" readOnly={locked} value={line.volume}
        onChange={event => update(line)({ volume: event.target.value })} /> },
      { key: 'unit', label: 'Ед.', width: 'w-24', cell: (line, index) => <Select
        label={`Единица, строка ${index + 1}`} value={line.unit} disabled={locked} onValueChange={unit => update(line)({ unit })}>
        {['м', 'шт.', 'компл.'].map(unit => <SelectItem key={unit} value={unit}>{unit}</SelectItem>)}
      </Select> },
      { key: 'details', label: 'Подробности', width: 'w-44', cell: (line, index) =>
        <Button variant={selected === line.id ? 'tonal' : 'text'} aria-expanded={selected === line.id}
          aria-label={`Подробности строки ${index + 1}`} onClick={() => setExpanded(selected === line.id ? null : line.id)}>
          {selected === line.id ? 'Свернуть' : 'Открыть'}
        </Button> },
    ];
    const index = lines.findIndex(line => line.id === selected);
    return <div className="space-y-3">
      <EditGrid label="Строки отчёта" columns={columns} rows={lines} rowKey={line => line.id} numbered fill
        selected={line => line.id === selected} add={locked ? undefined : { label: 'Добавить строку', onAdd: add }}
        onRemove={locked ? undefined : remove} />
      {index >= 0 && <Card title={`Подробности строки ${index + 1}`}>{details(lines[index], index)}</Card>}
    </div>;
  }

  return <div className="space-y-3">
    {lines.map((line, index) => {
      const title = workTypes.find(type => type.id === line.type)?.name || `Строка ${index + 1}`;
      const open = variant === 'b' || line.id === selected;
      return <Card key={line.id} title={`${index + 1} · ${title}`} invalid={missing?.line === index + 1}
        menu={!locked && <IconButton icon={Delete} label={`Удалить строку ${index + 1}`} onClick={() => remove(line)} />}>
        {open ? <>
          <LineBasics line={line} disabled={locked} update={update(line)} />
          {details(line, index)}
        </> : <div className="flex flex-wrap items-center justify-between gap-2">
          <p className="text-body-medium text-on-surface-variant">{line.volume} {line.unit} · {line.place || 'Место не указано'}</p>
          <Button variant="tonal" onClick={() => setExpanded(line.id)}>Раскрыть строку {index + 1}</Button>
        </div>}
      </Card>;
    })}
    {!locked && <Button variant="tonal" icon={<Icon icon={Add} size={18} />} onClick={add}>Добавить строку</Button>}
  </div>;
}
