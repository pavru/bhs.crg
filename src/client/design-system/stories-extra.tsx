import React from 'react';
import * as glyphs from '@/shared/ui/icons';
import type { Registry, Story, StoryProps } from './story-model';
import { storyId } from './story-model';
import { createStories } from './stories';

export function extraStories(registry: Registry = {}, base = createStories(registry)): Story[] {
  const stories: Story[] = [];
  const noop = () => {};
  const el = (name: string, props: StoryProps = {}, children?: React.ReactNode) => React.createElement((registry[name] ?? name) as React.ComponentType<StoryProps>, props, Array.isArray(children) ? React.Children.toArray(children) : children);
  const add = (component: string, variant: string, props: StoryProps = {}, children?: React.ReactNode, width?: number, renderAs?: string, state?: string) => stories.push({ id: storyId(component, variant), component, variant, props, children, width, renderAs, state });
  const alias = (component: string, owner: string, match?: (story: Story) => boolean) => {
    for (const source of base.filter(s => s.component === owner && (!match || match(s)))) stories.push({ ...source, id: storyId(component, source.variant), component, renderAs: source.renderAs ?? owner });
  };
  for (const component of ['MenuItem', 'MenuCheckboxItem', 'MenuRadioItem', 'MenuRadioGroup', 'MenuLabel', 'MenuNote', 'MenuSeparator', 'MenuGroup', 'MenuSub']) {
    const renderer = 'Demo' + component;
    registry[renderer] = (props: StoryProps) => el('Menu', { open: true, trigger: el('Button', {}, 'Действия') },
      component === 'MenuRadioGroup' ? el(component, props, [el('MenuRadioItem', { key: 'a', value: 'a' }, 'По дате'), el('MenuRadioItem', { key: 'b', value: 'b' }, 'По имени')]) :
        component === 'MenuRadioItem' ? el('MenuRadioGroup', { value: 'a', onValueChange: noop }, el(component, props, 'По дате')) :
          component === 'MenuSub' ? el(component, { ...props, label: 'Экспорт' }, el('MenuItem', {}, 'Скачать PDF')) :
            component === 'MenuGroup' ? el(component, props, el('MenuItem', {}, 'Документы')) :
              el(component, props, component === 'MenuSeparator' ? undefined : 'Документы'),
    );
    if (component === 'MenuItem') for (const state of ['Default', 'Disabled', 'Danger', 'Description', 'Checked']) add(component, `State=${state}`, { icon: glyphs.Description, disabled: state === 'Disabled', danger: state === 'Danger', checked: state === 'Checked' ? true : undefined, description: state === 'Description' ? 'Реквизиты и приложения' : undefined }, undefined, 450, renderer);
    else if (component === 'MenuCheckboxItem') for (const checked of [false, true, 'indeterminate']) add(component, `Checked=${checked}`, { checked, onCheckedChange: noop }, undefined, 450, renderer);
    else if (component === 'MenuRadioGroup') for (const value of ['a', 'b']) add(component, `Value=${value}`, { value, onValueChange: noop }, undefined, 450, renderer);
    else if (component === 'MenuRadioItem') for (const disabled of [false, true]) add(component, `Disabled=${disabled}`, { value: 'a', disabled }, undefined, 450, renderer);
    else add(component, 'State=Default', {}, undefined, 450, renderer);
  }
  for (const component of ['SelectGroup', 'SelectItem']) {
    const renderer = 'Demo' + component;
    registry[renderer] = (props: StoryProps) => el('Select', { label: 'Тип документа', value: 'a', onValueChange: noop }, component === 'SelectGroup' ? el(component, props, [el('SelectItem', { key: 'a', value: 'a' }, 'Акты'), el('SelectItem', { key: 'b', value: 'b' }, 'Журналы')]) : el(component, props, 'Акты'));
    if (component === 'SelectItem') for (const disabled of [false, true]) add(component, `Disabled=${disabled}`, { value: 'a', disabled, icon: glyphs.Description, supporting: disabled ? 'Недоступно' : undefined }, undefined, 380, renderer, 'Open');
    else add(component, 'State=Group', { label: 'Документы' }, undefined, 380, renderer, 'Open');
  }
  for (const form of ['list', 'menu']) for (const filled of [false, true]) add('ListRowBody', `Form=${form}, Filled=${filled}`, { form, filled, icon: glyphs.Description, headline: 'Акт скрытых работ', supporting: 'Корпус 1 · Электроснабжение', trailing: '12', overline: form === 'list' ? 'Документы' : undefined }, undefined, 450, 'DemoListRowBody');
  registry.DemoListRowBody = (props: StoryProps) => <div className={`flex items-center gap-4 p-4 rounded-sm ${props.filled ? 'bg-secondary-container text-on-secondary-container' : 'bg-surface text-on-surface'}`}>{el('ListRowBody', props)}</div>;
  add('SectionMenu', 'State=Open', { value: { groups: [{ label: 'Документы', items: [{ to: '/', label: 'Комплекты' }, { to: '/types', label: 'Типы документов' }] }], outside: [{ to: '/costs/invoices', label: 'Счета', module: 'costs' }] } }, el('Button', { variant: 'outlined' }, 'Страницы раздела'), 500, undefined, 'Open');
  function DemoHostedWindowStep(props: StoryProps) {
    const [step, setStep] = React.useState(true);
    return el('Modal', { open: true, onOpenChange: noop, title: 'Документ', footer: el('Button', { onClick: () => setStep(true) }, 'Открыть следующий шаг') }, [el('TextField', { key: 'field', label: 'Наименование', defaultValue: 'Акт скрытых работ' }), step ? el('WindowStep', { ...props, key: 'step', onBack: () => setStep(false) }, el('TextField', { label: 'Номер приложения', defaultValue: 'Приложение 1' })) : null]);
  };
  registry.DemoHostedWindowStep = DemoHostedWindowStep;
  add('WindowStep', 'Context=Hosted, State=Default', { title: 'Следующий шаг', onBack: noop }, undefined, 800, 'DemoHostedWindowStep');
  for (const rows of [1, 6]) add('TextAreaField', `Rows=${rows}`, { label: 'Примечание', rows, value: 'Прокладка кабеля в лотках на отметке +3.600', onChange: noop }, undefined, 420);
  const edit = base.find(s => s.component === 'EditGrid')!;
  add('EditGrid', 'State=Empty', { ...edit.props, rows: [], empty: 'Добавьте первую строку', add: { label: 'Добавить строку', onAdd: noop } }, undefined, 700);
  add('EditGrid', 'State=Numbered', { ...edit.props, numbered: true }, undefined, 700);
  add('EditGrid', 'State=AddRemove', { ...edit.props, add: { label: 'Добавить строку', onAdd: noop }, onRemove: noop }, undefined, 700);
  for (const member of ['Root', 'Trigger', 'Anchor', 'Content', 'Portal', 'Close', 'Arrow']) alias(member, 'Popover');
  alias('PdfPages', 'PdfViewer', s => s.variant.includes('Loaded'));
  alias('PdfViewerImpl', 'PdfViewer');
  alias('MonacoEditor', 'CodeEditor');
  alias('Magnifier', 'ImageView', s => s.variant.includes('Magnifier'));
  alias('TruncatedTips', 'Tooltip', s => s.variant.includes('State=Open'));
  const avatar = 'data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="80" height="80"><rect width="80" height="80" rx="40" fill="#1d5fb0"/><text x="40" y="51" text-anchor="middle" font-family="Arial" font-size="30" fill="white">АП</text></svg>');
  add('Avatar', 'State=Image', { src: avatar, name: 'Александр Потоцкий', alt: 'Александр Потоцкий' });
  add('Avatar', 'State=ImageError', { src: 'data:image/png;base64,broken', name: 'Александр Потоцкий', alt: 'Александр Потоцкий' });
  for (const size of [20, 24, 40, 48]) add('ScopeIcon', `Size=${size}`, { scope: 'Construction', size });
  for (const tone of ['warning', 'error']) for (const compact of [false, true]) add('ValueIssueHint', `Tone=${tone}, Compact=${compact}`, { messages: [tone === 'error' ? 'Запись удалена' : 'Значение не найдено в справочнике'], compact }, undefined, 450);
  for (const state of ['ReadOnly', 'Required', 'HiddenLabel']) add('DateRange', `State=${state}`, { label: 'Период', from: '2026-11-01', to: '2026-11-21', onChange: noop, readOnly: state === 'ReadOnly', required: state === 'Required', labelHidden: state === 'HiddenLabel' }, undefined, 600);
  for (const state of ['Loading', 'Disabled', 'More', 'Refused']) add('EmployeePicker', `State=${state}`, { candidates: [{ employeeId: 'a', name: 'Иван Петров', personnelNumber: '125', eligible: true }], selected: new Set(['a']), search: '', onSearch: noop, onChange: noop, loading: state === 'Loading', disabled: state === 'Disabled', more: state === 'More', limit: 50, refused: state === 'Refused' ? { a: 'Сотрудник занят в другом назначении' } : {} }, undefined, 700);
  const filterColumns = [
    { name: 'name', label: 'Наименование', kind: 'string', operators: ['eq', 'contains', 'in', 'is_empty'] },
    { name: 'sum', label: 'Сумма', kind: 'number', operators: ['eq', 'gt', 'between'] },
    { name: 'date', label: 'Дата', kind: 'date', operators: ['eq', 'between', 'gte'] },
    { name: 'status', label: 'Состояние', kind: 'choice', options: ['Черновик', 'Готов'], operators: ['eq', 'in'] },
  ];
  for (const [column, op, value] of [['name', 'contains', 'Кабель'], ['sum', 'gt', '10000'], ['date', 'between', ['2026-11-01', '2026-11-21']], ['status', 'eq', 'draft']]) {
    const cond = { type: 'condition', column, op, ...(Array.isArray(value) ? { values: value } : { value }) };
    add('ConditionEditor', `Column=${column}`, { columns: filterColumns, cond, onChange: noop, stacked: false }, undefined, 800);
    add('RowFilterValue', `Column=${column}`, { column: filterColumns.find(c => c.name === column), cond, onChange: noop }, undefined, 500);
  }
  add('ConditionHint', 'State=UnknownValue', { columns: [{ ...filterColumns[0], kind: 'list', known: { complete: true, items: [{ value: 'Кабель' }] } }], cond: { type: 'condition', column: 'name', op: 'eq', value: 'Неизвестное наименование' }, onChange: noop }, undefined, 600);
  for (const state of ['Empty', 'Simple', 'Complex', 'Invalid', 'Suggested']) add('FilterChips', `State=${state}`, {
    columns: filterColumns, filter: state === 'Empty' || state === 'Suggested' ? null : state === 'Complex' ? { type: 'group', logic: 'or', children: [{ type: 'condition', column: 'name', op: 'contains', value: 'Кабель' }, { type: 'condition', column: 'sum', op: 'gt', value: '10000' }] } : { type: 'group', logic: 'and', children: [{ type: 'condition', column: state === 'Invalid' ? 'removed' : 'name', op: 'contains', value: 'Кабель' }] }, onChange: noop, onAdvanced: noop, suggested: state === 'Suggested' ? ['status', 'date'] : undefined,
  }, undefined, 800);
  for (const hidden of [0, 12]) for (const partial of [false, true]) add('SitesFilter', `Hidden=${hidden}, Partial=${partial}`, { hidden, partial }, undefined, 450);
  add('FavoritesPicker', 'State=Ready', {}, undefined, 500);
  for (const id of ['site-1', 'site-2']) add('FavoriteStar', `Site=${id}`, { id });
  add('NotFavoriteNote', 'State=OutsideFavorites', { constructionId: 'site-2' }, undefined, 600);
  add('FilterHidesAll', 'State=Hidden', { text: 'Документы скрыты отбором по стройкам', action: 'Показать все', onShowAll: noop, testId: 'catalog-hidden' }, undefined, 600);
  add('NotInListNotice', 'State=OutsideList', { text: 'Документ не входит в текущий список', onShowAll: noop, testId: 'catalog-not-in-list' }, undefined, 600);
  registry.DemoSiteSelect = () => el('Select', { label: 'Стройка', value: 'site-1', onValueChange: noop }, [el('SiteSelectItems', { key: 'sites', choices: { groups: [{ label: 'Избранные стройки', sites: [{ id: 'site-1', name: 'Корпус 1' }] }], hidden: 1 } }), el('RevealItem', { key: 'reveal', hidden: 1 })]);
  for (const component of ['SiteSelectItems', 'RevealItem', 'SiteRevealScope']) add(component, 'State=Open', {}, undefined, 400, 'DemoSiteSelect', 'Open');
  return stories;
}
