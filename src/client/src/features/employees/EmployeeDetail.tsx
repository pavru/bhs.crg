import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Archive, Edit, Unarchive } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { Modal } from '@/shared/ui/Modal';
import { DetailTitle } from '@/shared/ui/ListDetailShell';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { StatusText } from '@/shared/ui/StatusText';
import { Divider } from '@/shared/ui/Divider';
import { resolveEffectiveFields } from '@/shared/api/schema';
import { useEmployee } from '@/shared/api/employees';
import type { DocumentType } from '@/shared/api/types';
import { ArchivedBanner } from '../document-sets/catalog/ArchiveParts';
import { EditEntryForm } from '../document-sets/catalog/CatalogResource';
import { useRecordArchive } from '../document-sets/catalog/useRecordArchive';
import { AccountBlock } from './AccountBlock';
import { requisiteRows } from './employeeAccount';

/**
 * Карточка сотрудника (макет Р1, вариант А/В — «обычная карточка»): реквизиты для чтения и блок
 * «Учётная запись». Правка реквизитов — той же формой, что в «Общих данных» (`EditEntryForm`): запись
 * уходит дверью сотрудников (`OWN_DOORS`, решение по C15), и двух форм одного сотрудника, которые
 * разошлись бы, нет.
 */
export function EmployeeDetail({ id, canEdit, types, familyTypes }: {
  id: string;
  canEdit: boolean;
  types: DocumentType[];
  familyTypes: DocumentType[];
}) {
  const { data: employee, isLoading, isError } = useEmployee(id);
  const [editing, setEditing] = useState(false);
  const archive = useRecordArchive(types);

  if (isLoading) return <p className="p-6 text-body-medium text-on-surface-variant">Загрузка карточки…</p>;
  if (isError || !employee) return <p className="p-6 text-body-medium text-on-surface-variant">Карточку получить не удалось.</p>;

  const type = types.find(t => t.id === employee.compositeTypeId);
  const rows = type ? requisiteRows(resolveEffectiveFields(type, types), employee.data) : [];
  // Архив сбрасывает всё прочитанное сам (useSetCommonDataArchive) — и список сотрудников тоже.
  const toggleArchive = () => archive.act(employee, !employee.archived);

  return (
    <div className="flex flex-col min-h-0 flex-1" data-testid="employee-detail">
      <div className="shrink-0 px-6 py-4 border-b border-outline-variant bg-surface flex items-start gap-3">
        <span className="min-w-0 flex-1 flex flex-wrap items-center gap-x-2 gap-y-1">
          <DetailTitle>{employee.displayName}</DetailTitle>
          {employee.archived && <StatusText>в архиве</StatusText>}
        </span>
        {canEdit && (
          <>
            <Button variant="text" icon={<Icon icon={Edit} size={18} />} onClick={() => setEditing(true)}>Изменить</Button>
            <RowActionsMenu ariaLabel="Ещё" actions={[employee.archived
              ? { key: 'unarchive', label: 'Вернуть из архива', icon: Unarchive, onSelect: () => void toggleArchive() }
              : { key: 'archive', label: 'В архив', icon: Archive, onSelect: () => void toggleArchive() }]} />
          </>
        )}
      </div>
      {employee.archived && (
        <ArchivedBanner employee busy={archive.pending} onReturn={canEdit ? () => void toggleArchive() : undefined} />
      )}

      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-5 space-y-6">
        {rows.length > 0 && (
          <dl className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-2 text-body-medium" data-testid="employee-requisites">
            {rows.map(r => (
              <div key={r.key} className="contents">
                <dt className="text-on-surface-variant">{r.title}</dt>
                <dd className="text-on-surface break-words">{r.text}</dd>
              </div>
            ))}
          </dl>
        )}
        <Divider />
        <AccountBlock employee={employee} canEdit={canEdit} />
      </div>

      <Modal open={editing} onOpenChange={setEditing} title="Изменить сотрудника" wide flushBody>
        {editing && (
          // Читает дверью сотрудников: правка под их правом не обязана требовать права на общие данные.
          <EditEntryForm id={employee.id} readBase="/employees" compositeTypes={familyTypes} documentTypes={[]} allDocTypes={types}
            scope="System" scopeId={null} employee archiveBusy={archive.pending}
            // Список и карточку сбрасывает сама запись дверью сотрудников (OWN_DOORS.screen).
            onClose={() => setEditing(false)} />
        )}
      </Modal>
    </div>
  );
}
