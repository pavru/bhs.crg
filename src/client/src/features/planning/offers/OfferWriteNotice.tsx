import { Banner } from '@/shared/ui/Banner';
import type { useOfferWriteForm } from './useOfferWriteForm';

export function OfferWriteNotice({ form, hasInput = false }: { form: ReturnType<typeof useOfferWriteForm>; hasInput?: boolean }) {
  if (form.refusal) return <Banner tone="error" actions={form.refusal.kind === 'conflict' ? [{
    label: 'Перечитать КП', onClick: () => void form.reload(), loading: form.busy,
  }] : undefined}>{form.refusal.message}{hasInput && ' Введённые данные остались в форме.'}</Banner>;
  if (form.reviewed) return <Banner tone="info">КП перечитано. {hasInput ? 'Сверьте введённое с текущим КП перед сохранением.' : 'Проверьте состояние и подтвердите действие ещё раз.'}</Banner>;
  return null;
}
