import { Link2 } from 'lucide-react';
import { useCommonDataEntry } from '@/shared/api/commonData';
import { ArchivedMark } from '@/shared/ui/ArchivedMark';

/** Показ резолвнутой $ref-ссылки в связанном поле (issue #99): резолвит запись каталога по id → имя. */
export function BoundRefValue({ entryId }: { entryId: string }) {
  const { data: entry } = useCommonDataEntry(entryId);
  return (
    <span className="inline-flex items-center gap-1 text-brand">
      <Link2 size={12} className="shrink-0" />
      {entry ? entry.displayName : <span className="text-fg4">запись каталога…</span>}
      {/* Запись по идентификатору несёт признак сама (issue #1185): связка цела, пометка — о выборе. */}
      {entry?.archived && <ArchivedMark />}
    </span>
  );
}
