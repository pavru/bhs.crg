import { useState } from 'react';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { Check, ChevronDown } from 'lucide-react';
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
    <DropdownMenu.Root onOpenChange={open => { if (!open) close(); }}>
      <DropdownMenu.Trigger asChild disabled={disabled}>
        <button type="button" aria-label={ariaLabel}
          className={`inline-flex items-center justify-between gap-2 w-full h-9 px-3 rounded-md border
            border-stroke-strong bg-surface text-sm text-left transition-colors
            focus:outline-none focus-visible:ring-2 focus-visible:ring-brand
            data-[state=open]:ring-2 data-[state=open]:ring-brand
            disabled:opacity-50 disabled:pointer-events-none
            ${chosen.length > 0 ? 'text-fg1' : 'text-fg4'} ${className}`}>
          <span className="min-w-0 flex-1 truncate">{label}</span>
          <ChevronDown size={15} className="text-fg4 shrink-0" aria-hidden="true" />
        </button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="start" sideOffset={4}
          className="z-50 min-w-[16rem] max-h-80 overflow-auto rounded-md border border-stroke bg-surface py-1"
          style={{ boxShadow: 'var(--f-shadow28)' }}>
          {/* Пустое меню — это отказ, переодетый в результат: выбрать нечего и непонятно почему. */}
          {roles.length === 0 && (
            <div className="px-3 py-2 text-xs text-fg4">
              {isLoading ? 'Роли загружаются…' : 'Список ролей не загрузился — обновите страницу.'}
            </div>
          )}
          {roles.map(r => (
            // onSelect гасим: меню закрывается по выбору пункта, а здесь выбирают НЕСКОЛЬКО —
            // закрытие после первой галки заставляло бы открывать его заново на каждую роль.
            <DropdownMenu.CheckboxItem key={r.name} checked={chosen.includes(r.name)}
              onSelect={e => e.preventDefault()} onCheckedChange={() => setDraft(toggleRole(chosen, r.name))}
              className="flex items-start gap-2 px-3 py-1.5 text-sm cursor-pointer select-none outline-none
                data-[highlighted]:bg-base">
              <span className="w-4 shrink-0 pt-0.5 text-brand">
                {chosen.includes(r.name) && <Check size={14} />}
              </span>
              <span className="min-w-0">
                <span className="block text-fg1">{r.title}</span>
                {r.summary && <span className="block text-xs text-fg4">{r.summary}</span>}
              </span>
            </DropdownMenu.CheckboxItem>
          ))}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}
