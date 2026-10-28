import { useState } from 'react';
import { CollapsibleSection } from './CollapsibleSection';
import { Button } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useToast } from '@/shared/ui/Toast';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useAccountingCurrency, useSaveAccountingCurrency } from '@/shared/api/accountingCurrency';
import { currencyTitle } from './companySettings';
import { CompanyTimeZone } from './CompanyTimeZone';

/**
 * Настройки компании (ТЗ CORE-25.3): валюта учёта (ТЗ COST-7.2, issue #1248) и часовой пояс —
 * он своим блоком, `CompanyTimeZone`: у него свой запрос и своё сохранение.
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
  const [zoneDirty, setZoneDirty] = useState(false);

  /** Сменить. Отказ НЕ ловим: диалог показывает причину сам и остаётся открытым — она длинная
   *  («…счетов: 1440, накладных: 39…»), в исчезающем уведомлении её не прочитать. */
  async function submit() {
    if (!next) return;
    await save.mutateAsync(next);
    toast.success('Валюта учёта изменена.');
    setPicked(null);
    setConfirming(false);
  }

  /**
   * Убрать из базы строку, которой нет в списке: записать действующую валюту её кодом. Валюту это
   * не меняет, поэтому доступно и при документах, и без подтверждения. Обычным «Сохранить» сюда не
   * попасть — выбор совпадает с действующим, сохранять «нечего» (ревью PR #1306).
   */
  async function cleanUp() {
    if (!data) return;
    try {
      await save.mutateAsync(data.currency);
      toast.success('Сохранённое значение исправлено.');
    } catch (e) {
      toast.apiError(e, 'Не удалось исправить сохранённое значение.');
    }
  }

  return (
    <CollapsibleSection title="Компания" storageKey="company" defaultOpen={false}
      right={dirty || zoneDirty ? <span className="text-xs text-warning normal-case">не сохранено</span> : undefined}>
      {isError && (
        <div className="flex items-center gap-3">
          <span className="text-sm text-error">Настройки компании не загрузились.</span>
          <Button variant="outlined" size="sm" onClick={() => void refetch()}>Повторить</Button>
        </div>
      )}

      {/* Пока ответа нет, поля нет: нарисовать рубль вместо неполученной валюты значило бы
          показать выбранным то, чего не выбирали. */}
      {data && (data.locked ? (
        <div data-testid="currency-locked">
          <p className="text-xs text-fg3">Валюта учёта</p>
          <p className="text-sm text-on-surface mt-0.5">{currencyTitle(data, data.currency)}</p>
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
        <div data-testid="currency-stale">
          <p className="text-xs text-warning max-w-xl">
            В базе сохранено «{data.stale}» — такой валюты в списке нет, действует{' '}
            {currencyTitle(data, data.currency)}.
          </p>
          <Button variant="text" size="sm" className="mt-1 -ml-2" onClick={() => void cleanUp()}
            disabled={save.isPending}>
            Записать «{currencyTitle(data, data.currency)}»
          </Button>
        </div>
      )}

      <div className="pt-4 mt-1 border-t border-outline-variant">
        <CompanyTimeZone onDirty={setZoneDirty} />
      </div>

      <ConfirmDialog
        open={confirming}
        // Закрыли после отказа — выбор сбрасываем: поле к этому времени пришло с сервера запертым.
        onOpenChange={o => { if (!o) { setConfirming(false); if (save.isError) setPicked(null); } }}
        title="Сменить валюту учёта?"
        description={
          <div className="space-y-3">
            <p className="font-medium text-on-surface">
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
