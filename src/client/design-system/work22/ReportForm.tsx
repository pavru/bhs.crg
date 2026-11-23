import { useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button, IconButton } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { Icon } from '@/shared/ui/Icon';
import { ArrowBack, Save, Send } from '@/shared/ui/icons';
import { PageHeader } from '@/shared/ui/PageHeader';
import { ShiftDateHeader } from '@/shared/ui/ShiftDateHeader';
import { SitePicker } from '@/shared/ui/SitePicker';
import { Tabs, TabPanel } from '@/shared/ui/Tabs';
import { TextField } from '@/shared/ui/TextField';
import { ReportLines } from './ReportLines';
import {
  assignmentBoundary, dateText, nightSuggestion, shiftText, siteName, sites, statuses, submissionRefusal,
  type Report, type Scenario, type Variant,
} from './model';

type Step = 'general' | 'lines' | 'submit';
const steps: { value: Step; label: string }[] = [
  { value: 'general', label: 'Смена' }, { value: 'lines', label: 'Работы' }, { value: 'submit', label: 'Подача' },
];

export function ReportForm({ variant, scenario, report, isNew, onChange, onSave, onOpenExisting, onBack }: {
  variant: Variant; scenario: Scenario; report: Report; isNew: boolean;
  onChange: (next: Report) => void; onSave: (next: Report) => void; onOpenExisting: () => void; onBack: () => void;
}) {
  const [step, setStep] = useState<Step>('general');
  const [notice, setNotice] = useState<'saved' | 'duplicate' | 'stale' | 'required' | 'submitted' | null>(null);
  const [reread, setReread] = useState(false);
  const closed = scenario === 'closed';
  const locked = closed || report.status === 'accepted';
  const shiftLocked = locked || report.status === 'submitted';
  const ownSites = scenario === 'manager' && report.employee === 'Артём Петров' ? sites.slice(0, 1) : sites;
  const selectedSite = sites.find(site => site.id === report.site);
  const pending = report.lines.flatMap(line => line.photos).filter(photo => photo.state === 'pending').length;
  const update = (patch: Partial<Report>) => onChange({ ...report, ...patch });

  function changeSite(id: string) {
    const next = sites.find(site => site.id === id);
    if (!next) return;
    const suggested = scenario === 'night' && id === 'terminal' ? nightSuggestion : next.suggested;
    update(isNew ? { site: id, date: suggested.date, shift: suggested.shift } : { site: id });
  }

  function save() {
    if (isNew && scenario === 'duplicate') return setNotice('duplicate');
    if (scenario === 'stale' && !reread) return setNotice('stale');
    onSave(report);
    setNotice('saved');
  }

  function submit() {
    if (isNew && scenario === 'duplicate') return setNotice('duplicate');
    if (scenario === 'required' && report.lines[submissionRefusal.line - 1]?.material === '') {
      setNotice('required');
      setStep('lines');
      return;
    }
    onSave({ ...report, status: 'submitted', note: undefined });
    setNotice('submitted');
  }

  const header = <Card title="Стройка и смена">
    <div className="work22-header-fields">
      <SitePicker sites={ownSites} value={report.site} disabled={shiftLocked} onChange={changeSite} />
      <ShiftDateHeader date={report.date} shift={report.shift} today={selectedSite?.today} locked={shiftLocked} busy={false}
        lockReason={report.status === 'submitted' ? 'Смена меняется только после возврата' : undefined}
        onDate={date => update({ date })} onShift={shift => update({ shift })} onIncomplete={() => {}} />
      <TextField label="Часы за смену" value={report.hours} inputMode="decimal" readOnly={locked}
        onChange={event => update({ hours: event.target.value })} />
    </div>
    <div className="flex flex-wrap gap-x-4 gap-y-1 text-body-small text-on-surface-variant">
      <span>{report.employee && ownSites.length === 1 ? 'стройки сотрудника в вашем охвате (1)' : assignmentBoundary.text}</span>
      {selectedSite && <span>Сегодня на стройке · {dateText(selectedSite.today)}</span>}
    </div>
  </Card>;

  const lines = <section className="space-y-3" aria-label="Выполненные работы">
    <h3 className="text-title-medium">Выполненные работы</h3>
    <ReportLines variant={variant} lines={report.lines} closed={closed} locked={locked}
      missing={notice === 'required' && !report.lines[submissionRefusal.line - 1]?.material ? submissionRefusal : undefined}
      onChange={updated => update({ lines: updated })} />
  </section>;

  const check = <Card title="Отчёт к подаче">
    <dl className="work22-summary">
      <div><dt>Стройка</dt><dd>{siteName(report.site)}</dd></div>
      <div><dt>Дата и смена</dt><dd>{dateText(report.date)} · {shiftText[report.shift]}</dd></div>
      <div><dt>Часы</dt><dd>{report.hours || '—'}</dd></div>
      <div><dt>Строки работ</dt><dd>{report.lines.length}</dd></div>
    </dl>
    {pending > 0 && <p className="text-body-medium text-on-surface-variant">Загрузка фото продолжится после подачи.</p>}
  </Card>;

  return <>
    <PageHeader icon={<IconButton icon={ArrowBack} label="К отчётам" onClick={onBack} />}
      title={isNew ? 'Новый отчёт' : `${dateText(report.date)} · ${shiftText[report.shift]}`}
      subtitle={report.employee ? `Вводит Иванов за ${report.employee === 'Артём Петров' ? 'Артёма Петрова' : 'Дениса Волкова'}` : siteName(report.site)} />
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-body-medium">
      <span className="rounded-full bg-secondary-container px-3 py-1 text-label-large text-on-secondary-container">{statuses[report.status]}</span>
      {report.note && <span className="text-on-surface-variant">{report.note}</span>}
    </div>
    {closed && <Banner tone="info">Период закрыт. Доступна досылка ранее заявленных фото.</Banner>}
    {report.status === 'submitted' && !closed && <Banner tone="info">
      Изменения будут видны менеджеру как правка после подачи.
    </Banner>}
    {notice === 'duplicate' && <Banner tone="error" actions={[{ label: 'Открыть', onClick: onOpenExisting }]}>
      На эту смену отчёт уже есть.
    </Banner>}
    {notice === 'stale' && <Banner tone="error" actions={[{ label: 'Перечитать', onClick: () => { setReread(true); setNotice(null); } }]}>
      Отчёт тем временем изменили — перечитайте. Ваши изменения сохранены в форме.
    </Banner>}
    {notice === 'required' && !report.lines[submissionRefusal.line - 1]?.material &&
      <Banner tone="error">Строка {submissionRefusal.line} · {submissionRefusal.message}</Banner>}
    {notice === 'saved' && <Banner tone="success">Отчёт сохранён.</Banner>}
    {notice === 'submitted' && <Banner tone="success">
      Отчёт подан.{pending > 0 && ' Загрузка фото продолжается.'}
    </Banner>}
    {variant === 'c' ? <>
      <Tabs<Step> id="work22-steps" aria-label="Шаги отчёта" value={step} items={steps} onChange={setStep} />
      <TabPanel id="work22-steps" value={step}>
        {step === 'general' ? header : step === 'lines' ? lines : check}
      </TabPanel>
    </> : <>{header}{lines}</>}
    <div className="work22-actions">
      {variant === 'c' && step !== 'general' && <Button variant="text"
        onClick={() => setStep(step === 'submit' ? 'lines' : 'general')}>Назад</Button>}
      {!locked && <Button variant="outlined" icon={<Icon icon={Save} size={18} />} onClick={save}>Сохранить</Button>}
      {variant === 'c' && step !== 'submit' ? <Button variant="filled" onClick={() => setStep(step === 'general' ? 'lines' : 'submit')}>
        {step === 'general' ? 'К работам' : 'К подаче'}
      </Button> : !locked && report.status !== 'submitted' && <Button variant="filled" icon={<Icon icon={Send} size={18} />}
        onClick={submit}>Подать отчёт</Button>}
    </div>
  </>;
}
