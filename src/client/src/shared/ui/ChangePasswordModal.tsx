import { useState } from 'react';
import { Modal, ModalButtons, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { useChangeMyPassword } from '@/shared/api/account';
import { useAuth } from '@/shared/hooks/useAuth';
import { PASSWORD_MIN_LENGTH, PASSWORD_HINT } from '@/shared/auth/passwordPolicy';

function apiError(e: unknown): string {
  const err = e as { response?: { data?: { error?: string; errors?: { description: string }[] } }; message?: string };
  const data = err?.response?.data;
  if (Array.isArray(data?.errors)) return data!.errors.map(x => x.description).join('; ');
  return data?.error || err?.message || 'Ошибка';
}

export function ChangePasswordModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const change = useChangeMyPassword();
  const { updateSession } = useAuth();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState('');
  const [done, setDone] = useState(false);

  function reset() { setCurrent(''); setNext(''); setConfirm(''); setError(''); setDone(false); }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    if (next !== confirm) { setError('Новый пароль и подтверждение не совпадают'); return; }
    try {
      const res = await change.mutateAsync({ currentPassword: current, newPassword: next });
      // Смена пароля отзывает старые токены → применяем свежую пару, чтобы не разлогиниться.
      if (res?.accessToken && res?.refreshToken) updateSession(res.accessToken, res.refreshToken);
      setDone(true);
      setTimeout(() => { reset(); onClose(); }, 1200);
    } catch (err) { setError(apiError(err)); }
  }

  return (
    <Modal open={open} onOpenChange={o => { if (!o) { reset(); onClose(); } }} title="Сменить пароль">
      <form onSubmit={submit} className="space-y-3">
        <div>
          <label className="block text-body-medium font-medium text-on-surface-variant mb-1">Текущий пароль</label>
          <input type="password" value={current} onChange={e => setCurrent(e.target.value)} required autoFocus
            className="w-full border border-outline rounded-sm px-3 py-2 text-body-medium bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-primary" />
        </div>
        <div>
          <label className="block text-body-medium font-medium text-on-surface-variant mb-1">Новый пароль</label>
          <input type="password" value={next} onChange={e => setNext(e.target.value)} required minLength={PASSWORD_MIN_LENGTH}
            className="w-full border border-outline rounded-sm px-3 py-2 text-body-medium bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-primary" />
          <p className="mt-1 text-body-small text-on-surface-variant">{PASSWORD_HINT}</p>
        </div>
        <div>
          <label className="block text-body-medium font-medium text-on-surface-variant mb-1">Подтверждение</label>
          <input type="password" value={confirm} onChange={e => setConfirm(e.target.value)} required minLength={PASSWORD_MIN_LENGTH}
            className="w-full border border-outline rounded-sm px-3 py-2 text-body-medium bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-primary" />
        </div>
        {error && <p className="text-body-medium text-error">{error}</p>}
        {done && <p className="text-body-medium text-success">Пароль изменён</p>}
        <ModalButtons className="pt-1">
          <ModalExit />
          <ModalPrimary type="submit" loading={change.isPending}>
            {change.isPending ? 'Сохранение…' : 'Сменить'}
          </ModalPrimary>
        </ModalButtons>
      </form>
    </Modal>
  );
}
