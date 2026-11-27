import { useEffect, useRef, useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { Chip } from '@/shared/ui/Chip';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { PageHeader } from '@/shared/ui/PageHeader';
import { Select, SelectItem } from '@/shared/ui/Select';
import { SideSheet } from '@/shared/ui/SideSheet';
import { StatusText } from '@/shared/ui/StatusText';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { useTheme } from '@/shared/ui/themeContext';
import { HeaderFields, LineFields, type Contents, type Line } from './CorrectionFields';

const freshLine = (id: string): Line => ({
  id, work: '', variant: '', volume: '', unit: '', section: '', location: '', comment: '', act: '',
  materials: [], isCrew: false, crewRole: '', participants: [],
});

const initial: Contents = {
  hours: '8', note: 'Работы по наряду № 21', lines: [
    { ...freshLine('cable'), work: 'Прокладка кабеля', variant: 'В лотке', volume: '120', unit: 'м',
      section: 'Электроснабжение', location: 'Щитовая А-12', materials: [{ kind: 'ВВГнг 3×2,5', note: '' }], photo: 'pending' },
    { ...freshLine('tray'), work: 'Монтаж лотка', volume: '18', unit: 'м', section: 'Электроснабжение',
      location: 'Коридор Б', act: 'А-16', photo: 'stored' },
  ],
};

type Version = { number: number; contents: Contents; reason?: string };
type ResponseMode = 'success' | 'conflict' | 'unknown';
type Outcome = 'ready' | 'sending' | 'conflict' | 'compare' | 'unknown';
const titles = { a: 'Правка в карточке', b: 'Боковая панель', c: 'По шагам' };

function LineSummary({ contents }: { contents: Contents }) {
  return <Card title="Работы">
    <ol className="divide-y divide-outline-variant">
      {contents.lines.map(line => <li key={line.id} className="space-y-1 py-3 text-body-medium">
        <strong>{line.work}</strong>
        <p>{line.volume} {line.unit} · {line.location || 'Место не указано'}</p>
        <p>Акт · {line.act || '—'}</p>
        {line.photo && <StatusText>{line.photo === 'stored' ? 'Фото сохранено' : 'Ожидается файл фото'}</StatusText>}
      </li>)}
    </ol>
  </Card>;
}

function Difference({ base, draft }: { base: Contents; draft: Contents }) {
  const removed = base.lines.filter(line => !draft.lines.some(item => item.id === line.id));
  const added = draft.lines.filter(line => !base.lines.some(item => item.id === line.id));
  const updated = draft.lines.filter(line => base.lines.some(item => item.id === line.id && JSON.stringify(item) !== JSON.stringify(line)));
  return <Card title="Проверка изменений">
    <ul className="space-y-1 text-body-medium">
      {base.hours !== draft.hours && <li>Часы · {base.hours || '—'} → {draft.hours || '—'}</li>}
      {base.note !== draft.note && <li>Примечание · {draft.note || '—'}</li>}
      {updated.map(line => <li key={line.id}>Исправлено · {line.work}{line.act && ` · Акт ${line.act}`}</li>)}
      {added.map(line => <li key={line.id}>Добавлено · {line.work}</li>)}
      {removed.map(line => <li key={line.id}>Удалено · {line.work}{line.photo && ' · Фото останется в прежней версии'}</li>)}
      {base.lines.map(line => line.id).join() !== draft.lines.map(line => line.id).join() && <li>Состав и порядок строк обновятся.</li>}
    </ul>
  </Card>;
}

export function CorrectionMockup({ variant }: { variant: 'a' | 'b' | 'c' }) {
  const { resolvedTheme, setTheme } = useTheme();
  const [versions, setVersions] = useState<Version[]>([{ number: 1, contents: initial }]);
  const [number, setNumber] = useState<number>();
  const [draft, setDraft] = useState<Contents>();
  const [basis, setBasis] = useState<Version>();
  const [reason, setReason] = useState('');
  const [step, setStep] = useState<'fields' | 'review'>('fields');
  const [selected, setSelected] = useState('cable');
  const [mode, setMode] = useState<ResponseMode>('success');
  const [outcome, setOutcome] = useState<Outcome>('ready');
  const [leaving, setLeaving] = useState<{ number?: number }>();
  const [announcement, setAnnouncement] = useState('');
  const openButton = useRef<HTMLButtonElement>(null);
  const editorTitle = useRef<HTMLHeadingElement>(null);
  const cancelTitle = useRef<HTMLSpanElement>(null);
  const nextId = useRef(0);
  const current = versions.at(-1)!;
  const shown = versions.find(version => version.number === number) ?? current;
  const active = !!draft && !!basis;
  const dirty = !!draft && (JSON.stringify(draft) !== JSON.stringify(basis?.contents) || !!reason);
  const valid = !!draft && draft.lines.every(line => line.work && line.unit && line.volume.trim());
  const latest = current.number === shown.number;
  const replacement = versions.find(version => version.number === shown.number + 1);
  const currentLine = draft?.lines.find(line => line.id === selected) ?? draft?.lines[0];

  useEffect(() => {
    if (active && variant !== 'b') editorTitle.current?.focus();
  }, [active, step, variant]);

  useEffect(() => {
    if (variant === 'b' && leaving) cancelTitle.current?.focus();
  }, [variant, leaving]);

  function open() {
    setBasis(current);
    setDraft(current.contents);
    setSelected(current.contents.lines[0]?.id ?? '');
    setStep('fields');
    setReason('');
    setOutcome('ready');
    setAnnouncement('');
  }

  function close(next?: number) {
    setDraft(undefined);
    setBasis(undefined);
    setReason('');
    setOutcome('ready');
    setNumber(next);
    requestAnimationFrame(() => openButton.current?.focus());
  }

  function requestClose(next?: number) {
    if (outcome === 'sending') return;
    if (dirty) setLeaving({ number: next });
    else close(next);
  }

  function saved(version: Version) {
    close();
    setAnnouncement(`Версия ${version.number} сохранена.`);
  }

  function submit() {
    if (!draft || !basis || !valid || !reason.trim() || outcome !== 'ready') return;
    setOutcome('sending');
    setTimeout(() => {
      if (mode === 'conflict') {
        setVersions(list => [...list, { number: current.number + 1, contents: { ...current.contents, hours: '7' }, reason: 'Уточнение по табелю' }]);
        setOutcome('conflict');
      } else {
        const written = { number: current.number + 1, contents: draft, reason: reason.trim() };
        setVersions(list => [...list, written]);
        if (mode === 'unknown') setOutcome('unknown');
        else saved(written);
      }
      setMode('success');
    }, 450);
  }

  function acceptBasis() {
    if (!basis || !draft) return;
    setDraft({ ...draft, hours: draft.hours === basis.contents.hours ? current.contents.hours : draft.hours });
    setBasis(current);
    setOutcome('ready');
  }

  function addLine() {
    if (!draft) return;
    const line = freshLine(`new-${++nextId.current}`);
    setDraft({ ...draft, lines: [...draft.lines, line] });
    setSelected(line.id);
  }

  const fields = draft && <>
    <HeaderFields draft={draft} onChange={setDraft} />
    <div className="flex flex-wrap items-center justify-between gap-2">
      <h3 className="text-title-medium">Строки работы</h3>
      <Button variant="tonal" onClick={addLine}>Добавить строку</Button>
    </div>
    {variant === 'b' && currentLine && <Select label="Строка" value={currentLine.id} onValueChange={setSelected}>
      {draft.lines.map((line, index) => <SelectItem key={line.id} value={line.id}>{index + 1}. {line.work || 'Новая строка'}</SelectItem>)}
    </Select>}
    {(variant === 'b' && currentLine ? [currentLine] : draft.lines).map(line => {
      const index = draft.lines.indexOf(line);
      return <LineFields key={line.id} line={line} number={index + 1} last={index + 1 === draft.lines.length}
        onChange={edited => setDraft({ ...draft, lines: draft.lines.map(item => item.id === line.id ? edited : item) })}
        onRemove={() => setDraft({ ...draft, lines: draft.lines.filter(item => item.id !== line.id) })}
        onMove={direction => {
          const lines = draft.lines.slice();
          lines.splice(index, 1);
          lines.splice(index + direction, 0, line);
          setDraft({ ...draft, lines });
        }} />;
    })}
  </>;

  const reviewing = variant !== 'c' || step === 'review';
  const confirmInSheet = variant === 'b' && !!leaving;
  const editor = draft && basis && <section aria-label="Редактор исправления" className="@container min-w-0 space-y-4">
    {variant !== 'b' && <h2 ref={editorTitle} tabIndex={-1} className="text-title-large">
      {variant === 'c' && reviewing ? 'Проверка и причина' : 'Исправление отчёта'}
    </h2>}
    <p className="text-body-small text-on-surface-variant">На основе версии {basis.number} · Ночная смена</p>
    {outcome === 'conflict' && <Banner tone="warning" actions={[{ label: 'Перечитать и сверить', onClick: () => setOutcome('compare') }]}>
      Пока форма была открыта, отчёт изменили. Черновик сохранён.
    </Banner>}
    {outcome === 'compare' && <Banner tone="warning" actions={[{ label: 'Подтвердить новую основу', onClick: acceptBasis }]}>
      <span className="block break-words">Часы за смену · было {basis.contents.hours || '—'} · текущая версия {current.contents.hours || '—'} · черновик {draft.hours || '—'}</span>
      <span className="block">{draft.hours === basis.contents.hours ? 'Часы из текущей версии будут сохранены.' : 'После подтверждения сохранится ваш вариант часов.'}</span>
    </Banner>}
    {outcome === 'unknown' && <Banner tone="warning" actions={[{ label: 'Проверить версию и журнал', onClick: () => saved(current) }]}>
      Ответ не получен. Проверьте, сохранилось ли исправление.
    </Banner>}
    <fieldset className="min-w-0 space-y-4" disabled={outcome !== 'ready' || confirmInSheet}>
      {variant !== 'c' || step === 'fields' ? fields : <Difference base={basis.contents} draft={draft} />}
      {reviewing && <TextAreaField label="Причина исправления" rows={3} required maxLength={1000}
        hint="Причина появится в истории версий" value={reason} onChange={e => setReason(e.target.value)} />}
    </fieldset>
    {confirmInSheet ? <Banner tone="warning" actions={[
      { label: 'Продолжить правку', onClick: () => setLeaving(undefined) },
      { label: 'Отменить исправление', onClick: () => { close(leaving.number); setLeaving(undefined); } },
    ]}><span ref={cancelTitle} tabIndex={-1} className="block">Отменить исправление?</span><span className="block">Введённые значения и причина будут потеряны.</span></Banner>
    : <div className="flex flex-wrap justify-end gap-2">
      <Button variant="text" onClick={() => requestClose()} disabled={outcome === 'sending'}>Отмена</Button>
      {variant === 'c' && reviewing && <Button variant="outlined" onClick={() => setStep('fields')} disabled={outcome !== 'ready'}>Назад к полям</Button>}
      {reviewing ? <Button variant="filled" loading={outcome === 'sending'} disabled={outcome !== 'ready' || !valid || !dirty || !reason.trim()}
        onClick={submit}>Сохранить новой версией</Button>
        : <Button variant="filled" disabled={!valid || outcome !== 'ready'} onClick={() => setStep('review')}>Проверить изменения</Button>}
    </div>}
  </section>;

  return <main className="mx-auto max-w-[1280px] space-y-4 p-4 md:p-6" onKeyDown={event => {
    if (event.key === 'Escape' && active && variant !== 'b' && !leaving && !event.defaultPrevented) requestClose();
  }}>
    <div className="flex flex-wrap items-center justify-between gap-3" aria-label="Настройки просмотра макета">
      <span className="text-label-large">{titles[variant]}</span>
      <div className="flex flex-wrap items-center gap-2">
        <div className="min-w-48"><Select label="Ответ сервера в примере" density="dense" value={mode}
          onValueChange={value => setMode(value as ResponseMode)}>
          <SelectItem value="success">Запись успешна</SelectItem>
          <SelectItem value="conflict">Конфликт версий (409)</SelectItem>
          <SelectItem value="unknown">Ответ потерян</SelectItem>
        </Select></div>
        <Chip selected={resolvedTheme === 'dark'} onClick={() => setTheme(resolvedTheme === 'dark' ? 'light' : 'dark')}>Тёмная тема</Chip>
      </div>
    </div>
    <PageHeader title="Андрей Лебедев" subtitle="Главный корпус · 19.10.2026 · ночь"
      actions={latest && !active ? <Button variant="filled" ref={openButton} onClick={open}>Исправить отчёт</Button> : undefined} />
    <div className="flex flex-wrap items-center gap-3">
      <StatusText tone="success">Принят</StatusText><StatusText>Период закрыт</StatusText>
      {shown.contents.lines.some(line => !line.act) && <StatusText tone="warning">Не заполнены поля заказчика</StatusText>}
    </div>
    {announcement && <Banner tone="success">{announcement}</Banner>}
    {versions.length > 1 && <div className="max-w-sm"><Select label="Версия отчёта" value={String(shown.number)}
      onValueChange={value => active ? requestClose(Number(value)) : setNumber(Number(value))}>
      {[...versions].reverse().map(version => <SelectItem key={version.number} value={String(version.number)}>
        Версия {version.number}{version.number === current.number ? ' · действующая' : version.number === 1 ? ' · на момент закрытия' : ''}
      </SelectItem>)}
    </Select></div>}
    {replacement && <p className="text-body-medium">Заменил Павел Соколов · Причина исправления · {replacement.reason}</p>}
    <div className={variant === 'c' && active ? undefined : 'grid items-start gap-4 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]'}>
      {active && variant !== 'b' ? editor : <div className="space-y-3">
        <p className="text-body-medium">{shown.contents.hours} ч · {shown.contents.note}</p>
        <LineSummary contents={shown.contents} />
      </div>}
      {!(variant === 'c' && active) && <Card title="Журнал отчёта">
        <ol className="space-y-3 text-body-medium">
          {versions.slice(1).reverse().map(version => <li key={version.number}>Версия {version.number} · Исправил Павел Соколов<br />{version.reason}</li>)}
          <li>19.10.2026 · Отчёт принят</li>
        </ol>
      </Card>}
    </div>
    {variant === 'b' && <SideSheet open={active} variant="modal" title="Исправление отчёта" onClose={() => requestClose()}>{editor}</SideSheet>}
    <ConfirmDialog open={variant !== 'b' && !!leaving} onOpenChange={open => { if (!open) setLeaving(undefined); }}
      title="Отменить исправление?" description="Введённые значения и причина будут потеряны."
      cancelLabel="Продолжить правку" confirmLabel="Отменить исправление" onConfirm={() => close(leaving?.number)} />
  </main>;
}
