import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Chip } from '@/shared/ui/Chip';
import { EditGrid } from '@/shared/ui/EditGrid';
import { StatusText } from '@/shared/ui/StatusText';
import { Tabs, TabPanel } from '@/shared/ui/Tabs';
import { TextField } from '@/shared/ui/TextField';
import { StoryProviders } from '../story-providers';
import '@fontsource/roboto/400.css';
import '@fontsource/roboto/500.css';
import '../catalog.css';

type Screen = 'schema' | 'entry' | 'review';
const fields = [
  { key: 'act', title: 'Номер акта скрытых работ' },
  { key: 'test', title: 'Протокол испытаний' },
  { key: 'note', title: 'Примечание прораба' },
];
const screens: { value: Screen; label: string }[] = [
  { value: 'schema', label: 'Настройка типа' },
  { value: 'entry', label: 'Ввод строки' },
  { value: 'review', label: 'Приёмка' },
];
const params = new URLSearchParams(location.search);
const variant = params.get('page');
const grouped = variant === 'b';
const focus = variant === 'c';
const theme = params.get('theme') === 'dark' ? 'dark' : 'light';

function App() {
  const [screen, setScreen] = useState<Screen>('schema');
  const [flagged, setFlagged] = useState(['act', 'test']);
  const [savedFlags, setSavedFlags] = useState(flagged);
  const [draft, setDraft] = useState<Record<string, string>>({});
  const [values, setValues] = useState(draft);
  const [filtered, setFiltered] = useState(false);
  const [expanded, setExpanded] = useState(false);
  const [card, setCard] = useState(false);
  const [accepted, setAccepted] = useState(false);

  // Фикстура ответа: при сохранении пересчитывает перечень так, как его возвращает сервер.
  const missing = fields.filter(field => savedFlags.includes(field.key) && !values[field.key]?.trim());
  const isMissing = (key: string) => missing.some(field => field.key === key);
  const fieldList = (list: typeof fields) => list.map(field =>
    <TextField key={field.key} label={field.title} value={draft[field.key] ?? ''}
      hint={!grouped && isMissing(field.key) ? 'Нужно к приёмке' : undefined}
      onChange={event => setDraft({ ...draft, [field.key]: event.target.value })} />);
  const list = (list: typeof fields) => <ul className="list-disc space-y-1 ps-5 text-body-medium">
    {list.map(field => <li key={field.key}>{field.title}</li>)}
  </ul>;
  const flagsChanged = flagged.join() !== savedFlags.join();
  const valueChanged = JSON.stringify(draft) !== JSON.stringify(values);

  return <main className="mx-auto flex max-w-5xl flex-col gap-4 p-4 md:p-8">
    <h1 className="text-headline-small">{grouped ? 'Б · Отдельная группа полей' : focus ? 'В · Переход к незаполненным' : 'А · Пометки рядом с полями'}</h1>
    <Tabs id="work24" value={screen} items={screens} onChange={setScreen} aria-label="Экран" />
    <TabPanel id="work24" value={screen}>
      {screen === 'schema' && <Card title="Строка работы: сведения">
        <EditGrid label="Поля типа" rows={fields} rowKey={field => field.key} columns={[
          { key: 'title', label: 'Подпись', rowHeader: true, cell: field => field.title },
          { key: 'kind', label: 'Вид', cell: () => 'Строка' },
          { key: 'acceptance', label: 'Нужно к приёмке', cell: field => <Checkbox
            aria-label={`Нужно к приёмке · ${field.title}`} checked={flagged.includes(field.key)}
            onChange={checked => setFlagged(checked ? [...flagged, field.key] : flagged.filter(key => key !== field.key))} /> },
        ]} />
        <div className="mt-4 flex justify-end">
          <Button variant="filled" disabled={!flagsChanged} onClick={() => setSavedFlags(flagged)}>Сохранить схему</Button>
        </div>
      </Card>}
      {screen === 'entry' && <Card title="Подробности строки 1 · Прокладка кабеля">
        <div className="space-y-4">
          <TextField label="Место работ" defaultValue="Корпус 1, щитовая" />
          {grouped ? <>
            {savedFlags.length > 0 && <fieldset className="space-y-3">
              <legend className="mb-2 text-title-small">Нужно к приёмке</legend>
              {missing.length > 0 && <StatusText tone="warning">Не заполнены поля: {missing.length}</StatusText>}
              {fieldList(fields.filter(field => savedFlags.includes(field.key)))}
            </fieldset>}
            {fieldList(fields.filter(field => !savedFlags.includes(field.key)))}
          </> : focus ? <>
            {missing.length > 0 && <Chip count={missing.length} selected={expanded}
              onClick={() => setExpanded(!expanded)}>Не заполнены поля</Chip>}
            {fieldList(expanded ? missing : fields)}
          </> : <div className="grid gap-3 md:grid-cols-2">{fieldList(fields)}</div>}
          <div className="flex justify-end">
            <Button variant="filled" disabled={!valueChanged} onClick={() => setValues(draft)}>Сохранить отчёт</Button>
          </div>
        </div>
      </Card>}
      {screen === 'review' && (!card ? <Card title="Очередь приёмки">
        <div className="space-y-3">
          <Chip count={!accepted && missing.length ? 1 : 0}
            selected={filtered} onClick={() => setFiltered(!filtered)}>Не заполнены поля</Chip>
          <EditGrid label="Отчёты" rows={accepted || (filtered && !missing.length) ? [] : [{ id: 'report' }]}
            rowKey={row => row.id} columns={[
              { key: 'author', label: 'Сотрудник', rowHeader: true, cell: () => 'Алексей Серов' },
              { key: 'marks', label: 'Пометки', cell: () => missing.length ?
                <StatusText tone="warning">не заполнены поля: {missing.length}</StatusText> : null },
              { key: 'open', label: 'Отчёт', cell: () => <Button variant="text" onClick={() => setCard(true)}>Открыть</Button> },
            ]} />
          {accepted && <Button variant="text" onClick={() => setCard(true)}>Открыть принятый отчёт</Button>}
        </div>
      </Card> : <Card title="Алексей Серов · Корпус 1">
        <div className="space-y-4">
          <div className="flex flex-wrap items-center gap-3">
            <Button variant="text" onClick={() => setCard(false)}>К очереди</Button>
            <StatusText tone={accepted ? 'success' : 'neutral'}>{accepted ? 'Принят' : 'Ждёт приёмки'}</StatusText>
          </div>
          {missing.length > 0 && !grouped && <StatusText tone="warning">Не заполнены поля: {missing.length}</StatusText>}
          {grouped && missing.length > 0 && <section className="space-y-2">
            <h2 className="text-title-small">Не заполнены поля · строка 1</h2>{list(missing)}
          </section>}
          <section className="space-y-2">
            <h2 className="text-title-small">Прокладка кабеля · строка 1</h2>
            {!grouped && missing.length > 0 && list(missing)}
            {focus && missing.length > 0 && <Button variant="text" onClick={() => { setExpanded(true); setScreen('entry'); }}>
              Перейти к полям строки 1
            </Button>}
          </section>
          {!accepted && <Button variant="filled" onClick={() => setAccepted(true)}>Принять</Button>}
        </div>
      </Card>)}
    </TabPanel>
  </main>;
}

document.documentElement.dataset.theme = theme;
createRoot(document.getElementById('root')!).render(
  <StoryProviders theme={theme} component="Work24"><App /></StoryProviders>,
);
