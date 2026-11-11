import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { LinkIcon, LinkOff, Person } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { Banner } from '@/shared/ui/Banner';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { useLinkAccount, useUnlinkAccount, type Employee } from '@/shared/api/employees';
import { accountView } from './employeeAccount';
import { AccountPickerDialog } from './AccountPickerDialog';

/**
 * Блок «Учётная запись» карточки сотрудника (макет Р1, общее для вариантов; B17). Строится только по
 * `account` от сервера. Кнопок «Связать» и «Отвязать» без `core.employees.edit` нет вовсе — и места
 * под них тоже: тот, кто только читает, видит состояние, а не отключённые кнопки.
 */
export function AccountBlock({ employee, canEdit }: { employee: Employee; canEdit: boolean }) {
  const view = accountView(employee.account);
  const [linkOpen, setLinkOpen] = useState(false);
  const [unlinkOpen, setUnlinkOpen] = useState(false);
  const link = useLinkAccount();
  const unlink = useUnlinkAccount();
  const toast = useSnackbar();
  // Архивного не связывают: сервер откажет «сотрудник в архиве», и кнопку, обещающую отказ, не рисуем (B19).
  // Поверх удалённой учётной записи связать можно без «отвязать» — сервер это разрешает.
  const canLink = canEdit && !employee.archived && employee.account.state !== 'linked';
  const canUnlink = canEdit && employee.account.state !== 'none';

  return (
    <section aria-labelledby="employee-account-h" className="space-y-3" data-testid="employee-account">
      <h3 id="employee-account-h" className="text-title-small text-on-surface">Учётная запись</h3>
      {view.deleted ? (
        <Banner tone="warning" className="rounded-md border-0 bg-surface-container-low"
          actions={[
            ...(canLink ? [{ label: 'Связать…', icon: LinkIcon, onClick: () => setLinkOpen(true) }] : []),
            ...(canUnlink ? [{ label: 'Отвязать', onClick: () => setUnlinkOpen(true) }] : []),
          ]}>
          {view.headline}. {view.supporting}
        </Banner>
      ) : (
        <div className="flex flex-wrap items-center gap-4">
          <span className={`shrink-0 grid place-items-center size-10 rounded-full ${employee.account.state === 'linked'
            ? 'bg-secondary-container text-on-secondary-container' : 'bg-surface-container-high text-on-surface-variant'}`}>
            <Icon icon={employee.account.state === 'linked' ? Person : LinkOff} size={24} />
          </span>
          <span className="flex-1 min-w-0 flex flex-col">
            <span className="text-body-large text-on-surface break-words" data-testid="employee-account-state">{view.headline}</span>
            {view.supporting && <span className="text-body-medium text-on-surface-variant break-words">{view.supporting}</span>}
          </span>
          {canLink && (
            <Button variant="tonal" icon={<Icon icon={LinkIcon} size={18} />} onClick={() => setLinkOpen(true)}>Связать…</Button>
          )}
          {canUnlink && <Button variant="text" onClick={() => setUnlinkOpen(true)}>Отвязать</Button>}
        </div>
      )}
      {view.dismissed && (
        // Совет «отвяжите» — тому, кто может отвязать; остальным пометка только сообщает.
        <Banner tone="info" className="rounded-md border-0 bg-surface-container-low">
          {view.dismissed}{canUnlink && ' Отвяжите учётную запись, если он больше не работает.'}
        </Banner>
      )}

      {linkOpen && (
        <AccountPickerDialog employee={{ id: employee.id, name: employee.displayName }} confirmLabel="Связать"
          onClose={() => setLinkOpen(false)}
          onConfirm={async c => {
            await link.mutateAsync({ id: employee.id, userId: c.userId, version: employee.version });
            toast.success(`«${employee.displayName}» связан с учётной записью «${c.name}».`);
          }} />
      )}
      <ConfirmDialog open={unlinkOpen} onOpenChange={setUnlinkOpen}
        title="Отвязать учётную запись?" confirmLabel="Отвязать" confirmDanger
        description={`${employee.account.name ?? 'Учётная запись'} больше не сможет войти и подать отчёт как ${employee.displayName}. ` +
          'Поданные отчёты остаются за сотрудником. Действие пишется в журнал действий.'}
        onConfirm={async () => {
          await unlink.mutateAsync({ id: employee.id, version: employee.version });
          toast.success(`Учётная запись отвязана от «${employee.displayName}».`);
        }} />
    </section>
  );
}
