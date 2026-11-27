import { formatNumber } from '@/shared/format/format';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { StatusText } from '@/shared/ui/StatusText';
import { TextField } from '@/shared/ui/TextField';
import { materialName, type LineDraft } from './purchaseLineDraft';

export function PurchaseLineFields({ draft, names, busy = false, onChange }: {
  draft: LineDraft[];
  names: Record<string, string | null> | null;
  busy?: boolean;
  onChange: (next: LineDraft[]) => void;
}) {
  const patch = (key: number, changes: Partial<LineDraft>) => onChange(draft.map(line => line.key === key ? { ...line, ...changes } : line));
  if (!draft.length) return <p className="text-body-medium">Для заявки нужна хотя бы одна строка.</p>;

  return <div className="@container/lines grid min-w-0 gap-3">
    {draft.map(({ row, key, amount, unit, note }) => {
      const title = materialName(names, row.nomenclatureId);
      return <Card key={key} title={title}
        actions={<Button variant="text" disabled={busy} aria-label={`Убрать строку: ${title}`}
          onClick={() => onChange(draft.filter(item => item.key !== key))}>Убрать строку</Button>}>
        <div className="grid gap-2">
          {'estimateQuantity' in row && <>
            <p className="text-body-medium text-on-surface-variant">
              По смете {formatNumber(row.estimateQuantity, 6)} {row.unit ?? '(без единицы)'}
              {' · '}Отпущено на стройку {row.compatibleIssuedQuantity === null ? 'неизвестно в этой единице'
                : `${formatNumber(row.compatibleIssuedQuantity, 6)} ${row.unit ?? ''}`}
            </p>
            {row.issuedExceeds && <StatusText tone="warning">Отпущено больше сметы. Предложено 0.</StatusText>}
            {row.unitMismatch && <StatusText tone="warning">{row.unit ? 'Несовместимые единицы. Проверьте количество.'
              : 'Единица в смете отсутствует. Укажите её вручную.'}</StatusText>}
          </>}
          <div className="grid gap-2 @sm/lines:grid-cols-2">
            <TextField label="Количество в заявке" inputMode="decimal" value={amount} required disabled={busy}
              onChange={event => patch(key, { amount: event.target.value })} />
            <TextField label="Единица в заявке" maxLength={50} value={unit} required disabled={busy}
              onChange={event => patch(key, { unit: event.target.value })} />
          </div>
          <TextField label="Примечание" maxLength={2000} value={note} disabled={busy}
            onChange={event => patch(key, { note: event.target.value })} />
        </div>
      </Card>;
    })}
  </div>;
}
