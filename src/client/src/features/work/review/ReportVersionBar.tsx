import type { ReviewFullCard } from '@/shared/api/workReview';
import { formatDateTime } from '@/shared/format/format';
import { Select, SelectItem } from '@/shared/ui/Select';

export function ReportVersionBar({ card, onChange }: { card: ReviewFullCard; onChange: (number: number | undefined) => void }) {
  const { report, viewedVersion, atClosingVersion } = card;
  if (!report.versions.length) return null;
  const shown = report.versions.find(version => version.number === viewedVersion);
  return <div className="flex flex-wrap items-center gap-x-5 gap-y-2">
    <div className="w-64 max-w-full">
      <Select label="Версия отчёта" density="dense" value={String(viewedVersion)}
        onValueChange={value => onChange(Number(value) === report.currentVersion ? undefined : Number(value))}>
        <SelectItem value={String(report.currentVersion)}>Версия {report.currentVersion} · действующая</SelectItem>
        {[...report.versions].reverse().map(version => <SelectItem key={version.number} value={String(version.number)}>
          Версия {version.number}{version.number === atClosingVersion ? ' · на момент закрытия' : ''}
        </SelectItem>)}
      </Select>
    </div>
    {shown && <div className="text-body-small text-on-surface-variant">
      <p>Заменил {shown.author.name} · {formatDateTime(shown.at)}</p>
      <p className="whitespace-pre-wrap">Причина исправления · {shown.reason}</p>
    </div>}
  </div>;
}
