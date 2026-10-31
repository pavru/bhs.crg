import { useEffect, useState } from 'react';
import { Button } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useToast } from '@/shared/ui/Toast';
import { useCompanyTimeZone, useSaveCompanyTimeZone } from '@/shared/api/companyTimeZone';
import { SERVER_ZONE, companyNow, zoneChoices, zoneNotice } from './companySettings';

/**
 * Часовой пояс компании (ТЗ CORE-29, issue #1148). До этого экрана пояс задавался только запросом к
 * API, а пока он не задан, действует пояс сервера — в поставке это UTC: новые сутки наступали в
 * 03:00 по Москве, и счёт со сроком «вчера» до этого часа просроченным не считался. Отказа при этом
 * нет нигде, ответ выглядит правдоподобно — поэтому невыбранный пояс экран называет словами.
 *
 * Подтверждения у смены нет: пояс ничего не пересчитывает и меняется обратно.
 */
export function CompanyTimeZone({ onDirty }: {
  /** Есть ли несохранённый выбор — раздел пишет об этом в своей шапке, как о валюте. */
  onDirty?: (dirty: boolean) => void;
}) {
  const { data, isError, refetch } = useCompanyTimeZone();
  const save = useSaveCompanyTimeZone();
  const toast = useToast();
  // Правка, а не копия ответа: эффектом не синхронизируем — выбор затирался бы ответом сервера.
  const [picked, setPicked] = useState<string | null>(null);

  const stored = data?.timeZoneId ?? SERVER_ZONE;
  const next = picked ?? stored;
  const dirty = !!data && next !== stored;
  const notice = data ? zoneNotice(data) : null;
  // Свернутый раздел блок размонтирует, и выбор пропадёт: пометка в шапке — единственное, что об
  // этом предупреждает. Размонтирование пометку снимает — вместе с выбором.
  useEffect(() => { onDirty?.(dirty); return () => onDirty?.(false); }, [dirty, onDirty]);

  async function submit() {
    try {
      await save.mutateAsync(next === SERVER_ZONE ? null : next);
      toast.success(next === SERVER_ZONE ? 'Выбор пояса снят: действует пояс сервера.' : 'Часовой пояс компании изменён.');
      setPicked(null);
    } catch (e) {
      toast.apiError(e, 'Не удалось сохранить часовой пояс.');
    }
  }

  if (isError) return (
    <div className="flex items-center gap-3">
      <span className="text-body-medium text-error">Часовой пояс компании не загрузился.</span>
      <Button variant="outlined" onClick={() => void refetch()}>Повторить</Button>
    </div>
  );
  // Пока ответа нет, поля нет: нарисовать «Москву» вместо неполученного пояса значило бы показать
  // выбранным то, чего не выбирали.
  if (!data) return null;

  return (
    <div data-testid="company-time-zone" className="space-y-2">
      <Select label="Часовой пояс компании" containerClassName="max-w-sm" value={next}
        onValueChange={setPicked} disabled={save.isPending}>
        {zoneChoices(data).map(z => <SelectItem key={z.value} value={z.value}>{z.label}</SelectItem>)}
      </Select>
      {notice && <p role="status" className="text-body-small text-warning max-w-xl">{notice}</p>}
      <p className="text-body-small text-on-surface-variant max-w-xl">
        Сейчас у компании: <span data-testid="company-now" className="text-on-surface">{companyNow(data.effectiveNow)}</span>. По этому
        поясу система считает сутки: от него зависят «Осталось дней» и «Просрочен» у счетов, то,
        какое число считается сегодняшним в учёте, и время резервной копии.
      </p>
      <div className="flex items-center gap-3">
        <Button variant="filled" onClick={() => void submit()} disabled={!dirty} loading={save.isPending}>
          Сохранить пояс
        </Button>
        {dirty && (
          <Button variant="text" onClick={() => setPicked(null)} disabled={save.isPending}>Отменить</Button>
        )}
      </div>
    </div>
  );
}
