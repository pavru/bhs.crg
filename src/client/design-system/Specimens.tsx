import { useState } from 'react';
import { Button, IconButton, type ButtonVariant } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { DateField } from '@/shared/ui/DateField';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Switch } from '@/shared/ui/Switch';
import { RadioGroup } from '@/shared/ui/RadioGroup';
import { Chip } from '@/shared/ui/Chip';
import { Banner, type BannerTone } from '@/shared/ui/Banner';
import { StatusText, type StatusTone } from '@/shared/ui/StatusText';
import { EmptyState } from '@/shared/ui/EmptyState';
import { Modal } from '@/shared/ui/Modal';
import { Menu, MenuItem, MenuSeparator } from '@/shared/ui/Menu';
import { Tooltip } from '@/shared/ui/Tooltip';
import { Icon } from '@/shared/ui/Icon';
import { Add, Save, Description, MoreVert, Delete, Download, Check } from '@/shared/ui/icons';
import { Source, Specimen } from './Catalog';

export function Actions() {
  const [variant, setVariant] = useState<ButtonVariant>('filled');
  const [label, setLabel] = useState('Сохранить изменения');
  const [disabled, setDisabled] = useState(false);
  const [loading, setLoading] = useState(false);
  const [danger, setDanger] = useState(false);
  const [leading, setLeading] = useState(true);
  const [message, setMessage] = useState('Нажмите кнопку, наведите курсор или перейдите к ней клавишей Tab.');
  return <>
    <Specimen title="Кнопка в ваших руках" note="Меняйте свойства и проверяйте настоящий компонент: наведение, нажатие и клавиатурный фокус работают.">
      <div className="playground"><div className="playground-preview"><Button variant={variant} loading={loading} disabled={disabled} danger={danger} icon={leading ? <Icon icon={Save} size={18} /> : undefined} onClick={() => setMessage('Действие выполнено в примере. Данные приложения не менялись.')}>{label || 'Кнопка'}</Button><p role="status">{message}</p></div><div className="playground-controls"><TextField label="Подпись кнопки" density="dense" value={label} onChange={e => setLabel(e.target.value)} /><Select label="Вид кнопки" density="dense" value={variant} onValueChange={value => setVariant(value as ButtonVariant)}>{['filled', 'tonal', 'outlined', 'text'].map(v => <SelectItem key={v} value={v}>{v}</SelectItem>)}</Select><Checkbox checked={leading} onChange={setLeading} label="Ведущий значок" /><Checkbox checked={disabled} onChange={setDisabled} label="Отключена" /><Checkbox checked={loading} onChange={setLoading} label="Выполнение" /><Checkbox checked={danger} onChange={setDanger} label="Опасное действие" /></div></div>
      <Source file="src/shared/ui/Button.tsx">{`<Button\n  variant="${variant}"${disabled ? '\n  disabled' : ''}${loading ? '\n  loading' : ''}${danger ? '\n  danger' : ''}${leading ? '\n  icon={<Icon icon={Save} size={18} />}' : ''}\n>\n  ${label || 'Кнопка'}\n</Button>`}</Source>
    </Specimen>
    <Specimen title="Иерархия действий" note="Высота 40 px. На экране — одно главное действие filled; остальные подчинены ему."><div className="sample-row">{(['filled', 'tonal', 'outlined', 'text'] as const).map(v => <div className="labelled-sample" key={v}><Button variant={v} icon={<Icon icon={Add} size={18} />}>Добавить</Button><code>{v}</code></div>)}</div></Specimen>
    <Specimen title="Состояния и кнопки со значком"><div className="sample-row"><Button variant="tonal">Доступна</Button><Button variant="tonal" disabled>Недоступна</Button><Button variant="tonal" loading>Сохранение</Button><Button variant="outlined" danger>Удалить</Button><IconButton icon={MoreVert} label="Другие действия" /><IconButton icon={Add} label="Добавить запись" variant="tonal" /><IconButton icon={Delete} label="Удалить запись" danger /></div></Specimen>
  </>;
}

export function Fields() {
  const [value, setValue] = useState('Электромонтажные работы');
  const [density, setDensity] = useState<'form' | 'dense' | 'cell'>('form');
  const [error, setError] = useState(false);
  const [readOnly, setReadOnly] = useState(false);
  const [disabled, setDisabled] = useState(false);
  const [scan, setScan] = useState(false);
  const [date, setDate] = useState('2026-11-21');
  const [type, setType] = useState('act');
  return <>
    <Specimen title="Поле и его состояния" note="Одна оболочка OutlinedField: форма, плотное поле и клетка таблицы.">
      <div className="playground"><div className="playground-preview field-preview"><TextField label="Наименование работ" density={density} value={value} onChange={e => { setValue(e.target.value); setScan(false); }} error={error ? 'Укажите наименование работ' : undefined} hint="Так название будет показано в документе" readOnly={readOnly} disabled={disabled} fromScan={scan} /></div><div className="playground-controls"><Select label="Плотность поля" density="dense" value={density} onValueChange={v => setDensity(v as typeof density)}><SelectItem value="form">Форма · 56 px</SelectItem><SelectItem value="dense">Плотное · 40 px</SelectItem><SelectItem value="cell">Клетка · 40 px</SelectItem></Select><Checkbox checked={error} onChange={setError} label="Ошибка" /><Checkbox checked={readOnly} onChange={setReadOnly} label="Только чтение" /><Checkbox checked={disabled} onChange={setDisabled} label="Отключено" /><Checkbox checked={scan} onChange={setScan} label="Получено со скана" /></div></div>
      <Source file="src/shared/ui/TextField.tsx">{`<TextField\n  label="Наименование работ"\n  density="${density}"\n  value={value}\n  onChange={e => setValue(e.target.value)}${error ? '\n  error="Укажите наименование работ"' : ''}${readOnly ? '\n  readOnly' : ''}${disabled ? '\n  disabled' : ''}${scan ? '\n  fromScan' : ''}\n/>`}</Source>
    </Specimen>
    <Specimen title="Форма документа" note="Проверьте ввод даты, календарь, список и многострочное поле."><div className="example-form"><TextField label="Номер документа" defaultValue="АОСР-021" /><DateField label="Дата документа" value={date} onChange={setDate} /><Select label="Тип документа" value={type} onValueChange={setType}><SelectItem value="act">Акт скрытых работ</SelectItem><SelectItem value="certificate">Сертификат качества</SelectItem><SelectItem value="scheme">Исполнительная схема</SelectItem></Select><TextAreaField label="Примечание" defaultValue="Прокладка кабеля в лотках на отметке +3.600" rows={3} /></div></Specimen>
  </>;
}

export function Selection() {
  const [checked, setChecked] = useState(true);
  const [enabled, setEnabled] = useState(true);
  const [format, setFormat] = useState('pdf');
  const [filter, setFilter] = useState(true);
  return <>
    <Specimen title="Выбор и переключение" note="Переключатель меняет режим сразу. Флажок выбирает независимый параметр."><div className="selection-columns"><div><Checkbox checked={checked} onChange={setChecked} label="Включить в комплект" description="Документ попадёт в итоговую подборку" /><Checkbox checked={false} onChange={() => {}} indeterminate label="Выбрана часть документов" /><Checkbox checked={false} onChange={() => {}} disabled label="Недоступный параметр" /></div><div><label className="switch-label">Показывать архивные<Switch checked={enabled} onChange={setEnabled} label="Показывать архивные" /></label><label className="switch-label">Недоступный режим<Switch checked={false} onChange={() => {}} disabled label="Недоступный режим" /></label><RadioGroup label="Формат просмотра" items={[{ value: 'pdf', label: 'PDF-документ' }, { value: 'preview', label: 'Предварительный просмотр' }]} value={format} onChange={setFormat} /></div></div></Specimen>
    <Specimen title="Чипы отбора"><div className="sample-row"><Chip selected={filter} onClick={() => setFilter(!filter)} count={12}>Акты</Chip><Chip selected={!filter} onClick={() => setFilter(!filter)} count={4}>Схемы</Chip><Chip selected disabled>Недоступен</Chip><Chip icon={Add} onClick={() => setFilter(true)}>Добавить условие</Chip></div><p className="catalog-note">Нажмите на чип: отметка и состояние меняются вместе с выбором.</p><Source file="src/shared/ui/Chip.tsx">{'<Chip selected={selected} onClick={() => setSelected(!selected)} count={12}>\n  Акты\n</Chip>'}</Source></Specimen>
  </>;
}

export function Feedback() {
  const [tone, setTone] = useState<BannerTone>('warning');
  const [dismissed, setDismissed] = useState(false);
  return <>
    <Specimen title="Сообщение в контексте"><div className="feedback-tools"><Select label="Тон сообщения" density="dense" value={tone} onValueChange={v => { setTone(v as BannerTone); setDismissed(false); }}>{['info', 'success', 'warning', 'error'].map(t => <SelectItem key={t} value={t}>{t}</SelectItem>)}</Select></div>{!dismissed ? <Banner tone={tone} actions={[{ label: 'Понятно', onClick: () => setDismissed(true) }]}>В трёх документах не заполнены реквизиты. Проверьте их перед формированием PDF.</Banner> : <Button variant="outlined" onClick={() => setDismissed(false)}>Показать сообщение снова</Button>}<Source file="src/shared/ui/Banner.tsx">{`<Banner tone="${tone}" actions={[{ label: 'Понятно', onClick: dismiss }]}>\n  Проверьте реквизиты перед формированием PDF.\n</Banner>`}</Source></Specimen>
    <Specimen title="Состояния записей" note="Состояние передают подпись и знак, цвет лишь помогает их различить."><div className="sample-row">{([['neutral', 'Черновик'], ['primary', 'В работе'], ['success', 'Сформирован'], ['warning', 'Проверить'], ['error', 'Ошибка'], ['quiet', 'В архиве']] as [StatusTone, string][]).map(([tone, text]) => <StatusText key={tone} tone={tone}>{text}</StatusText>)}</div></Specimen>
    <Specimen title="Пока нет документов"><EmptyState icon={<Icon icon={Description} size={48} />} title="Добавьте первый документ" description="Выберите тип документа и заполните реквизиты. Он появится в комплекте." action={<Button variant="filled" onClick={() => setDismissed(false)} icon={<Icon icon={Add} size={18} />}>Добавить документ</Button>} /></Specimen>
  </>;
}

export function Layers() {
  const [open, setOpen] = useState(false);
  const [name, setName] = useState('Акт скрытых работ № 21');
  const [message, setMessage] = useState('');
  return <>
    <Specimen title="Окно редактирования" note="Проверьте фокус, закрытие по Escape и возврат к кнопке открытия."><div className="layer-preview"><Icon icon={Description} size={48} /><p>Небольшая форма открывается поверх текущего контекста.</p><Button variant="filled" onClick={() => setOpen(true)}>Открыть пример окна</Button></div><p role="status" className="catalog-note">{message}</p><Source file="src/shared/ui/Modal.tsx">{'<Modal open={open} onOpenChange={setOpen} title="Документ">\n  <TextField label="Наименование" value={name} onChange={changeName} />\n</Modal>'}</Source></Specimen>
    <Specimen title="Меню и подсказка"><div className="sample-row"><Menu trigger={<Button variant="outlined" trailingIcon={<Icon icon={MoreVert} size={18} />}>Действия с документом</Button>}><MenuItem icon={Download} onSelect={() => setMessage('Выбрано скачивание PDF в примере')}>Скачать PDF</MenuItem><MenuItem icon={Check} onSelect={() => setMessage('Выбрана проверка реквизитов')}>Проверить реквизиты</MenuItem><MenuSeparator /><MenuItem danger icon={Delete} onSelect={() => setMessage('Удаление показано как пример; данные не меняются')}>Удалить</MenuItem></Menu><Tooltip content="Сформировать документ можно после заполнения реквизитов"><Button variant="text">Наведите или перейдите клавишей Tab</Button></Tooltip></div></Specimen>
    <Modal open={open} onOpenChange={setOpen} title="Редактирование документа" footer={<><Button variant="text" onClick={() => setOpen(false)}>Отмена</Button><Button variant="filled" onClick={() => { setOpen(false); setMessage(`Сохранено в примере: ${name}`); }}>Сохранить</Button></>}><TextField label="Наименование" value={name} onChange={e => setName(e.target.value)} /></Modal>
  </>;
}
