import { useEffect, useState } from 'react';
import type { Offer, OfferMarkupsInput } from '@/shared/api/planningOfferTypes';
import { formatInput } from '@/shared/format/format';
import { useLocale } from '@/shared/hooks/useLocale';
import { parseNumber } from '@/shared/utils/parseNumber';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DataGrid } from '@/shared/ui/DataGrid';
import { EditGrid, type EditGridColumn } from '@/shared/ui/EditGrid';
import { LeaveGuard } from '@/shared/ui/LeaveGuard';
import { NumberInput } from '@/shared/ui/NumberInput';
import { NumberField } from './OfferHeaderFields';
import { MARKUP_LABELS, markupText } from './offerFormat';
import { OfferWriteNotice } from './OfferWriteNotice';
import { useOfferWriteForm, type ReloadOffer } from './useOfferWriteForm';

interface Scope {
  key: string;
  id: string;
  name: string;
  chapter?: string;
  kind: 'section' | 'line';
  own: number | null;
  effective: string;
}

const generalKey = 'offer';

function scopeLabel(row: Scope) {
  return row.chapter ? `${row.name}, глава ${row.chapter}` : row.name;
}

function scopesOf(offer: Offer): Scope[] {
  return [
    ...offer.sections.map((row): Scope => ({ key: JSON.stringify(['section', row.code, row.name]), id: row.code,
      name: row.name ? `${row.code} · ${row.name}` : row.code, kind: 'section', own: row.markup, effective: '' })),
    ...offer.lines.map((row): Scope => ({ key: `line:${row.id}`, id: row.id,
      name: row.number ? `${row.number} · ${row.name}` : row.name, kind: 'line', own: row.ownMarkup,
      chapter: [row.lsrSection?.code, row.lsrSection?.name].filter(Boolean).join(' · '),
      effective: `${markupText(row.markup.value)} · ${MARKUP_LABELS[row.markup.level]}` })),
  ];
}

type Change = { label: string; text: string };

export function OfferMarkups({ offer, mayEdit, reload, onDirty }: {
  offer: Offer; mayEdit: boolean; reload: ReloadOffer; onDirty: (dirty: boolean) => void;
}) {
  const [locale] = useLocale();
  const [edits, setEdits] = useState<Record<string, Change>>({});
  const form = useOfferWriteForm(offer, reload);
  const scopes = scopesOf(form.seen);
  const editable = mayEdit && offer.status === 'draft' && form.seen.status === 'draft';
  const dirty = Object.keys(edits).length > 0;
  useEffect(() => { onDirty(dirty); }, [dirty, onDirty]);

  const keys = new Set([generalKey, ...scopes.map(row => row.key)]);
  const detached = Object.entries(edits).filter(([key]) => !keys.has(key));
  const invalidText = Object.values(edits).some(({ text }) => text.trim() && parseNumber(text, locale) === null);
  const general = edits[generalKey]?.text ?? formatInput(form.seen.markup);
  const markup = parseNumber(general, locale);
  const invalid = markup === null || invalidText || detached.length > 0;
  const disabled = form.busy || form.refusal?.kind === 'forbidden';

  function change(key: string, label: string, text: string) {
    form.capture();
    setEdits(previous => ({ ...previous, [key]: { label, text } }));
  }

  function discard() {
    setEdits({});
    form.reset();
  }

  async function save() {
    if (!editable || !dirty || invalid || form.blocked || markup === null) return false;
    const input: OfferMarkupsInput = { markup, sections: [], lines: [] };
    for (const row of scopes) {
      const edit = edits[row.key];
      const value = edit ? parseNumber(edit.text, locale) : row.own;
      if (value === null) continue;
      if (row.kind === 'section') input.sections.push({ code: row.id, markup: value });
      else input.lines.push({ lineId: row.id, markup: value });
    }
    if (!(await form.apply({ type: 'markups', input }))) return false;
    setEdits({});
    return true;
  }

  const columns: EditGridColumn<Scope>[] = [
    { key: 'position', label: 'Позиция', rowHeader: true, cell: row => <>{row.name}
      <span className="block text-body-small text-on-surface-variant">{row.kind === 'section' ? 'Глава' :
        row.chapter ? `Глава ${row.chapter}` : 'Строка'}</span></> },
    { key: 'own', label: 'Своя наценка, %', width: 'w-32', align: 'right', cell: row => editable
      ? <NumberInput value={edits[row.key]?.text ?? formatInput(row.own)}
        label={`Своя наценка: ${scopeLabel(row)}`} onChange={text => change(row.key, scopeLabel(row), text)} disabled={disabled} />
      : markupText(row.own) },
    { key: 'effective', label: 'Действующая наценка', cell: row => row.effective || '—' },
  ];

  return <section className="grid min-w-0 grid-cols-1 gap-3" aria-label="Наценки КП">
    <LeaveGuard active={dirty} saving={form.busy} save={save}
      saveBlocked={!editable || invalid || form.blocked ? 'Сначала проверьте наценки во вкладке КП.' : undefined} />
    {editable ? <NumberField label="Наценка КП" value={general}
      onChange={text => change(generalKey, 'Наценка КП', text)} disabled={disabled} />
      : <p className="text-body-medium">Наценка КП · {markupText(form.seen.markup)}</p>}
    {editable ? <EditGrid columns={columns} rows={scopes} rowKey={row => row.key} label="Наценки глав и строк" />
      : <DataGrid<Scope> columns={columns.map(({ cell, ...column }) => ({ ...column, cell: row => cell(row, 0) }))}
        rows={scopes} rowKey={row => row.key} density="compact" />}
    {editable && <p className="text-body-small text-on-surface-variant">Пустое поле наследует уровень выше. Действующие наценки показаны по сохранённому КП.</p>}
    {invalidText && <Banner tone="error">Проверьте формат введённых наценок.</Banner>}
    {detached.length > 0 && <div className="space-y-2"><Banner tone="warning" actions={[{ label: 'Снять правки к этим позициям', onClick: () => {
      setEdits(previous => Object.fromEntries(Object.entries(previous).filter(([key]) => keys.has(key))));
    } }]}>После перечитывания этих позиций больше нет в КП.</Banner>
      <ul className="list-disc ps-5 text-body-medium">{detached.map(([key, edit]) => <li key={key}>{edit.label} · {edit.text || 'наценка снята'}</li>)}</ul>
    </div>}
    {!editable && dirty && <div className="space-y-2"><Banner tone="warning">Черновик формы сохранён на экране. Текущее КП доступно только для чтения.</Banner>
      <ul className="list-disc ps-5 text-body-medium">{Object.entries(edits).map(([key, edit]) => <li key={key}>{edit.label} · {edit.text || 'наценка снята'}</li>)}</ul>
    </div>}
    <OfferWriteNotice form={form} hasInput={dirty} />
    {dirty && <div className="flex flex-wrap gap-2">
      {editable && <Button variant="filled" disabled={invalid || form.busy || form.blocked} onClick={() => void save()}>
        {form.busy ? 'Сохраняем…' : 'Сохранить наценки'}
      </Button>}
      <Button variant="text" disabled={form.busy} onClick={discard}>Отменить правки</Button>
    </div>}
  </section>;
}
