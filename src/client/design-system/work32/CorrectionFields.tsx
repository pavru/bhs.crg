import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { TextField } from '@/shared/ui/TextField';

export type Line = {
  id: string;
  work: string;
  variant: string;
  volume: string;
  unit: string;
  section: string;
  location: string;
  comment: string;
  act: string;
  materials: { kind: string; note: string }[];
  isCrew: boolean;
  crewRole: string;
  participants: string[];
  photo?: 'stored' | 'pending';
};

export type Contents = { hours: string; note: string; lines: Line[] };

export const freshLine = (id: string): Line => ({
  id, work: '', variant: '', volume: '', unit: '', section: '', location: '', comment: '', act: '',
  materials: [], isCrew: false, crewRole: '', participants: [],
});

type LineFieldsProps = {
  line: Line; number: number; last: boolean; onChange: (next: Line) => void;
  onRemove: () => void; onMove: (direction: -1 | 1) => void;
};

const workTypes = ['Прокладка кабеля', 'Монтаж лотка', 'Разметка трассы'];

function Choice({ label, value, items, onChange }: {
  label: string; value: string; items: string[]; onChange: (value: string) => void;
}) {
  return <Select label={label} value={value || undefined} emptyItem="Не указано" onValueChange={onChange}>
    {items.map(item => <SelectItem key={item} value={item}>{item}</SelectItem>)}
  </Select>;
}

export function LineFields({ line, number, last, onChange, onRemove, onMove }: LineFieldsProps) {
  function field<K extends keyof Line>(key: K, value: Line[K]) { onChange({ ...line, [key]: value }); }
  return <Card title={`${number}. ${line.work || 'Новая строка'}`}>
    <div className="grid items-start gap-3 @md:grid-cols-2">
      <Choice label="Вид работы" value={line.work} items={workTypes} onChange={value => field('work', value)} />
      <Choice label="Вариант" value={line.variant} items={['В лотке', 'В трубе']} onChange={value => field('variant', value)} />
      <TextField label="Объём" value={line.volume} inputMode="decimal" onChange={e => field('volume', e.target.value)} />
      <Choice label="Единица" value={line.unit} items={['м', 'шт.']} onChange={value => field('unit', value)} />
      <Choice label="Раздел" value={line.section} items={['Электроснабжение', 'Освещение']} onChange={value => field('section', value)} />
      <TextField label="Место работ" value={line.location} onChange={e => field('location', e.target.value)} />
      <TextField label="Акт" value={line.act} hint={line.act ? undefined : 'Нужно к приёмке'} onChange={e => field('act', e.target.value)} />
      <TextField label="Комментарий к строке" value={line.comment} onChange={e => field('comment', e.target.value)} />
      <label className="flex min-h-10 items-center gap-3 text-body-medium"><Checkbox checked={line.isCrew} onChange={value => field('isCrew', value)} />Бригадная работа</label>
      {line.isCrew && <Choice label="Роль в бригаде" value={line.crewRole} items={['Бригадир', 'Монтажник']} onChange={value => field('crewRole', value)} />}
    </div>
    {line.isCrew && <fieldset className="mt-3 flex flex-wrap gap-3">
      <legend className="text-label-large">Участники</legend>
      {['Ирина Белова', 'Михаил Орлов'].map(person => <label key={person} className="flex min-h-10 items-center gap-3 text-body-medium">
        <Checkbox checked={line.participants.includes(person)} onChange={checked => field('participants', checked
          ? [...line.participants, person] : line.participants.filter(item => item !== person))} />{person}
      </label>)}
    </fieldset>}
    <fieldset className="mt-3 space-y-2">
      <legend className="text-label-large">Материалы</legend>
      {line.materials.map((material, index) => <div key={index} className="grid items-start gap-2 @lg:grid-cols-[1fr_1fr_auto]">
        <Choice label={`Материал ${index + 1}`} value={material.kind} items={['ВВГнг 3×2,5', 'Лоток 100×50']}
          onChange={kind => field('materials', line.materials.map((item, i) => i === index ? { ...item, kind } : item))} />
        <TextField label={`Примечание к материалу ${index + 1}`} value={material.note} onChange={event =>
          field('materials', line.materials.map((item, i) => i === index ? { ...item, note: event.target.value } : item))} />
        <Button variant="text" onClick={() => field('materials', line.materials.filter((_, i) => i !== index))}>Убрать материал</Button>
      </div>)}
      <Button variant="text" onClick={() => field('materials', [...line.materials, { kind: '', note: '' }])}>Добавить материал</Button>
    </fieldset>
    {line.photo && <StatusText>{line.photo === 'stored' ? 'Фото сохранено' : 'Ожидается файл фото'}</StatusText>}
    <div className="mt-3 flex flex-wrap gap-1">
      {number > 1 && <Button variant="text" onClick={() => onMove(-1)}>Выше</Button>}
      {!last && <Button variant="text" onClick={() => onMove(1)}>Ниже</Button>}
      <Button variant="text" danger onClick={onRemove}>Убрать строку</Button>
    </div>
  </Card>;
}

export function HeaderFields({ draft, onChange }: { draft: Contents; onChange: (draft: Contents) => void }) {
  return <div className="grid items-start gap-3 @md:grid-cols-[1fr_2fr]">
    <TextField label="Часы за смену" value={draft.hours} inputMode="decimal" onChange={e => onChange({ ...draft, hours: e.target.value })} />
    <TextAreaField label="Примечание к отчёту" rows={2} value={draft.note} onChange={e => onChange({ ...draft, note: e.target.value })} />
  </div>;
}
