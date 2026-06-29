import { useState } from 'react';
import { Plus, KeyRound, Trash2, ShieldCheck, User as UserIcon } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { useAuth, type UserRole } from '@/shared/hooks/useAuth';
import {
  useListUsers, useCreateUser, useChangeUserRole, useResetUserPassword, useDeleteUser,
  type AppUser,
} from '@/shared/api/users';

function apiError(e: unknown): string {
  const err = e as { response?: { data?: { error?: string } }; message?: string };
  return err?.response?.data?.error || err?.message || 'Ошибка';
}

export function UsersPage() {
  const { user: me } = useAuth();
  const { data: users = [], isLoading } = useListUsers();
  const changeRole = useChangeUserRole();
  const del = useDeleteUser();
  const [createOpen, setCreateOpen] = useState(false);
  const [resetFor, setResetFor] = useState<AppUser | null>(null);
  const [rowError, setRowError] = useState<{ id: string; msg: string } | null>(null);

  async function onRoleChange(u: AppUser, role: UserRole) {
    setRowError(null);
    try { await changeRole.mutateAsync({ id: u.id, role }); }
    catch (e) { setRowError({ id: u.id, msg: apiError(e) }); }
  }

  async function onDelete(u: AppUser) {
    if (!confirm(`Удалить пользователя «${u.email}»? Действие необратимо.`)) return;
    setRowError(null);
    try { await del.mutateAsync(u.id); }
    catch (e) { setRowError({ id: u.id, msg: apiError(e) }); }
  }

  return (
    <div className="px-6 py-4 max-w-4xl">
      <div className="flex items-center justify-between mb-4">
        <h1 className="text-xl font-semibold text-fg1">Пользователи</h1>
        <button onClick={() => setCreateOpen(true)}
          className="flex items-center gap-2 bg-brand hover:bg-brand-hover text-white text-sm font-medium px-4 py-2 rounded-md transition-colors">
          <Plus size={16} /> Добавить пользователя
        </button>
      </div>

      {isLoading ? (
        <div className="text-center text-fg4 text-sm py-10">Загрузка...</div>
      ) : (
        <div className="border border-stroke rounded-lg overflow-hidden bg-surface">
          <table className="w-full text-sm">
            <thead className="bg-base border-b border-stroke">
              <tr>
                <th className="text-left px-4 py-2.5 font-medium text-fg2">Пользователь</th>
                <th className="text-left px-4 py-2.5 font-medium text-fg2 w-48">Роль</th>
                <th className="px-4 py-2.5 w-24" />
              </tr>
            </thead>
            <tbody className="divide-y divide-muted">
              {users.map(u => {
                const isSelf = u.id === me?.sub;
                return (
                  <tr key={u.id} className="group hover:bg-base align-top">
                    <td className="px-4 py-2.5">
                      <div className="text-fg1 font-medium flex items-center gap-2">
                        {u.role === 'Admin'
                          ? <ShieldCheck size={14} className="text-brand shrink-0" />
                          : <UserIcon size={14} className="text-fg4 shrink-0" />}
                        {u.displayName || u.email}
                        {isSelf && <span className="text-[11px] text-fg4 font-normal">(вы)</span>}
                      </div>
                      {u.displayName && <div className="text-xs text-fg4 mt-0.5">{u.email}</div>}
                      {rowError?.id === u.id && <div className="text-xs text-danger mt-1">{rowError.msg}</div>}
                    </td>
                    <td className="px-4 py-2.5">
                      <select value={u.role} onChange={e => onRoleChange(u, e.target.value as UserRole)}
                        disabled={changeRole.isPending}
                        className="border border-stroke-strong rounded-md px-2 py-1.5 text-sm bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand">
                        <option value="Admin">Администратор</option>
                        <option value="User">Пользователь</option>
                      </select>
                    </td>
                    <td className="px-4 py-2.5">
                      <div className="flex items-center justify-end gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                        <button onClick={() => setResetFor(u)} title="Сбросить пароль"
                          className="p-1.5 text-fg4 hover:text-brand rounded"><KeyRound size={14} /></button>
                        <button onClick={() => onDelete(u)} disabled={isSelf || del.isPending}
                          title={isSelf ? 'Нельзя удалить себя' : 'Удалить'}
                          className="p-1.5 text-fg4 hover:text-danger rounded disabled:opacity-30 disabled:hover:text-fg4"><Trash2 size={14} /></button>
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      <p className="text-xs text-fg4 mt-3">
        Администратор — полный доступ. Пользователь — только работа с документами и данными (без настройки системы).
      </p>

      <CreateUserModal open={createOpen} onClose={() => setCreateOpen(false)} />
      <ResetPasswordModal user={resetFor} onClose={() => setResetFor(null)} />
    </div>
  );
}

function CreateUserModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const create = useCreateUser();
  const [email, setEmail] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [role, setRole] = useState<UserRole>('User');
  const [error, setError] = useState('');

  function reset() { setEmail(''); setDisplayName(''); setPassword(''); setRole('User'); setError(''); }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    try {
      await create.mutateAsync({ email: email.trim(), displayName: displayName.trim(), password, role });
      reset(); onClose();
    } catch (err) { setError(apiError(err)); }
  }

  return (
    <Modal open={open} onOpenChange={o => { if (!o) { reset(); onClose(); } }} title="Новый пользователь">
      <form onSubmit={submit} className="space-y-3">
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Email</label>
          <input type="email" value={email} onChange={e => setEmail(e.target.value)} required autoFocus
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
        </div>
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Отображаемое имя</label>
          <input value={displayName} onChange={e => setDisplayName(e.target.value)}
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
        </div>
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Начальный пароль</label>
          <input type="text" value={password} onChange={e => setPassword(e.target.value)} required minLength={6}
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm font-mono bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
          <p className="text-xs text-fg4 mt-1">Минимум 6 символов. Пользователь сможет сменить его сам.</p>
        </div>
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Роль</label>
          <select value={role} onChange={e => setRole(e.target.value as UserRole)}
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand">
            <option value="User">Пользователь — только документы и данные</option>
            <option value="Admin">Администратор — полный доступ</option>
          </select>
        </div>
        {error && <p className="text-sm text-danger">{error}</p>}
        <div className="flex justify-end gap-3 pt-1">
          <button type="button" onClick={() => { reset(); onClose(); }}
            className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">Отмена</button>
          <button type="submit" disabled={create.isPending}
            className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
            {create.isPending ? 'Создание...' : 'Создать'}
          </button>
        </div>
      </form>
    </Modal>
  );
}

function ResetPasswordModal({ user, onClose }: { user: AppUser | null; onClose: () => void }) {
  const reset = useResetUserPassword();
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [done, setDone] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!user) return;
    setError('');
    try { await reset.mutateAsync({ id: user.id, newPassword: password }); setDone(true); setTimeout(() => { setDone(false); setPassword(''); onClose(); }, 1200); }
    catch (err) { setError(apiError(err)); }
  }

  return (
    <Modal open={!!user} onOpenChange={o => { if (!o) { setPassword(''); setError(''); setDone(false); onClose(); } }}
      title={`Сброс пароля — ${user?.email ?? ''}`}>
      <form onSubmit={submit} className="space-y-3">
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Новый пароль</label>
          <input type="text" value={password} onChange={e => setPassword(e.target.value)} required minLength={6} autoFocus
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm font-mono bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
          <p className="text-xs text-fg4 mt-1">Сообщите новый пароль пользователю.</p>
        </div>
        {error && <p className="text-sm text-danger">{error}</p>}
        {done && <p className="text-sm text-success">Пароль изменён</p>}
        <div className="flex justify-end gap-3 pt-1">
          <button type="button" onClick={onClose}
            className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">Отмена</button>
          <button type="submit" disabled={reset.isPending}
            className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
            {reset.isPending ? 'Сохранение...' : 'Сбросить пароль'}
          </button>
        </div>
      </form>
    </Modal>
  );
}
