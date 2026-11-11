import { useMemo, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CheckCircle, ErrorIcon, Info } from '@/shared/ui/icons';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Checkbox } from '@/shared/ui/Checkbox';
import { ListItem } from '@/shared/ui/ListItem';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { apiError } from '@/shared/utils/apiError';
import {
  useAccountMatches, useLinkAccounts, type AccountMatch, type LinkBatchResult, type LinkRowResult,
} from '@/shared/api/employees';
import { batchSummary, matchReasonText, rowOutcomeText } from './employeeAccount';
import { AccountPickerDialog } from './AccountPickerDialog';

type Choice = { userId: string; name: string; login: string };

/**
 * Режим «Сопоставить» (макет Р1-В): во весь экран — сотрудники без связи и предложенная СЕРВЕРОМ учётная
 * запись того же ФИО. Отмечены только однозначные; у строки без предложения — причина («нет совпадения»,
 * «несколько совпадений») и тот же поиск кандидатов, что в карточке. «Связать отмеченные» — одна пачка,
 * итог — по каждой строке: удачная не откатывается из-за соседней, у отказа — слова сервера.
 */
export function MatchAccountsDialog({ onClose }: { onClose: () => void }) {
  const { data: rows = [], isLoading, isError, refetch } = useAccountMatches(true);
  const batch = useLinkAccounts();
  // Выбор человека поверх предложения сервера: строка → учётная запись.
  const [picked, setPicked] = useState<Record<string, Choice>>({});
  // Снятые и поставленные руками флажки; по умолчанию отмечено то, у чего есть выбор.
  const [toggled, setToggled] = useState<Record<string, boolean>>({});
  const [result, setResult] = useState<LinkBatchResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [picking, setPicking] = useState<AccountMatch | null>(null);

  const choiceOf = (r: AccountMatch): Choice | null => picked[r.employee.id] ?? r.suggested;
  const isChecked = (r: AccountMatch) => !!choiceOf(r) && (toggled[r.employee.id] ?? true);
  const checked = rows.filter(isChecked);
  const outcomes = useMemo(() => new Map((result?.results ?? []).map(r => [r.employeeId, r])), [result]);
  const suggestedCount = rows.filter(r => r.suggested).length;

  async function linkChecked() {
    setError(null);
    try {
      const done = await batch.mutateAsync(checked.map(r => ({
        employeeId: r.employee.id, userId: choiceOf(r)!.userId, version: r.employee.version,
      })));
      setResult(done);
      setPicked({}); setToggled({});
      // Связанные уходят из списка, а у несвязанных — свежие версии: «изменили» лечится повтором.
      await refetch();
    } catch (e) { setError(apiError(e, 'Пачку отправить не удалось — не связано ничего.')); }
  }

  return (
    <Modal open fullScreen onOpenChange={o => { if (!o) onClose(); }} title="Сопоставление с учётными записями"
      busy={batch.isPending} nothingToLose
      footer={<>
        <ModalExit>{result ? 'Готово' : 'Отмена'}</ModalExit>
        <ModalPrimary onClick={() => void linkChecked()} disabled={checked.length === 0} loading={batch.isPending}>
          {`Связать отмеченные · ${checked.length}`}
        </ModalPrimary>
      </>}>
      <div className="space-y-4" data-testid="match-dialog">
        {result && <ResultSummary result={result} />}
        {error && <Banner tone="error" className="rounded-md border-0">{error}</Banner>}
        {isLoading ? (
          <p className="text-body-medium text-on-surface-variant">Ищем совпадения…</p>
        ) : isError ? (
          <p className="text-body-medium text-on-surface-variant">Список сотрудников без учётной записи получить не удалось.</p>
        ) : rows.length === 0 ? (
          <p className="text-body-medium text-on-surface-variant">Все сотрудники, кроме архивных и уволенных, уже связаны.</p>
        ) : (
          <>
            <Banner tone="info" icon={Info} className="rounded-md border-0 bg-surface-container-low">
              {`Сотрудников без учётной записи: ${rows.length}. Для ${suggestedCount} найдено совпадение ФИО — проверьте перед связыванием. ` +
                'Отмечены только однозначные.'}
            </Banner>
            <div role="list" aria-label="Сотрудники без учётной записи" className="-mx-4">
              {rows.map(r => {
                const choice = choiceOf(r);
                const outcome = outcomes.get(r.employee.id);
                return (
                  <div role="listitem" key={r.employee.id}>
                    <ListItem passive checked={isChecked(r)} headline={r.employee.name}
                      leadingAction={
                        <Checkbox checked={isChecked(r)} disabled={!choice} aria-label={`Связать ${r.employee.name}`}
                          onChange={v => setToggled(t => ({ ...t, [r.employee.id]: v }))} />
                      }
                      supporting={outcome ? <Outcome row={outcome} /> : choice
                        ? `${choice.name} · ${choice.login}${picked[r.employee.id] ? '' : ' · совпало ФИО'}`
                        : matchReasonText(r.reason) ?? ''}
                      action={
                        <Button variant="text" onClick={() => setPicking(r)}>{choice ? 'Другая…' : 'Выбрать…'}</Button>
                      } />
                  </div>
                );
              })}
            </div>
          </>
        )}
      </div>
      {picking && (
        <AccountPickerDialog employee={{ id: picking.employee.id, name: picking.employee.name }} confirmLabel="Выбрать"
          onClose={() => setPicking(null)}
          onConfirm={c => {
            setPicked(p => ({ ...p, [picking.employee.id]: { userId: c.userId, name: c.name, login: c.login } }));
            setToggled(t => ({ ...t, [picking.employee.id]: true }));
          }} />
      )}
    </Modal>
  );
}

/** Итог пачки: «Связано N из M», остальное — у строк (они остаются в списке с причиной). */
function ResultSummary({ result }: { result: LinkBatchResult }) {
  const failed = result.total - result.linked;
  return (
    <Banner tone={failed ? 'warning' : 'success'} className="rounded-md border-0 bg-surface-container-low" data-testid="match-summary">
      {`${batchSummary(result)}. `}
      {failed ? 'Причина у каждой несвязанной строки; связи остальных сделаны и записаны в журнал.' : 'Связи записаны в журнал.'}
    </Banner>
  );
}

function Outcome({ row }: { row: LinkRowResult }) {
  const ok = row.outcome === 'linked';
  return (
    <span className={`inline-flex items-center gap-1 ${ok ? 'text-success' : 'text-error'}`}>
      <Icon icon={ok ? CheckCircle : ErrorIcon} size={18} className="shrink-0" />
      {rowOutcomeText(row)}
    </span>
  );
}
