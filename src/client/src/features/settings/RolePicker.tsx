import { useState } from 'react';
import { Menu, MenuCheckboxItem, MenuNote } from '@/shared/ui/Menu';
import { Icon } from '@/shared/ui/Icon';
import { KeyboardArrowDown } from '@/shared/ui/icons';
import { useRoles } from '@/shared/api/roles';
import type { RoleRef } from '@/shared/api/users';
import { toggleRole, sameRoles } from './rolePickerDraft';

/**
 * Выбор НЕСКОЛЬКИХ ролей (ТЗ AUTH-3, issue #984): действующие права — объединение прав всех
 * выбранных ролей.
 *
 * Меню с галками, а не выпадающий список: список с единственным выбором обещал бы, что роль одна,
 * и снимал бы прежнюю молча. Здесь видно и что выбрано, и что доступно.
 *
 * ⚠️ Выбор отдаётся наружу ОДИН раз — при закрытии меню, а не на каждую галку (ревью #984).
 * Пощелчковая отправка собирала каждый запрос из `value`, а тот обновлялся только после ответа
 * сервера: два быстрых щелчка — и второй запрос уходил со старым составом, отменяя первый. Здесь
 * галки правят черновик, а наружу уходит его итог, одним запросом.
 *
 * ⚠️ Список ролей ЖИВОЙ — он же источник и для редактора ролей. Перечень имён на клиенте означал
 * бы, что роль, заведённую администратором, назначить нечем: она есть, права у неё есть, а в меню
 * её нет.
 */
export function RolePicker({ value, known = [], onChange, disabled, ariaLabel = 'Роли', className = '' }: {
  value: string[];
  /**
   * Названия ролей, УЖЕ известные вызывающему (роли пользователя из списка). Нужны, чтобы подпись
   * не зависела от того, ответил ли `/api/roles`: собранная только из живого списка, она говорила
   * бы «Без ролей» — строку со смыслом «доступ отозван» — про человека, у которого роли есть.
   */
  known?: RoleRef[];
  onChange: (roles: string[]) => void;
  disabled?: boolean;
  ariaLabel?: string;
  className?: string;
}) {
  const { data: roles = [], isLoading } = useRoles();
  const [draft, setDraft] = useState<string[] | null>(null);
  const chosen = draft ?? value;

  // Название: сперва то, что пришло вместе с пользователем, затем живой список, затем само имя.
  // Имя вида role-1a2b3c4d читается плохо, но это правда — в отличие от «ролей нет».
  const titleOf = (name: string) =>
    known.find(r => r.name === name)?.title ?? roles.find(r => r.name === name)?.title ?? name;

  const label = chosen.length > 0 ? chosen.map(titleOf).join(', ') : 'Без ролей';

  function close() {
    const next = draft;
    setDraft(null);
    if (next && !sameRoles(next, value)) onChange(next);
  }

  return (
    <Menu className="min-w-64" maxHeight="20rem" disabled={disabled} onOpenChange={open => { if (!open) close(); }} trigger={
      <button type="button" aria-label={ariaLabel}
        className={`inline-flex items-center justify-between gap-2 w-full h-9 px-3 rounded-sm border
          border-outline bg-surface text-body-medium text-left transition-colors
          focus:outline-none focus-visible:ring-2 focus-visible:ring-primary
          data-[state=open]:ring-2 data-[state=open]:ring-primary
          disabled-outlined disabled:pointer-events-none
          ${chosen.length > 0 ? 'text-on-surface' : 'text-on-surface-variant'} ${className}`}>
        <span className="min-w-0 flex-1 truncate">{label}</span>
        <Icon icon={KeyboardArrowDown} size={18} className="text-on-surface-variant shrink-0" aria-hidden="true" />
      </button>
    }>
      {/* Пустое меню — это отказ, переодетый в результат: выбрать нечего и непонятно почему. */}
      {roles.length === 0 && (
        <MenuNote>{isLoading ? 'Роли загружаются…' : 'Список ролей не загрузился — обновите страницу.'}</MenuNote>
      )}
      {roles.map(r => (
        // onSelect гасим: меню закрывается по выбору пункта, а здесь выбирают НЕСКОЛЬКО —
        // закрытие после первой галки заставляло бы открывать его заново на каждую роль.
        <MenuCheckboxItem key={r.name} checked={chosen.includes(r.name)} description={r.summary || undefined}
          onSelect={e => e.preventDefault()} onCheckedChange={() => setDraft(toggleRole(chosen, r.name))}>
          {r.title}
        </MenuCheckboxItem>
      ))}
    </Menu>
  );
}
