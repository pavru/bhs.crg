import { useEffect, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Add, Delete, Key, Mail, Person, VerifiedUser } from '@/shared/ui/icons';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { Button, IconButton } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { PASSWORD_MIN_LENGTH, PASSWORD_HINT } from '@/shared/auth/passwordPolicy';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { SendMessageDialog } from '@/shared/ui/SendMessageDialog';
import { useAuth } from '@/shared/hooks/useAuth';
import { useRoles } from '@/shared/api/roles';
import { RolePicker } from './RolePicker';
import {
  useListUsers, useCreateUser, useChangeUserRoles, useResetUserPassword, useDeleteUser,
  type AppUser,
} from '@/shared/api/users';
import { PageHeader } from '@/shared/ui/PageHeader';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';

function apiError(e: unknown): string {
  const err = e as { response?: { data?: { error?: string } }; message?: string };
  return err?.response?.data?.error || err?.message || 'Ошибка';
}

export function UsersPage() {
  const { user: me } = useAuth();
  const { data: users = [], isLoading } = useListUsers();
  const { data: roles = [] } = useRoles();
  const changeRoles = useChangeUserRoles();
  const del = useDeleteUser();
  const [createOpen, setCreateOpen] = useState(false);
  const [resetFor, setResetFor] = useState<AppUser | null>(null);
  const [rowError, setRowError] = useState<{ id: string; msg: string } | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<AppUser | null>(null);
  const [sendOpen, setSendOpen] = useState(false);

  async function onRolesChange(u: AppUser, roles: string[]) {
    setRowError(null);
    try { await changeRoles.mutateAsync({ id: u.id, roles }); }
    catch (e) { setRowError({ id: u.id, msg: apiError(e) }); }
  }


  const columns: DataGridColumn<AppUser>[] = [
    { key: 'user', label: 'Пользователь', wrap: true,
      cell: u => {
        const isSelf = u.id === me?.sub;
        return (
          <>
            <div className="text-on-surface font-medium flex items-center gap-2">
              {/* Щит — у роли «все права», а не у роли с именем «Admin»: признак
                  приходит с сервера, а имя ничего не значит (issues #989/#990).
                  Ролей может быть несколько, и щит ставится, если ТАКАЯ есть хотя бы
                  одна: «все права» плюс что-то ещё — всё равно все права. */}
              {u.roles.some(ur => roles.find(r => r.name === ur.name)?.allPermissions)
                ? <Icon icon={VerifiedUser} size={18} className="text-primary shrink-0" />
                : <Icon icon={Person} size={18} className="text-on-surface-variant shrink-0" />}
              {u.displayName || u.email}
              {isSelf && <span className="text-caption text-on-surface-variant font-normal">(вы)</span>}
            </div>
            {u.displayName && <div className="text-xs text-on-surface-variant mt-0.5">{u.email}</div>}
            {rowError?.id === u.id && <div className="text-xs text-error mt-1">{rowError.msg}</div>}
          </>
        );
      } },
    { key: 'roles', label: 'Роли', width: 'w-56',
      cell: u => (
        <RolePicker value={u.roles.map(r => r.name)} known={u.roles}
          onChange={next => onRolesChange(u, next)}
          disabled={changeRoles.isPending}
          ariaLabel={`Роли — ${u.displayName || u.email}`} className="w-52" />
      ) },
    { key: 'actions', label: '', width: 'w-24',
      cell: u => {
        const isSelf = u.id === me?.sub;
        return (
          <div className="flex items-center justify-end gap-1 opacity-0 group-hover/row:opacity-100 group-focus-within/row:opacity-100 transition-opacity">
            <IconButton label="Сбросить пароль" size="sm" onClick={() => setResetFor(u)}>
              <Icon icon={Key} size={18} />
            </IconButton>
            <IconButton label="Удалить" size="sm" danger onClick={() => setDeleteTarget(u)}
              disabled={isSelf || del.isPending}
              title={isSelf ? 'Нельзя удалить себя' : 'Удалить'}>
              <Icon icon={Delete} size={18} />
            </IconButton>
          </div>
        );
      } },
  ];

  return (
    <div className="px-6 py-4 max-w-4xl">
      <PageHeader title="Пользователи" className="mb-4" actions={<>
        <Button variant="tonal" icon={<Icon icon={Mail} size={18} />} onClick={() => setSendOpen(true)}>
          Отправить сообщение
        </Button>
        <Button variant="filled" icon={<Icon icon={Add} size={18} />} onClick={() => setCreateOpen(true)}>
          Добавить пользователя
        </Button>
      </>} />

      <SendMessageDialog open={sendOpen} onClose={() => setSendOpen(false)}
        candidates={users.map(u => ({ id: u.id, displayName: u.displayName, email: u.email }))} />

      {isLoading ? (
        <div className="text-center text-on-surface-variant text-sm py-10">Загрузка...</div>
      ) : (
        <DataGrid columns={columns} rows={users} rowKey={u => u.id} />
      )}

      <p className="text-xs text-on-surface-variant mt-3">
        Ролей у человека может быть несколько: действующие права — объединение прав всех его ролей.
        Что именно даёт каждая и кому её выдавать — на экране «Роли и права»: там же состав
        правится, и правка действует немедленно у всех носителей. Снятые все роли означают
        отозванный доступ: человек войдёт, но не увидит ничего.
      </p>

      <CreateUserModal open={createOpen} onClose={() => setCreateOpen(false)} />
      <ResetPasswordModal user={resetFor} onClose={() => setResetFor(null)} />
      <ConfirmDialog
        open={!!deleteTarget}
        onOpenChange={o => { if (!o) setDeleteTarget(null); }}
        title={`Удалить пользователя «${deleteTarget?.email ?? ''}»?`}
        description={<p>Действие необратимо.</p>}
        confirmLabel="Удалить пользователя"
        onConfirm={() => { if (deleteTarget) return del.mutateAsync(deleteTarget.id); }}
      />
    </div>
  );
}

function CreateUserModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const create = useCreateUser();
  const [email, setEmail] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  // Умолчания у роли НЕТ, и это главное в этом диалоге.
  //
  // ⚠️ Здесь стояло `role || roles[0]?.name`. Выглядело безобидно — «первая роль списка вместо
  // имени, зашитого в код», — а означало: какая роль окажется первой в ответе сервера, такую и
  // получит человек, у которого администратор роль не выбрал. Сервер сортировал по техническому
  // имени, первой шла Accountant, и новый сотрудник заводился «Бухгалтером» — с правом отмечать
  // оплату и закрывать период (ревью #983). Порядок на сервере починен, но умолчание опасно самим
  // своим существованием: выдача прав — не то место, где подставляют «что-нибудь».
  //
  // Ролей может быть несколько (issue #984), но пустой список при ЗАВЕДЕНИИ отвергает и сервер:
  // завести человека без единой роли — почти всегда промах, он войдёт и не увидит ничего.
  // Снять же все роли у работающего можно, и это другое действие — осознанный отзыв доступа.
  const [chosen, setChosen] = useState<string[]>([]);
  const [error, setError] = useState('');

  function reset() { setEmail(''); setDisplayName(''); setPassword(''); setChosen([]); setError(''); }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    if (chosen.length === 0) { setError('Выберите роль: она и есть набор прав, который получит человек.'); return; }
    try {
      await create.mutateAsync({ email: email.trim(), displayName: displayName.trim(), password, roles: chosen });
      reset(); onClose();
    } catch (err) { setError(apiError(err)); }
  }

  return (
    <Modal open={open} onOpenChange={o => { if (!o) { reset(); onClose(); } }} title="Новый пользователь"
      // Роли выбирают в меню, которого окно не замечает, — признак правок явный.
      isDirty={!!email.trim() || !!displayName.trim() || !!password || chosen.length > 0} busy={create.isPending}>
      <form onSubmit={submit} className="space-y-3">
        <TextField label="Email" type="email" value={email} onChange={e => setEmail(e.target.value)} required autoFocus />
        <TextField label="Отображаемое имя" value={displayName} onChange={e => setDisplayName(e.target.value)} />
        <TextField label="Начальный пароль" type="text" value={password} onChange={e => setPassword(e.target.value)}
          required minLength={PASSWORD_MIN_LENGTH} className="font-mono"
          hint={`${PASSWORD_HINT} Пользователь сможет сменить его сам.`} />
        <div>
          <div className="text-xs text-on-surface-variant mb-1">Роли</div>
          <RolePicker value={chosen} onChange={setChosen} ariaLabel="Роли нового пользователя" />
          <p className="text-xs text-on-surface-variant mt-1">
            Можно выбрать несколько: права складываются. Что даёт каждая — на экране «Роли и права».
          </p>
        </div>
        {error && <p className="text-sm text-error">{error}</p>}
        <div className="flex justify-end gap-2 pt-1">
          <ModalExit />
          <Button type="submit" variant="filled" loading={create.isPending} disabled={chosen.length === 0}>
            {create.isPending ? 'Создание…' : 'Создать'}
          </Button>
        </div>
      </form>
    </Modal>
  );
}

/**
 * Сброс пароля. Окно одно и смонтировано всегда — так `Modal` возвращает фокус на кнопку-ключ в
 * строке (ревью PR #1312); состояние поэтому сбрасывается при закрытии, руками.
 */
export function ResetPasswordModal({ user, onClose }: { user: AppUser | null; onClose: () => void }) {
  const reset = useResetUserPassword();
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  // Пароль, который сервер принял. Раньше окно само закрывалось через 1,2 секунды и стирало его
  // (issue #1237): не успел прочитать — сбрасывай второй раз. Теперь он стоит на экране, пока
  // человек сам не скажет «Готово»: это результат, который читают, и таймеру тут не место.
  const [issued, setIssued] = useState<string | null>(null);
  const [copied, setCopied] = useState<'yes' | 'failed' | null>(null);

  // «Скопировано» верно, пока человек не ушёл из окна браузера: там буфер могли перезаписать, а
  // подпись продолжала бы утверждать своё. Вернулся — кнопка снова предлагает скопировать.
  useEffect(() => {
    const forget = () => setCopied(null);
    window.addEventListener('blur', forget);
    return () => window.removeEventListener('blur', forget);
  }, []);

  function leave() {
    setPassword(''); setError(''); setIssued(null); setCopied(null);
    onClose();
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!user) return;
    setError('');
    try { await reset.mutateAsync({ id: user.id, newPassword: password }); setIssued(password); }
    catch (err) { setError(apiError(err)); }
  }

  async function copy() {
    // Буфер доступен не везде (страница без HTTPS, отказ браузера) — тогда честно говорим, что
    // копировать придётся выделением: поле для того и оставлено выделяемым.
    try { await navigator.clipboard.writeText(issued ?? ''); setCopied('yes'); }
    catch { setCopied('failed'); }
  }

  return (
    <Modal open={!!user} onOpenChange={o => { if (!o) leave(); }}
      title={`${issued === null ? 'Сброс пароля' : 'Пароль изменён'} — ${user?.email ?? ''}`}
      isDirty={issued === null && password !== ''} busy={reset.isPending}
      acknowledge={issued === null ? undefined : 'Готово'}>
      {issued === null ? (
        <form onSubmit={submit} className="space-y-3">
          <TextField label="Новый пароль" type="text" value={password} onChange={e => setPassword(e.target.value)}
            required minLength={PASSWORD_MIN_LENGTH} autoFocus className="font-mono"
            hint={`${PASSWORD_HINT} После сброса пароль останется на экране — его можно будет скопировать.`} />
          {error && <p role="alert" className="text-sm text-error">{error}</p>}
          <div className="flex justify-end gap-2 pt-1">
            <ModalExit />
            <Button type="submit" variant="filled" loading={reset.isPending}>
              {reset.isPending ? 'Сохранение…' : 'Сбросить пароль'}
            </Button>
          </div>
        </form>
      ) : (
        <div className="space-y-3">
          <div className="flex items-center gap-2">
            {/* Поле только для чтения, а не `<output>`: тот — живая область, и секрет при появлении
                зачитывался бы вслух программой чтения с экрана. */}
            <input readOnly aria-label="Новый пароль" value={issued} onFocus={e => e.target.select()}
              className="flex-1 min-w-0 rounded-md border border-outline-variant bg-surface-container-low px-3 py-2 font-mono text-sm text-on-surface" />
            {/* Фокус — сюда, а не на «Готово»: форму отправляют клавишей Enter, и задержанная
                клавиша закрыла бы окно с паролем раньше, чем его прочли. */}
            <Button type="button" variant="tonal" autoFocus onClick={copy}>
              {copied === 'yes' ? 'Скопировано' : 'Скопировать'}
            </Button>
          </div>
          {copied === 'failed' && (
            <p role="alert" className="text-sm text-error">Скопировать не удалось — выделите пароль и скопируйте вручную.</p>
          )}
          <p className="text-sm text-on-surface-variant">
            Сообщите пароль пользователю. После закрытия окна он больше нигде не показывается.
          </p>
        </div>
      )}
    </Modal>
  );
}
