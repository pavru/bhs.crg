import { useState } from 'react';
import { Button, IconButton } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Card } from '@/shared/ui/Card';
import { Chip } from '@/shared/ui/Chip';
import { RadioGroup } from '@/shared/ui/RadioGroup';
import { StatusText } from '@/shared/ui/StatusText';
import { Modal } from '@/shared/ui/Modal';
import { Icon } from '@/shared/ui/Icon';
import { Add, Description, Close } from '@/shared/ui/icons';
import { Source, Specimen } from './Catalog';

type Document = { id: string; name: string; type: string; date: string; ready: boolean };
const initial: Document[] = [
  { id: 'АОСР-021', name: 'Прокладка кабелей в лотках', type: 'Акт скрытых работ', date: '21.11.2026', ready: true },
  { id: 'АОСР-022', name: 'Устройство заземления', type: 'Акт скрытых работ', date: '21.11.2026', ready: false },
  { id: 'ИС-014', name: 'Кабельные трассы. Отметка +3.600', type: 'Исполнительная схема', date: '20.11.2026', ready: true },
  { id: 'СК-008', name: 'Кабель ВВГнг(А)-LS 5×6', type: 'Сертификат качества', date: '19.11.2026', ready: true },
  { id: 'АОСР-023', name: 'Монтаж светильников', type: 'Акт скрытых работ', date: '21.11.2026', ready: false },
];
const layouts = [
  { value: 'list', label: 'А · Список' },
  { value: 'cards', label: 'Б · Карточки' },
  { value: 'detail', label: 'В · С панелью' },
];
const status = (row: Document) => <StatusText tone={row.ready ? 'success' : 'warning'}>{row.ready ? 'Сформирован' : 'Проверить'}</StatusText>;
const columns: DataGridColumn<Document>[] = [
  { key: 'name', label: 'Документ', rowHeader: true, wrap: true, cell: row => <div className="document-name"><Icon icon={Description} size={18} /><span>{row.name}<small>{row.id}</small></span></div> },
  { key: 'type', label: 'Тип', wrap: true },
  { key: 'date', label: 'Дата', width: 'w-32' },
  { key: 'ready', label: 'Состояние', width: 'w-36', cell: status },
];

export function DocumentScreen() {
  const [layout, setLayout] = useState('list');
  const [approved, setApproved] = useState<string | null>(null);
  const [rows, setRows] = useState(initial);
  const [search, setSearch] = useState('');
  const [issues, setIssues] = useState(false);
  const [selected, setSelected] = useState<Document | null>(initial[1]);
  const [create, setCreate] = useState(false);
  const [name, setName] = useState('');
  const [message, setMessage] = useState('');
  const visible = rows.filter(row => (!issues || !row.ready) && `${row.name} ${row.id} ${row.type}`.toLocaleLowerCase('ru').includes(search.toLocaleLowerCase('ru')));
  const openRow = (row: Document) => { setSelected(row); if (layout !== 'detail') setMessage(`Выбран документ: ${row.name}`); };
  const add = () => {
    if (!name.trim()) return;
    const row = { id: `ДОК-${String(rows.length + 1).padStart(3, '0')}`, name: name.trim(), type: 'Акт скрытых работ', date: '21.11.2026', ready: false };
    setRows([...rows, row]); setCreate(false); setName(''); setMessage(`Добавлен в пример: ${row.name}`);
  };
  return <>
    <Specimen title="Один состав, три компоновки" note="Сравните способы работы с комплектом: быстрый просмотр списка, визуальный обзор и редактирование рядом со списком.">
      <div className="layout-options"><RadioGroup aria-label="Компоновка экрана" orientation="horizontal" items={layouts} value={layout} onChange={setLayout} /><Button variant="outlined" onClick={() => setApproved(layout)}>Выбрать эту компоновку</Button></div>
      {approved && <p role="status" className="catalog-note">Для примера выбрана компоновка «{layouts.find(l => l.value === approved)?.label}». Вы можете продолжить сравнение.</p>}
    </Specimen>
    <section className="screen-preview" aria-label="Демонстрационный экран документов">
      <div className="screen-breadcrumb">Корпус 1 / Электроснабжение</div>
      <div className="screen-heading"><div><h2>Документы комплекта</h2><p>Шифр ЭС-01 · Рабочая версия</p></div><Button variant="filled" icon={<Icon icon={Add} size={18} />} onClick={() => setCreate(true)}>Добавить документ</Button></div>
      <div className="screen-filters"><div className="sample-row"><Chip selected={!issues} onClick={() => setIssues(false)} count={rows.length}>Все документы</Chip><Chip selected={issues} onClick={() => setIssues(!issues)} count={rows.filter(r => !r.ready).length}>Проверить</Chip></div><TextField label="Поиск документов" placeholder="Найти в комплекте…" density="dense" value={search} onChange={e => setSearch(e.target.value)} /></div>
      <div className={`screen-body ${layout === 'detail' && selected ? 'with-detail' : ''}`}>
        <div className="screen-list">
          {layout === 'cards' ? <div className="document-cards">{visible.map(row => <Card key={row.id} title={row.name} icon={Description} variant="outlined" onClick={() => openRow(row)}><p className="catalog-note">{row.id} · {row.type}</p><div className="document-card-bottom"><span>{row.date}</span>{status(row)}</div></Card>)}{!visible.length && <p className="catalog-note">Документы по вашему запросу не найдены.</p>}</div> : <DataGrid columns={layout === 'detail' ? columns.filter(c => c.key !== 'type') : columns} rows={visible} rowKey={row => row.id} density={layout === 'detail' ? 'compact' : 'regular'} state={search || issues ? 'filtered-out' : 'no-data'} onRowOpen={openRow} />}
        </div>
        {layout === 'detail' && selected && <aside className="document-detail"><div className="detail-heading"><h3>Реквизиты документа</h3><IconButton icon={Close} label="Закрыть панель реквизитов" onClick={() => setSelected(null)} /></div><p className="catalog-note">{selected.id}</p><TextField label="Наименование" value={selected.name} onChange={e => { const updated = { ...selected, name: e.target.value }; setSelected(updated); setRows(rows.map(r => r.id === updated.id ? updated : r)); }} /><TextField label="Тип документа" value={selected.type} readOnly />{status(selected)}<Button variant="tonal" icon={<Icon icon={Description} size={18} />} onClick={() => setMessage('Пример предварительного просмотра. PDF в этом каталоге не формируется.')}>Предварительный просмотр</Button></aside>}
      </div>
      <div className="screen-bottom"><span>Показано {visible.length} из {rows.length}</span><span role="status">{message || 'Демонстрационные данные'}</span></div>
    </section>
    <Source file="shared/ui → композиция экрана">{'// Во всех трёх вариантах используются одни компоненты\n<Button variant="filled" icon={<Icon icon={Add} size={18} />}>\n  Добавить документ\n</Button>\n<Chip selected={issues} onClick={toggleIssues}>Проверить</Chip>\n<DataGrid columns={columns} rows={rows} onRowOpen={openDocument} />\n// Вариант Б: Card. Вариант В: DataGrid + панель с TextField.'}</Source>
    <Modal open={create} onOpenChange={setCreate} title="Добавить документ в пример" footer={<><Button variant="text" onClick={() => setCreate(false)}>Отмена</Button><Button variant="filled" disabled={!name.trim()} onClick={add}>Добавить</Button></>}><TextField label="Наименование документа" value={name} onChange={e => setName(e.target.value)} hint="Будет добавлен только в текущий пример" /></Modal>
  </>;
}
