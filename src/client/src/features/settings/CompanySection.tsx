import { useState } from 'react';
import { CollapsibleSection } from './CollapsibleSection';
import { Button } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useToast } from '@/shared/ui/Toast';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useAccountingCurrency, useSaveAccountingCurrency } from '@/shared/api/accountingCurrency';
import { currencyTitle } from './companySettings';

/**
 * Настройки компании (ТЗ CORE-25.3). Пока одна — валюта учёта (ТЗ COST-7.2, issue #1248); часовой
 * пояс компании встанет сюда же своей задачей.
 *
 * Валюту меняют, пока документов с суммами нет: суммы при смене не пересчитываются. Когда
 * документы есть, выбора на экране НЕТ вовсе — валюта названа текстом, причина под ней. Поле с
 * кнопкой, которая заведомо откажет, было бы приглашением нажать.
 */
export function CompanySection() {
  const { data, isError, refetch } = useAccountingCurrency();
  const save = useSaveAccountingCurrency();
  const toast = useToast();
  // Правка, а не копия ответа: эффектом не синхронизируем — выбор затирался бы ответом сервера.
  const [picked, setPicked] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);

  const next = picked ?? data?.currency;
  const dirty = !!data && next !== data.currency;

  async function submit() {
    if (!next) return;
    try {
      await save.mutateAsync(next);
      toast.success('Валюта учёта изменена.');
    } catch (e) {
      toast.apiError(e, 'Не удалось сменить валюту учёта.');
    } finally {
      // И при отказе: набранное больше не годится — поле придёт с сервера запертым.
      setPicked(null);
      setConfirming(false);
    }
  }

  return (
    <CollapsibleSection title="Компания" storageKey="company" defaultOpen={false}
      right={dirty ? <span className="text-xs text-warning normal-case">не сохранено</span> : undefined}>
      {isError && (
        <div className="flex items-center gap-3">
          <span className="text-sm text-danger">Настройки компании не загрузились.</span>
          <Button variant="outlined" size="sm" onClick={() => void refetch()}>Повторить</Button>
        </div>
      )}

      {/* Пока ответа нет, поля нет: нарисовать рубль вместо неполученной валюты значило бы
          показать выбранным то, чего не выбирали. */}
      {data && (data.locked ? (
        <div data-testid="currency-locked">
          <p className="text-xs text-fg3">Валюта учёта</p>
          <p className="text-sm text-fg1 mt-0.5">{currencyTitle(data, data.currency)}</p>
          <p className="text-xs text-fg3 mt-1.5 max-w-xl">{data.reason}</p>
        </div>
      ) : (
        <>
          <div>
            <Select label="Валюта учёта" containerClassName="max-w-xs" value={next}
              onValueChange={setPicked} disabled={save.isPending}>
              {data.currencies.map(c => (
                <SelectItem key={c.code} value={c.code}>{c.title} ({c.code})</SelectItem>
              ))}
            </Select>
            <p className="text-xs text-fg3 mt-1.5 max-w-xl">
              В этой валюте ведутся все суммы: счета, накладные, затраты по стройкам. Сменить её
              можно, пока в системе нет ни одного счёта и накладной: суммы при смене не
              пересчитываются.
            </p>
          </div>
          <div className="flex items-center gap-3">
            <Button variant="filled" onClick={() => setConfirming(true)} disabled={!dirty || save.isPending}>
              Сохранить
            </Button>
            {dirty && (
              <Button variant="text" onClick={() => setPicked(null)} disabled={save.isPending}>Отменить</Button>
            )}
          </div>
        </>
      ))}

      {data?.stale && (
        <p className="text-xs text-warning max-w-xl">
          В базе сохранено «{data.stale}» — такой валюты в списке нет, действует{' '}
          {currencyTitle(data, data.currency)}.
        </p>
      )}

      <ConfirmDialog
        open={confirming}
        onOpenChange={o => { if (!o) setConfirming(false); }}
        title="Сменить валюту учёта?"
        description={
          <div className="space-y-3">
            <p className="font-medium text-fg1">
              {data && currencyTitle(data, data.currency)} → {data && next && currencyTitle(data, next)}
            </p>
            <p>
              Все суммы будут показаны в новой валюте. После первого счёта или накладной сменить
              валюту будет нельзя.
            </p>
            <p>Смена будет записана в журнал действий.</p>
          </div>
        }
        confirmLabel="Сменить"
        errorTitle="Валюта не изменена"
        onConfirm={submit}
      />
    </CollapsibleSection>
  );
}
