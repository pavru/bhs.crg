import { formatDate } from '@/shared/format/format';
import { IconButton } from '@/shared/ui/Button';
import { ChevronLeft, ChevronRight } from '@/shared/ui/icons';
import { Select, SelectItem } from '@/shared/ui/Select';

export function SummaryDayPicker({ days, day, onChange }: { days: string[]; day: string; onChange: (day: string) => void }) {
  const index = days.indexOf(day);
  const previous = days[index - 1];
  const next = days[index + 1];
  return <div className="flex items-center gap-1" role="group" aria-label="День сводки">
    <IconButton icon={ChevronLeft} label="Предыдущий день" aria-disabled={!previous}
      onClick={previous ? () => onChange(previous) : undefined} />
    <div className="min-w-0 flex-1">
      <Select label="День" density="dense" value={day} onValueChange={onChange}>
        {days.map(date => <SelectItem key={date} value={date}>{formatDate(date)}</SelectItem>)}
      </Select>
    </div>
    <IconButton icon={ChevronRight} label="Следующий день" aria-disabled={!next}
      onClick={next ? () => onChange(next) : undefined} />
  </div>;
}
