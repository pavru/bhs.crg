import { useState } from 'react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { useChangeMyPassword } from '@/shared/api/users';

function apiError(e: unknown): string {
  const err = e as { response?: { data?: { error?: string; errors?: { description: string }[] } }; message?: string };
  const data = err?.response?.data;
  if (Array.isArray(data?.errors)) return data!.errors.map(x => x.description).join('; ');
  return data?.error || err?.message || 'Ошибка';
}

export function ChangePasswordModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const change = useChangeMyPassword();
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
      await change.mutateAsync({ currentPassword: current, newPassword: next });
      setDone(true);
      setTimeout(() => { reset(); onClose(); }, 1200);
    } catch (err) { setError(apiError(err)); }
  }

  return (
    <Modal open={open} onOpenChange={o => { if (!o) { reset(); onClose(); } }} title="Сменить пароль">
      <form onSubmit={submit} className="space-y-3">
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Текущий пароль</label>
          <input type="password" value={current} onChange={e => setCurrent(e.target.value)} required autoFocus
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
        </div>
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Новый пароль</label>
          <input type="password" value={next} onChange={e => setNext(e.target.value)} required minLength={6}
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
        </div>
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Подтверждение</label>
          <input type="password" value={confirm} onChange={e => setConfirm(e.target.value)} required minLength={6}
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
        </div>
        {error && <p className="text-sm text-danger">{error}</p>}
        {done && <p className="text-sm text-success">Пароль изменён</p>}
        <div className="flex justify-end gap-2 pt-1">
          <Button variant="text" onClick={() => { reset(); onClose(); }}>Отмена</Button>
          <Button type="submit" variant="filled" loading={change.isPending}>
            {change.isPending ? 'Сохранение…' : 'Сменить'}
          </Button>
        </div>
      </form>
    </Modal>
  );
}
