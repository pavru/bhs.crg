export type Kind = 'holiday' | 'working' | 'short';
export type Days = Record<string, Kind>;
export type Variant = 'year' | 'quarter' | 'exceptions';
export type Scenario = 'filled' | 'empty' | 'readonly' | 'conflict';

export const variants: Record<Variant, string> = {
  year: 'А · Год целиком',
  quarter: 'Б · Квартал и подробности дня',
  exceptions: 'В · Исключения и обзор года',
};
export const scenarios: Record<Scenario, string> = {
  filled: 'Обычная работа',
  empty: 'Год не заполнен',
  readonly: 'Только чтение',
  conflict: 'Год изменили перед сохранением',
};
export const labels: Record<Kind, string> = {
  holiday: 'Нерабочий день',
  working: 'Рабочий выходной',
  short: 'Сокращённый день',
};
export const months = ['Январь', 'Февраль', 'Март', 'Апрель', 'Май', 'Июнь', 'Июль', 'Август', 'Сентябрь', 'Октябрь', 'Ноябрь', 'Декабрь'];
export const weekdays = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];
export const workdays = ['Понедельник', 'Вторник', 'Среда', 'Четверг', 'Пятница', 'Суббота', 'Воскресенье'];

// Демонстрационная подборка для сценария правки и сравнения. Календарь года загружается на сервере из файла.
export const example: Days = {
  '02-20': 'short', '02-22': 'holiday', '02-23': 'holiday', '03-08': 'holiday', '04-30': 'short',
  '05-01': 'holiday', '05-09': 'holiday', '05-10': 'holiday', '06-11': 'short', '11-04': 'holiday',
};
export const imported: Days = {
  ...example, '01-01': 'holiday', '01-02': 'holiday', '01-03': 'holiday', '01-04': 'holiday',
  '01-05': 'holiday', '01-06': 'holiday', '01-07': 'holiday', '01-08': 'holiday', '05-03': 'holiday',
  '06-12': 'holiday', '06-14': 'holiday', '11-03': 'short', '11-05': 'holiday', '12-31': 'holiday',
};
export const siteExceptions: Days = { '06-12': 'working', '06-19': 'holiday' };
export const dateLabel = (date: string) => new Intl.DateTimeFormat('ru-RU', {
  month: 'long', day: 'numeric', timeZone: 'UTC',
}).format(new Date(`2028-${date}T12:00:00Z`));
