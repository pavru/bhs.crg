import { useId, useState, type ReactNode } from 'react';
import { NARROW, useMedia } from '@/shared/hooks/useMedia';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DateField } from '@/shared/ui/DateField';
import { Icon } from '@/shared/ui/Icon';
import { Apartment, ArrowForward, CalendarMonth, CheckCircle, LockIcon, Payments, Schedule, Warning } from '@/shared/ui/icons';
import { Modal, ModalExit, ModalNote, ModalPrimary } from '@/shared/ui/Modal';
import { ModalBanner } from '@/shared/ui/ModalBanner';
import { StatusText } from '@/shared/ui/StatusText';
import { WindowStep } from '@/shared/ui/WindowStep';
import { companyRule, companyScope, costsRule, end, period, preview, start, workRule, type Preview, type Scenario, type Variant } from './model';

function Dates() {
  return <div className="r4-dates grid grid-cols-2 gap-3">
    <DateField label="С" value={start} readOnly onChange={() => {}} />
    <DateField label="По (включительно)" value={end} readOnly onChange={() => {}} />
  </div>;
}

function CompanyRule() {
  return <p className="r4-rule text-body-small">
    <Icon icon={Schedule} size={18} />
    <span>{companyRule}</span>
  </p>;
}

function WorkHeading() {
  return <div className="r4-module-heading">
    <h3 className="text-title-medium">Учёт работ</h3>
    <p className="text-body-small text-on-surface-variant">{workRule}</p>
  </div>;
}

function Availability({ report, id }: { report: Preview; id: string }) {
  return <section className={`r4-availability ${report.canClose ? 'r4-available' : 'r4-blocked'}`} aria-labelledby={id}>
    <h4 id={id} className="r4-availability-heading text-title-small">
      <Icon icon={report.canClose ? CheckCircle : LockIcon} size={18} />
      {report.canClose ? 'Нет запретов на закрытие' : 'Закрытие запрещено'}
    </h4>
    {report.blocking.length > 0 && <ul className="r4-blockers text-body-medium">
      {report.blocking.map(line => <li key={line.text}>
        <div className="r4-count-line"><span>{line.text}</span><strong className="tabular-nums">{line.count}</strong></div>
        {line.note && <p className="text-body-small">{line.note}</p>}
      </li>)}
    </ul>}
  </section>;
}

function ClosingGroup({ title, text, count, before, warn }: {
  title: string; text: string; count: number; before?: number; warn?: boolean;
}) {
  return <div className="r4-group">
    <h4 className="text-label-medium text-on-surface-variant">{title}</h4>
    <p className="r4-count-line text-body-medium">
      <span className="flex items-center gap-2">{warn && <Icon icon={Warning} size={18} className="text-warning" />}{text}</span>
      <strong className="tabular-nums">{count}</strong>
    </p>
    {before !== undefined && <p className="text-body-small text-on-surface-variant">Было {before} в предыдущем перечне.</p>}
  </div>;
}

function Costs() {
  return <section className="r4-module r4-costs" aria-label="Счета и накладные">
    <div className="r4-module-heading">
      <h3 className="text-title-medium">Счета и накладные</h3>
      <p className="text-body-small text-on-surface-variant">{costsRule}</p>
    </div>
    <ClosingGroup title="Не завершено · закрытию не мешает" text="Не разобраны счета" count={2} warn />
    <ClosingGroup title="Войдёт в закрытый период" text="Проведённые накладные" count={8} />
  </section>;
}

function Snapshot({ report, refreshed, wide, availability }: {
  report: Preview; refreshed: boolean; wide?: boolean; availability: ReactNode;
}) {
  const accepted = <ClosingGroup title="Войдёт в закрытый период" text="Принятые отчёты" count={report.accepted}
    before={refreshed ? 24 : undefined} />;
  return wide ? <div className="r4-columns">
    <section className="r4-module r4-check-column" aria-label="Проверка учёта работ"><WorkHeading />{availability}</section>
    <div className="r4-stack">
      <section className="r4-module" aria-label="Сводка по учёту работ">
        <h3 className="text-title-medium">Итоги учёта работ</h3>
        {accepted}
      </section>
      <Costs />
    </div>
  </div> : <div className="r4-stack">
    <section className="r4-module" aria-label="Учёт работ">
      <WorkHeading />
      {availability}
      {accepted}
    </section>
    <Costs />
  </div>;
}

function StepTrack({ confirm }: { confirm: boolean }) {
  return <ol className="r4-steps text-label-large" aria-label="Шаги закрытия">
    <li aria-current={confirm ? undefined : 'step'}>1. Проверка</li>
    <li aria-current={confirm ? 'step' : undefined}>2. Подтверждение</li>
  </ol>;
}

function ClosingDialog({ variant, scenario, onExit, onClosed }: {
  variant: Variant; scenario: Scenario; onExit: () => void; onClosed: () => void;
}) {
  const [confirm, setConfirm] = useState(false);
  const [refreshed, setRefreshed] = useState(false);
  const id = useId();
  const stepAvailabilityId = useId();
  const report = preview(scenario, refreshed);
  const stepped = variant === 'c';
  const narrow = useMedia(NARROW);
  const closeLabel = narrow ? 'Закрыть' : 'Закрыть период';
  function close() {
    if (!report.canClose) return;
    if (scenario === 'changed') setRefreshed(true);
    else onClosed();
  }

  const closeActions = (back = false) => <>
    <ModalNote>{report.canClose ? 'Не разобраны: 2 счёта' : 'Есть запреты в учёте работ'}</ModalNote>
    <ModalExit>{back ? 'Назад' : 'Отмена'}</ModalExit>
    <ModalPrimary disabled={!report.canClose} aria-label="Закрыть период"
      aria-describedby={report.canClose ? undefined : id} onClick={close}>
      {closeLabel}
    </ModalPrimary>
  </>;
  const changeMessage = <ModalBanner tone="error">Перечень изменился. Период не закрыт. Проверьте новые числа.</ModalBanner>;
  const availability = <Availability report={report} id={id} />;

  return <Modal open onOpenChange={open => { if (!open) onExit(); }}
    title={narrow ? 'Закрытие периода' : 'Закрыть период · Компания'}
    nothingToLose wide={variant === 'b'}
    footer={stepped ? <>
      <ModalExit />
      <ModalPrimary disabled={!report.canClose} aria-describedby={report.canClose ? undefined : stepAvailabilityId}
        aria-label={report.canClose ? undefined : 'Закрыть период'} onClick={() => setConfirm(true)}>
        {report.canClose ? (narrow ? 'Далее' : 'К подтверждению') : closeLabel}
      </ModalPrimary>
    </> : closeActions()}>
    {!stepped && refreshed && changeMessage}
    <div className="r4-stack">
      <CompanyRule />
      {stepped && <StepTrack confirm={false} />}
      <Dates />
      <p className="text-body-small text-on-surface-variant">{companyScope}</p>
      {stepped ? <section className="r4-module" aria-label="Проверка учёта работ">
        <WorkHeading />
        <Availability report={report} id={stepAvailabilityId} />
        {!report.canClose && <Button variant="text" icon={<Icon icon={ArrowForward} size={18} />}
          onClick={() => setConfirm(true)}>Посмотреть сводку</Button>}
      </section> : <Snapshot report={report} refreshed={refreshed} wide={variant === 'b'} availability={availability} />}
    </div>
    {stepped && confirm && <WindowStep title={narrow ? 'Подтверждение' : 'Подтвердить закрытие · Компания'}
      onBack={() => setConfirm(false)}
      isDirty={false} footer={closeActions(true)}>
      <div className="r4-stack">
        <CompanyRule />
        <StepTrack confirm />
        {refreshed && <Banner tone="error">Перечень изменился. Период не закрыт. Проверьте новые числа.</Banner>}
        <div>
          <p className="text-title-small">{period} · включительно</p>
          <p className="text-body-small text-on-surface-variant">{companyScope}</p>
        </div>
        <Snapshot report={report} refreshed={refreshed} availability={report.canClose ? null : availability} />
      </div>
    </WindowStep>}
  </Modal>;
}

export function ClosingCanvas({ variant, scenario }: { variant: Variant; scenario: Scenario }) {
  const [open, setOpen] = useState(true);
  const [closed, setClosed] = useState(false);
  return <div className="r4-canvas">
    <header className="r4-appbar">
      <span className="text-title-medium">BHS.CRG</span>
      <span className="text-body-medium text-on-surface-variant">Управление учётными периодами</span>
    </header>
    <main className="r4-page r4-stack">
      <h1 className="text-headline-medium">Учётные периоды</h1>
      {closed && <Banner tone="success">Период закрыт по 31 октября 2026. Остались неразобранными 2 счёта.</Banner>}
      <section className="r4-period-card" aria-label="Период компании">
        <Icon icon={Apartment} size={24} className="text-primary" />
        <div>
          <h2 className="text-title-medium">Компания</h2>
          <p className="text-body-medium text-on-surface-variant">Закрыто по {closed ? '31 октября' : '30 сентября'} 2026</p>
        </div>
        <Button variant="filled" icon={<Icon icon={CalendarMonth} size={18} />}
          onClick={() => { setClosed(false); setOpen(true); }}>{closed ? 'Повторить сценарий' : 'Закрыть период'}</Button>
      </section>
      <section className="r4-period-card" aria-label="История закрытий">
        <Icon icon={Payments} size={24} className="text-on-surface-variant" />
        <div><h2 className="text-title-medium">История закрытий</h2><p className="text-body-medium text-on-surface-variant">1–30 сентября 2026</p></div>
        <StatusText tone="success">Закрыт</StatusText>
      </section>
    </main>
    {open && <ClosingDialog variant={variant} scenario={scenario} onExit={() => setOpen(false)}
      onClosed={() => { setClosed(true); setOpen(false); }} />}
  </div>;
}
