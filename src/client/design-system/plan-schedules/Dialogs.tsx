import { useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DataGrid } from '@/shared/ui/DataGrid';
import { EmployeePicker } from '@/shared/ui/EmployeePicker';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { NumberInput } from '@/shared/ui/NumberInput';
import { Select, SelectGroup, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import { employeeNames, employees, schedules, shortDate, type Work, type WorkLink } from './sample';

export function NewSchedule({ fromEstimate, onClose, onCreate }: {
  fromEstimate: boolean; onClose: () => void; onCreate: (name: string, source: string, works: Work[]) => void;
}) {
  const [estimate, setEstimate] = useState('Электроснабжение');
  const [revision, setRevision] = useState('3');
  const [name, setName] = useState(fromEstimate ? 'Электромонтаж' : '');
  return <Modal open title={fromEstimate ? 'Построить из сметы' : 'Новый график'} onOpenChange={open => !open && onClose()}
    footer={<><ModalExit /><ModalPrimary disabled={!name.trim()} onClick={() => onCreate(name.trim(),
      fromEstimate ? `${estimate} · редакция ${revision}` : 'Ручной график', fromEstimate ? schedules[0].works : [])}>
      {fromEstimate ? 'Построить' : 'Создать'}
    </ModalPrimary></>}>
    <div className="grid gap-4">
      <TextField label="Название графика" value={name} onChange={event => setName(event.target.value)} required />
      {fromEstimate && <>
        <Select label="Смета" value={estimate} onValueChange={setEstimate}>
          <SelectItem value="Электроснабжение">Электроснабжение</SelectItem>
          <SelectItem value="Наружные сети">Наружные сети</SelectItem>
        </Select>
        <Select label="Редакция" value={revision} onValueChange={setRevision}>
          <SelectItem value="3">Редакция 3 · действующая</SelectItem>
          <SelectItem value="2">Редакция 2</SelectItem>
        </Select>
      </>}
    </div>
  </Modal>;
}

export function EmployeeDialog({ selected, onClose, onApply }: {
  selected: string[]; onClose: () => void; onApply: (ids: string[]) => void;
}) {
  const [ids, setIds] = useState(new Set(selected));
  const [search, setSearch] = useState('');
  const candidates = employees.filter(employee =>
    `${employee.name} ${employee.personnelNumber}`.toLowerCase().includes(search.toLowerCase()))
    .map(employee => ({ ...employee, assigned: null, account: { state: 'linked' as const } }));
  return <Modal open title="Исполнители" wide onOpenChange={open => !open && onClose()}
    footer={<><ModalExit /><ModalPrimary onClick={() => onApply([...ids])}>Выбрать</ModalPrimary></>}>
    <EmployeePicker candidates={candidates} selected={ids} onChange={setIds} search={search} onSearch={setSearch} />
  </Modal>;
}

export function LinkDialog({ work, local, links, readOnly, conflict, onClose, onApply }: {
  work: Work; local: Work[]; links: WorkLink[]; readOnly: boolean; conflict: boolean;
  onClose: () => void; onApply: (links: WorkLink[]) => void;
}) {
  const choices = [...local, ...schedules[1].works];
  const current = links.filter(link => link.successor === work.id);
  const [predecessor, setPredecessor] = useState('');
  const [lag, setLag] = useState('0');
  const [cycle, setCycle] = useState<string[]>([]);
  const nameOf = (id: string) => choices.find(item => item.id === id)?.name ?? id;

  function add() {
    // Сценарий отказа воспроизводит заранее подготовленный ответ сервера.
    if (conflict || (work.id === 'trays' && predecessor === 'cable')) {
      setCycle(conflict ? ['trays', 'cable', 'base'] : ['trays', 'cable']);
      return;
    }
    onApply([...links.filter(link => link.successor !== work.id || link.predecessor !== predecessor),
      { predecessor, successor: work.id, lag }]);
    setPredecessor('');
    setLag('0');
    setCycle([]);
  }

  return <Modal open title={`Предшественники · ${work.name}`} wide onOpenChange={open => !open && onClose()}>
    <div className="grid gap-4">
      <DataGrid rows={current} rowKey={link => link.predecessor} density="compact" columns={[
        { key: 'name', label: 'Работа', cell: link => nameOf(link.predecessor) },
        { key: 'source', label: 'График', cell: link => schedules[1].works.some(row => row.id === link.predecessor)
          ? 'Общестрой' : 'Этот график' },
        { key: 'lag', label: 'Задержка, раб. дн.' },
        ...(!readOnly ? [{ key: 'remove', label: '', cell: (link: WorkLink) =>
          <Button variant="text" onClick={() => onApply(links.filter(item => item !== link))}>Убрать</Button> }] : []),
      ]} />
      {!readOnly && <>
        <div className="plan-schedule-fields">
          <Select label="Предшественник" value={predecessor} onValueChange={setPredecessor} placeholder="Выберите работу">
            <SelectGroup label="Этот график">{local.filter(row => row.id !== work.id)
              .map(row => <SelectItem key={row.id} value={row.id}>{row.name}</SelectItem>)}</SelectGroup>
            <SelectGroup label="Общестрой">{schedules[1].works.filter(row => row.id !== work.id && !local.includes(row))
              .map(row => <SelectItem key={row.id} value={row.id}>{row.name}</SelectItem>)}</SelectGroup>
          </Select>
          <NumberInput label="Задержка" unit="раб. дн." value={lag} onChange={setLag} />
        </div>
        <div><Button variant="filled" disabled={!predecessor || !/^[+-]?\d+$/.test(lag)} onClick={add}>
          Сохранить связь
        </Button></div>
      </>}
      {!!cycle.length && <Banner tone="error"><div>
        Связь образует цикл. Работы цикла:
        <ol className="list-decimal pl-5">{cycle.map(id => <li key={id}>{nameOf(id)}{id === 'base' ? ' · Общестрой' : ''}</li>)}</ol>
      </div></Banner>}
    </div>
  </Modal>;
}

export function RebuildDialog({ stale, onClose, onApply }: { stale: boolean; onClose: () => void; onApply: () => void }) {
  const [refreshed, setRefreshed] = useState(false);
  return <Modal open title="Перестроить по распределению" wide onOpenChange={open => !open && onClose()}
    footer={<><ModalExit /><ModalPrimary onClick={() => stale && !refreshed ? setRefreshed(true) : onApply()}>
      Перестроить
    </ModalPrimary></>}>
    <div className="grid gap-4">
      {refreshed && <Banner tone="warning">Данные изменились. Показан новый предпросмотр. Проверьте разницу и подтвердите ещё раз.</Banner>}
      <div className="plan-schedule-changes">
        <section className="plan-schedule-card"><h3 className="text-title-medium">Добавится · 1</h3>
          <p>Монтаж светильников · Корпус 2 · 12 шт.</p></section>
        <section className="plan-schedule-card"><h3 className="text-title-medium">Будет снята · 1</h3>
          <p>Установка щитов · Корпус 2 · 8 шт.</p></section>
        <section className="plan-schedule-card"><h3 className="text-title-medium">Изменится объём · 1</h3>
          <p>Монтаж кабельных лотков · Корпус 1</p>
          <p>700 → {refreshed ? '560' : '600'} м</p></section>
      </div>
      <section className="plan-schedule-card"><h3 className="text-title-medium">Будет удалена связь</h3>
        <p>Общестрой · Подготовка оснований → Электромонтаж · Установка щитов</p></section>
      <p className="text-body-medium text-on-surface-variant">Не войдут {refreshed ? 2 : 3} несопоставленные строки.</p>
    </div>
  </Modal>;
}

export function PrintDialog({ name, rows, onClose }: { name: string; rows: Work[]; onClose: () => void }) {
  return <Modal open title="Предпросмотр печати списка работ" wide onOpenChange={open => !open && onClose()}
    footer={<><ModalExit>Закрыть</ModalExit><ModalPrimary onClick={() => window.print()}>Печать</ModalPrimary></>}>
    <section className="plan-schedule-print grid gap-3">
      <h3 className="text-title-large">{name}</h3>
      <p className="text-body-medium">Бизнес-центр «Речной»</p>
      <DataGrid density="compact" rows={rows} rowKey={work => work.id} columns={[
        { key: 'name', label: 'Работа', wrap: true },
        { key: 'section', label: 'Раздел' },
        { key: 'volume', label: 'Объём', cell: work => `${work.volume} ${work.unit}` },
        { key: 'period', label: 'Сроки', cell: work => `${shortDate(work.start)} — ${shortDate(work.end)}` },
        { key: 'employees', label: 'Исполнители', wrap: true, cell: work => employeeNames(work) || '—' },
      ]} />
    </section>
  </Modal>;
}
