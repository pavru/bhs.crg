import { useState } from 'react';
import { formatNumber } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Modal, ModalAction, ModalButtons, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import { missingUnits, option, sections, siteName, sites, suggest, works, type Material, type Scenario, type Scope, type Site, type Variant } from './sample';

export function EstimateWizard({ variant, scenario, narrow, onClose, onSave }: {
  variant: Variant; scenario: Scenario; narrow: boolean; onClose: () => void;
  onSave: (site: Site, rows: Material[]) => void;
}) {
  const [site, setSite] = useState<Site>('park');
  const [scope, setScope] = useState<Scope>('items');
  const [section, setSection] = useState<string>(sections[0].value);
  const [picked, setPicked] = useState<string[]>(['feed', 'light']);
  const [step, setStep] = useState(0);
  const [rows, setRows] = useState<Material[]>();
  const [blocked, setBlocked] = useState(false);
  const reviewing = rows !== undefined || blocked;
  const readyToCalculate = scope === 'section' || picked.length > 0;
  const validRows = !!rows?.length && rows.every(row => row.unit.trim() && Number.isFinite(row.quantity) && row.quantity >= 0);
  const sectionName = sections.find(item => item.value === section)?.label;
  const source = scope === 'section' ? `Раздел стройки «${sectionName}»`
    : picked.length === 1 ? '1 позиция перечня' : `${picked.length} позиции перечня`;

  function clear() { setRows(undefined); setBlocked(false); }
  function calculate() {
    if (!readyToCalculate) return;
    if (scenario === 'hidden') setBlocked(true);
    else {
      const suggested = suggest(scope, section, picked);
      const includesFeed = scope === 'section' ? section === 'power' : picked.includes('feed');
      setRows(scenario === 'unknown-units' && includesFeed ? [...suggested, ...missingUnits] : suggested);
    }
  }
  function revise(id: string, patch: Partial<Material>) {
    setRows(previous => previous?.map(row => row.id === id ? { ...row, ...patch } : row));
  }
  function back() {
    if (reviewing) clear();
    else setStep(0);
  }
  function save() {
    if (rows && validRows) onSave(site, rows);
  }

  const construction = <Select label="Стройка" value={site} onValueChange={value => { setSite(option(sites, value, site)); clear(); }}>
    {sites.map(item => <SelectItem value={item.value} key={item.value}>{item.label}</SelectItem>)}
  </Select>;

  const coverage = <div className="grid gap-3">
    <Select label="Охват сметы" value={scope} onValueChange={value => {
      if (value === 'items' || value === 'section') { setScope(value); clear(); }
    }}>
      <SelectItem value="items">Позиции перечня</SelectItem>
      <SelectItem value="section">Раздел стройки</SelectItem>
    </Select>
    {scope === 'section' ? <Select label="Раздел стройки" value={section} onValueChange={value => { setSection(value); clear(); }}>
      {sections.map(item => <SelectItem value={item.value} key={item.value}>{item.label}</SelectItem>)}
    </Select> : <div className="grid gap-2">
      {works.map(item => <Checkbox key={item.value} label={item.label} description={item.section}
        checked={picked.includes(item.value)} onChange={checked => {
          setPicked(previous => checked ? [...previous, item.value] : previous.filter(id => id !== item.value));
          clear();
        }} />)}
      {!readyToCalculate && <p className="text-body-small text-on-surface-variant">Выберите хотя бы одну позицию.</p>}
    </div>}
  </div>;

  const answer = blocked ? <Banner tone="info">Материалы сметы скрыты. Для расчёта нужно право
    «Читать материалы сметы» (<code>plan.estimate.materials</code>).</Banner> : rows && <div className="grid min-w-0 gap-3">
    <Banner tone="info">Отпуск взят по всей стройке. При ограниченном выборе проверьте остаток вручную.
      Другие заявки не резервируют количество. Без нового отпуска повторный расчёт может дать тот же результат.</Banner>
    <p className="text-body-small text-on-surface-variant">Строки отпуска без номенклатуры: 1</p>
    {rows.map(row => <Card key={row.id} title={row.name}
      actions={<Button variant="text" aria-label={`Убрать ${row.name}`}
        onClick={() => setRows(previous => previous?.filter(item => item.id !== row.id))}>Убрать строку</Button>}>
      <div className="grid gap-2">
        <p className="text-body-medium text-on-surface-variant">
          По смете {formatNumber(row.estimate)}{row.estimateUnit ? ` ${row.estimateUnit}` : ' (без единицы)'}
          {' · '}Отпущено на стройку {row.issued === null ? 'неизвестно в этой единице' : `${formatNumber(row.issued)} ${row.estimateUnit}`}
        </p>
        {row.exceeds && <p className="text-body-small text-warning">Отпущено больше сметы. Предложено 0.</p>}
        {row.mismatch && <p className="text-body-small text-warning">{row.issue} Проверьте вручную.</p>}
        <div className="grid gap-2 sm:grid-cols-2">
          <TextField label="Количество в заявке" type="number" min="0" step="any" value={row.quantity}
            onChange={event => revise(row.id, { quantity: Number(event.target.value) })} />
          <TextField label="Единица в заявке" value={row.unit} onChange={event => revise(row.id, { unit: event.target.value })} />
        </div>
      </div>
    </Card>)}
    {rows.length === 0 && <p className="text-body-medium">Добавьте строки, пересчитав предложение.</p>}
  </div>;

  const chooseSite = variant === 'c' && step === 0;
  const steps = variant === 'c' ? ['Стройка', 'Охват сметы', 'Проверка строк'] : ['Выбор материалов', 'Проверка строк'];
  const current = variant === 'c' ? (reviewing ? 2 : step) : (reviewing ? 1 : 0);
  const primary = reviewing ? 'Сохранить черновик' : chooseSite ? 'Далее' : 'Рассчитать предложение';
  return <Modal open title="Новая заявка из сметы" wide fullScreen={narrow} onOpenChange={open => !open && onClose()}
    footer={<ModalButtons><ModalExit>Отмена</ModalExit>
      {(reviewing || (variant === 'c' && step > 0)) && <ModalAction onClick={back}>{variant === 'b' ? 'Пересчитать' : 'Назад'}</ModalAction>}
      <ModalPrimary disabled={reviewing ? !validRows : !chooseSite && !readyToCalculate}
        onClick={() => { if (reviewing) save(); else if (chooseSite) setStep(1); else calculate(); }}>{primary}</ModalPrimary>
    </ModalButtons>}>
    {variant === 'b' ? <div className="@container/estimate grid gap-4">
      <div className="grid min-w-0 gap-4 @2xl/estimate:grid-cols-[minmax(0,1fr)_minmax(0,1.6fr)]">
        <div className="grid content-start gap-3">{construction}{coverage}</div>
        <section className="grid min-w-0 content-start gap-2" aria-label="Предложение">
          <h3 className="text-title-medium">Предложение</h3>
          {answer ?? <p className="text-body-medium text-on-surface-variant">Выберите охват и рассчитайте количество.</p>}
        </section>
      </div>
    </div> : <div className="grid gap-4">
      <ol aria-label="Шаги мастера" className="flex flex-wrap gap-x-4 gap-y-1 border-b border-outline-variant pb-3 text-body-small">
        {steps.map((label, index) => <li key={label} aria-current={index === current ? 'step' : undefined}
          className={index === current ? 'font-medium text-primary' : 'text-on-surface-variant'}>{label}</li>)}
      </ol>
      {reviewing ? <><p className="text-body-medium">{siteName(site)} · {source}</p>{answer}</>
        : chooseSite ? construction
        : <>{variant === 'a' ? construction : <p className="text-body-medium">{siteName(site)}</p>}{coverage}</>}
    </div>}
  </Modal>;
}
