import { useEffect, useRef, useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Icon } from '@/shared/ui/Icon';
import { ArrowBack, ChevronRight, ImageIcon, NoteAdd } from '@/shared/ui/icons';
import { SegmentedButton } from '@/shared/ui/SegmentedButton';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { TextField } from '@/shared/ui/TextField';
import { blankLine, crew, needsLineFields, sites, work, type Line, type Report, type Variant, type Work } from './sample';

const scopes = [{ value: 'recent', label: 'Недавние' }, { value: 'site', label: 'На стройке' }] as const;
type WorkScope = (typeof scopes)[number]['value'];

export function WorkPicker({ siteId, onChoose }: { siteId: Report['siteId']; onChoose: (item: Work | null) => void }) {
  const [query, setQuery] = useState('');
  const [scope, setScope] = useState<WorkScope>('recent');
  const site = sites.find(item => item.id === siteId);
  const needle = query.trim().toLocaleLowerCase('ru');
  const pool = scope === 'site' && site ? work.filter(item => site.workIds.includes(item.id)) : work;
  const choices = needle ? pool.filter(item => item.name.toLocaleLowerCase('ru').includes(needle)) :
    scope === 'recent' ? pool.slice(0, 3) : pool;
  return <div className="mob-stack">
    <TextField label="Поиск работы" placeholder="Например, кабель" value={query} onChange={e => setQuery(e.target.value)} />
    {site && <SegmentedButton aria-label="Источник подсказок" items={[...scopes]} value={scope} onChange={setScope} />}
    <div className="mob-stack mob-tight">
      {(needle || !site) && <p className="mob-caption">{needle ? 'Результаты поиска' : 'Недавние работы'}</p>}
      {choices.map(item => <Button key={item.id} variant="text" multiline className="mob-choice" onClick={() => onChoose(item)}>
        <span>{item.name}</span><span className="mob-muted">{item.unit}</span><Icon icon={ChevronRight} size={18} />
      </Button>)}
      {choices.length === 0 && <p className="mob-muted" role="status">Совпадений нет. Измените запрос или добавьте свою работу.</p>}
    </div>
    <Button variant="outlined" icon={<Icon icon={NoteAdd} size={18} />} onClick={() => onChoose(null)}>Своя работа</Button>
  </div>;
}

export type EditorStart = { line?: Line; choice?: Work | null };
type Phase = 'pick' | 'details' | 'extras';
const stepNames: Record<Phase, string> = { pick: 'Работа', details: 'Параметры', extras: 'Бригада и заметка' };

export function LineFlow({ variant, report, start, onCancel, onSave }: {
  variant: Variant;
  report: Report;
  start: EditorStart;
  onCancel: () => void;
  onSave: (line: Line) => void;
}) {
  const [phase, setPhase] = useState<Phase>(start.line || start.choice !== undefined ? 'details' : 'pick');
  const [line, setLine] = useState(() => start.line ?? blankLine(start.choice ?? undefined));
  const [validation, setValidation] = useState(false);
  const title = useRef<HTMLHeadingElement>(null);
  const selectedWork = work.find(item => item.id === line.workId);
  const sectionOptions = sites.find(site => site.id === report.siteId)?.sections ?? [];
  const errors = validation ? needsLineFields(line, sectionOptions.length > 0) : [];
  const stepped = variant === 'c';
  useEffect(() => { title.current?.focus({ preventScroll: true }); }, [phase]);

  function patch<K extends keyof Line>(key: K, value: Line[K]) {
    setLine(previous => ({ ...previous, [key]: value }));
  }
  function confirm() {
    if (needsLineFields(line, sectionOptions.length > 0).length) {
      setValidation(true);
      return;
    }
    if (stepped && phase === 'details') {
      setPhase('extras');
      return;
    }
    onSave({ ...line, name: line.name.trim() });
  }
  function back() {
    if (phase === 'extras') setPhase('details');
    else if (phase === 'details' && !start.line && (stepped || start.choice === undefined)) setPhase('pick');
    else onCancel();
  }

  return <section className="mob-line-flow" aria-label="Форма строки работы">
    <div className="mob-flow-heading">
      <Button className="mob-flow-back" variant="text" icon={<Icon icon={ArrowBack} size={18} />} onClick={back}>
        {phase === 'pick' || start.line && phase === 'details' || variant === 'b' ? 'К отчёту' : 'Назад'}
      </Button>
      <h3 ref={title} tabIndex={-1}>{phase === 'pick' ? 'Выберите работу' : start.line ? 'Правка работы' : line.name || 'Своя работа'}</h3>
    </div>
    {stepped && <ol className="mob-steps" aria-label="Шаги оформления работы">
      {(Object.keys(stepNames) as Phase[]).map(step => <li key={step} aria-current={step === phase ? 'step' : undefined}>{stepNames[step]}</li>)}
    </ol>}
    {phase === 'pick' ? <WorkPicker siteId={report.siteId} onChoose={item => {
      setLine(blankLine(item ?? undefined));
      setValidation(false);
      setPhase('details');
    }} /> : <>
      {!!errors.length && <Banner tone="error">Заполните поля: {errors.join(', ')}.</Banner>}
      {phase === 'details' && <div className="mob-stack">
        {!selectedWork && <TextField label="Название работы" value={line.name} onChange={e => patch('name', e.target.value)} />}
        {selectedWork?.variants && <Select label="Вариант вида работы" value={line.variant} onValueChange={value => patch('variant', value)} emptyItem="Выберите вариант">
          {selectedWork.variants.map(value => <SelectItem key={value} value={value}>{value}</SelectItem>)}
        </Select>}
        {!!sectionOptions.length && <Select label="Раздел стройки" value={line.section} onValueChange={value => patch('section', value)} emptyItem="Выберите раздел">
          {sectionOptions.map(value => <SelectItem key={value} value={value}>{value}</SelectItem>)}
        </Select>}
        <div className="mob-field-pair">
          <TextField label="Объём" inputMode="decimal" value={line.quantity} onChange={e => patch('quantity', e.target.value)} />
          {selectedWork ? <TextField label="Единица" readOnly value={line.unit} /> :
            <Select label="Единица" value={line.unit} onValueChange={value => patch('unit', value)} emptyItem="Выберите">
              {['м', 'шт.', 'компл.', 'линия'].map(value => <SelectItem key={value} value={value}>{value}</SelectItem>)}
            </Select>}
        </div>
        {selectedWork?.place && <TextField label="Место" placeholder="Этаж, помещение или оси" value={line.place} onChange={e => patch('place', e.target.value)} />}
        {selectedWork?.material && <TextField label="Материалы" placeholder="Марка или наименование" value={line.material} onChange={e => patch('material', e.target.value)} />}
        {selectedWork?.web && <StatusText>Поля заказчика заполняются в вебе</StatusText>}
      </div>}
      {(!stepped || phase === 'extras') && <Extras line={line} onCrew={value => patch('crew', value)} onNote={value => patch('note', value)} onPhoto={value => patch('photo', value)} />}
      <div className="mob-actions"><Button variant="filled" onClick={confirm}>
        {stepped && phase === 'details' ? 'Далее' : 'Сохранить работу'}
      </Button></div>
    </>}
  </section>;
}

function Extras({ line, onCrew, onNote, onPhoto }: {
  line: Line; onCrew: (names: string[]) => void; onNote: (note: string) => void; onPhoto: (attached: boolean) => void;
}) {
  const [crewOpen, setCrewOpen] = useState(line.crew.length > 0);
  return <div className="mob-stack">
    <Button variant="outlined" aria-expanded={crewOpen} onClick={() => setCrewOpen(!crewOpen)}>
      {line.crew.length ? 'Бригада · ' + line.crew.length : 'Указать бригаду'}
    </Button>
    {crewOpen && <fieldset className="mob-checks">
      <legend>Участники смены</legend>
      {crew.map(name => <Checkbox key={name} label={name} checked={line.crew.includes(name)} onChange={on => {
        onCrew(on ? [...line.crew, name] : line.crew.filter(member => member !== name));
      }} />)}
    </fieldset>}
    <TextAreaField label="Комментарий" value={line.note} onChange={e => onNote(e.target.value)} />
    <Button variant="text" icon={<Icon icon={ImageIcon} size={18} />} onClick={() => onPhoto(!line.photo)}>
      {line.photo ? 'Убрать снимок из примера' : 'Добавить снимок в примере'}
    </Button>
    {line.photo && <StatusText tone="primary">Демонстрационный снимок прикреплён</StatusText>}
  </div>;
}
