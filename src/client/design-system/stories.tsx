import React from 'react';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { railSections, workNav, settingsNav } from '@/shared/ui/navConfig';
import * as glyphs from '@/shared/ui/icons';
import type { Registry, Story, StoryProps } from './story-model';
import { normalizeStories } from './story-normalize';

export function createStories(registry:Registry={}):Story[]{
  const noop=()=>{};
  const el=(name:string,props:StoryProps={},children?:React.ReactNode):React.ReactElement=>React.createElement((registry[name]??name) as React.ComponentType<StoryProps>,props,Array.isArray(children)?React.Children.toArray(children):children);
  const icon=(name='Add',size=18)=>el('Icon',{icon:glyphs[name as keyof typeof glyphs],size});
  const samples:Story[]=[];
  function add(component:string,variant:string,props:StoryProps={},children?:React.ReactNode,width?:number,state?:string){samples.push({id:'',component,variant,props,children,width,state});}
const states = ['Default', 'Hover', 'Focus', 'Pressed', 'Disabled'];
for (const variant of ['filled', 'tonal', 'outlined', 'text']) {
  for (const state of [...states, 'Loading']) {
    add('Button', `Variant=${variant}, State=${state}`, {variant, disabled: state === 'Disabled', loading: state === 'Loading'}, 'Сохранить', undefined, state);
  }
  add('Button', `Variant=${variant}, State=Danger`, {variant, danger: true}, 'Удалить');
  add('Button', `Variant=${variant}, State=Leading`, {variant, icon: icon()}, 'Добавить');
  add('Button', `Variant=${variant}, State=Trailing`, {variant, trailingIcon: icon('ArrowForward')}, 'Продолжить');
  add('Button', `Variant=${variant}, State=Multiline`, {variant, multiline:true, className:'w-36'}, 'Сохранить изменения');
  add('LinkButton', `Variant=${variant}`, {variant, href:'#'}, 'Открыть');
}
for (const variant of ['standard', 'filled', 'tonal', 'outlined']) for (const state of [...states, 'Loading', 'Selected', 'Danger']) {
  add('IconButton', `Variant=${variant}, State=${state}`, {variant, icon:glyphs.Star, label:'Избранное', selected:state==='Selected', loading:state==='Loading', disabled:state==='Disabled', danger:state==='Danger'}, undefined, undefined, state);
}
for (const name of ['TextField','TextAreaField','NumberInput','DateField','DateInput','TimeField','TimeInput','SearchInput']) {
  const segmented=/Date|Time/.test(name);
  for (const density of name==='SearchInput'?['form']:['form','dense','cell']) for (const state of ['Empty','Filled','Hover','Focus','Disabled','ReadOnly','Error',...(name==='TextField'||name==='TextAreaField'?['Scan','Trailing']:[])]) {
    const filled=state!=='Empty';
    const value=segmented?(name.includes('Date')?'2026-11-21':'14:30'):name==='NumberInput'?1250:'Электромонтажные работы';
    const props:StoryProps={label: 'Наименование',value:filled?value:'',onChange:noop,density,disabled:state==='Disabled',readOnly:state==='ReadOnly',required:true};
    if(state==='Error'){props.error='Проверьте значение';props.hint='Проверьте значение';props.invalid=true;}
    if(state==='Scan')props.fromScan=true;
    if(state==='Trailing')props.trailing=el('IconButton',{icon:glyphs.Search,label:'Найти'});
    if(name==='SearchInput'){delete props.label;props.placeholder='Поиск…';props['aria-label']='Поиск';}
    add(name, `Density=${density}, State=${state}`,props,undefined,340,state);
  }
}
for(const checked of [false,true])for(const state of states){
  add('Switch',`Checked=${checked}, State=${state}`,{checked,onChange:noop,label:'Уведомления',disabled:state==='Disabled'},undefined,undefined,state);
  add('Checkbox',`Checked=${checked}, State=${state}`,{checked,onChange:noop,label:'Выбрать строку',disabled:state==='Disabled'},undefined,undefined,state);
}
for(const state of ['Indeterminate','ReadOnly','Error','Description'])add('Checkbox',`Checked=false, State=${state}`,{label:'Выбрать строку',checked:false,onChange:noop,indeterminate:state==='Indeterminate',readOnly:state==='ReadOnly','aria-invalid':state==='Error',description:state==='Description'?'Дополнительное описание':undefined});
for(const orientation of ['vertical','horizontal'])for(const state of ['Default','Disabled','Description'])add('RadioGroup',`Orientation=${orientation}, State=${state}`,{label:'Режим',orientation,disabled:state==='Disabled',items:[{value:'a',label:'Автоматически',description:state==='Description'?'Выбрать по условиям':undefined},{value:'b',label:'Вручную'}],value:'a',onChange:noop},undefined,400);
for(const selected of [false,true])for(const state of states)add('Chip',`Selected=${selected}, State=${state}`,{selected,onClick:noop,disabled:state==='Disabled'},'Электрика',undefined,state);
add('Chip','Selected=true, State=Remove',{selected:true,onRemove:noop,removeLabel:'Убрать отбор'},'Электрика');
add('Chip','Selected=true, State=Menu',{selected:true,menu:true,count:12},'Стройка');
add('Chip','Selected=false, State=Assist',{icon:glyphs.Add,onClick:noop},'Добавить');
for(const tone of ['neutral','primary','success','warning','error','quiet'])add('StatusText',`Tone=${tone}`,{tone},'На проверке');
for(const tone of ['info','success','warning','error'])for(const actions of [false,true])add('Banner',`Tone=${tone}, Actions=${actions}`,{tone,actions:actions?[{label:'Повторить',onClick:noop}]:[]},'Результат обработки документов',500);
for(const variant of ['outlined','filled','elevated'])for(const state of ['Static','Interactive','Hover','Focus','Dragged','DropTarget','Invalid'])add('Card',`Variant=${variant}, State=${state}`,{variant,title:'Документы стройки',onClick:state==='Static'?undefined:noop,dragged:state==='Dragged',dropTarget:state==='DropTarget',invalid:state==='Invalid'},'Комплект исполнительной документации',360,state);
for(const count of [undefined,1,99,125])add('Badge',`Count=${count??'Dot'}`,{count});
for(const onContainer of [false,true,'secondary'])for(const disabled of [false,true])add('CountText',`Container=${onContainer}, Disabled=${disabled}`,{onContainer,disabled},'24');
for(const state of ['Default','Selected','Hover','Focus','Disabled','TwoLine','WithIcon']){
  const base={headline:'Электромонтажные работы',supporting:state==='TwoLine'?'Объект · Секция 1':undefined,icon:state==='WithIcon'?glyphs.Description:undefined};
  add('ListItem',`State=${state}`,{...base,onClick:noop,selected:state==='Selected'},undefined,420,state);
  add('ListOption',`State=${state}`,{...base,id:'a',onChoose:noop,active:state==='Selected',disabled:state==='Disabled'},undefined,420,state);
}
for(const open of [false,true])add('ListGroupHeader',`Open=${open}`,{id:'group',label:'Документы',count:12,open,onToggle:noop},undefined,420);
for(const variant of ['underline','segmented'])for(const state of ['Default','Disabled','Badge'])add('Tabs',`Variant=${variant}, State=${state}`,{id:'tabs',variant,items:[{value:'a',label:'Общие',count:state==='Badge'?12:undefined},{value:'b',label:'Строки',disabled:state==='Disabled'}],value:'a',onChange:noop},undefined,420);
for(const disabled of [false,true])add('SegmentedButton',`Disabled=${disabled}`,{items:[{value:'a',label:'Неделя'},{value:'b',label:'Месяц'},{value:'c',label:'Год'}],value:'b',onChange:noop,disabled});
for(const precision of ['day','month','year'])for(const state of ['Selected','Range','Bounds'])add('Calendar',`Precision=${precision}, State=${state}`,{value:'2026-11-21',onSelect:noop,precision,onClear:noop,range:state==='Range'?['2026-11-10','2026-11-21']:undefined,min:state==='Bounds'?'2026-11-10':undefined,max:state==='Bounds'?'2026-11-25':undefined},undefined,312);
for(const state of ['Empty','Selected','Disabled'])add('DateRange',`State=${state}`,{label:'Период',value:state==='Empty'?[]:['2026-11-01','2026-11-21'],from:state==='Empty'?'':'2026-11-01',to:state==='Empty'?'':'2026-11-21',onChange:noop,disabled:state==='Disabled'},undefined,600);
for(const state of ['Empty','Selected','Busy','Disabled'])add('FileDropZone',`State=${state}`,{label:'Файл счёта',hint:'PDF, PNG и JPEG · до 50 МБ',file:state==='Selected'?{name:'Счёт № 125.pdf',size:142800}:undefined,busy:state==='Busy'?'Загружаем файл…':undefined,disabled:state==='Disabled'?'Сначала выберите стройку':false,onFile:noop},undefined,450);
for(const orientation of ['horizontal','vertical'])add('Divider',`Orientation=${orientation}`,{orientation,className:orientation==='vertical'?'h-20':'w-80'});
for(const value of [undefined,0,45,100])add('LinearProgress',`Value=${value??'Indeterminate'}`,{value,label:'Загрузка'},undefined,400);
for(const size of [18,20,24,32,40,48])add('CircularProgress',`Size=${size}`,{size,label:'Загрузка'});
for(const name of ['ArchivedMark','EmptyState','ValueIssueHint','ValueIssueCount','FieldErrorMark','FieldScanMark'])add(name,'State=Default',{words:true,title:'Документов пока нет',description:'Добавьте первый документ',icon:glyphs.Description,messages:['Значение требует проверки'],count:3,message:'Проверьте значение'});
samples.find(s=>s.component==='EmptyState')!.props.icon=icon('Description',48);
for(const open of [false,true])add('ArchivedRows',`Open=${open}`,{count:3,forceOpen:open},el('ListItem',{headline:'Архивный документ'}),420);
for(const name of ['ScopeIcon','SourceOriginIcon'])for(const scope of ['Set','Section','Construction','System'])add(name,`Scope=${scope}`,{scope,source:{kind:'manual',scope}},undefined);
for(const state of ['Initials','Empty'])add('Avatar',`State=${state}`,{name:state==='Initials'?'Александр Потоцкий':null,email:null});
add('Markdown','State=Default',{},'# Заголовок\n\n**Жирный текст**, *курсив* и [ссылка](#).\n\n- Документ\n- Строка\n\n`код`',500);
for(const state of ['Default','Breadcrumb','Actions'])add('PageHeader',`State=${state}`,{title:'Счета',subtitle:'Учёт затрат',icon:glyphs.ReceiptLong,breadcrumb:state==='Breadcrumb'?'Стройка / Документы':undefined,actions:state==='Actions'?el('Button',{variant:'filled',icon:icon()},'Добавить'):undefined},undefined,600);
for(const s of samples.filter(s=>s.component==='PageHeader'))s.props.icon=icon('ReceiptLong',24);
add('SelectionBar','State=Selected',{count:3,onClear:noop,actions:[{label:'Удалить',icon:glyphs.Delete,onClick:noop}]},undefined,600);
add('CascadeList','State=Default',{items:['Вложенные документы','Прикреплённые файлы']},undefined,420);
add('MoveButtons','State=Default',{onUp:noop,onDown:noop,canUp:true,canDown:true});
add('NoAccessPage','State=Default',{what:'разделу настроек',missing:['core.system.manage']},undefined,600);
add('RereadFailedNotice','State=Error',{what:'счёт',error:new Error('Соединение прервано'),retrying:false,onRetry:noop},undefined,600);
add('DetailTitle','State=Default',{},'Счёт № 125');
add('NavSection','State=Default',{label:'Документы'},undefined,300);
add('NavSearchInput','State=Default',{value:'',onChange:noop},undefined,300);
for(const active of [false,true])add('NavItem',`Active=${active}`,{icon:glyphs.Description,label:'Счета',count:12,active,onClick:noop},undefined,300);
for(const on of [false,true])for(const wide of [false,true])add('SectionPill',`Active=${on}, Wide=${wide}`,{icon:glyphs.Home,on,wide});
add('ShowPanelButton','State=Default',{onShow:noop});
add('OutsideBanner','State=Default',{items:[{to:'#',label:'Счета',icon:glyphs.ReceiptLong}],inside:[]},undefined,500);
add('OutsideNav','State=Default',{items:[{to:'#',label:'Счета',icon:glyphs.ReceiptLong}]},undefined,300);
add('HiddenByWorkspace','State=Default',{module:'costs',what:'Счета'},undefined,500);
const types=[{id:'act',name:'Акт освидетельствования',code:'АОСР',section:'Акты'},{id:'journal',name:'Журнал работ',code:'ЖР',section:'Журналы'}];
for(const density of ['form','dense','cell'])for(const state of ['Empty','Filled','Hover','Focus','Disabled','Error','Scan','Open']) {
  const children=[el('SelectGroup',{label:'Документы'},[el('SelectItem',{key:'a',value:'a',icon:glyphs.Description,count:12},'Акты'),el('SelectItem',{key:'b',value:'b',disabled:true,supporting:'Нет права'},'Журналы')])];
  add('Select',`Density=${density}, State=${state}`,{label:'Тип документа',density,value:state==='Empty'?undefined:'a',onValueChange:noop,placeholder:'Выберите тип',disabled:state==='Disabled',invalid:state==='Error',error:state==='Error'?'Выберите тип':undefined,fromScan:state==='Scan'},children,340,state);
  add('TypePickerField',`Density=${density}, State=${state}`,{label:'Тип документа',density,types,value:state==='Empty'?undefined:'act',onChange:noop,clearable:{label:'Без типа'},disabled:state==='Disabled'},undefined,340,state);
}
for(const variant of ['chip','inline'])for(const state of ['Empty','Filled','Disabled','Open'])add('Select',`Density=${variant}, State=${state}`,{variant,emptyItem:'Все документы',value:state==='Empty'?undefined:'a',disabled:state==='Disabled',onValueChange:noop,'aria-label':'Тип документа'},el('SelectItem',{value:'a'},'Акты'),undefined,state);
for(const name of ['PlainField','SuggestField','SuggestBox'])for(const state of ['Empty','Filled','Disabled','Open','Error'])add(name,`State=${state}`,{label:'Наименование',value:state==='Empty'?'':'Электро',onChange:noop,options:[{value:'Электромонтажные работы'},{value:'Электрооборудование'}],source:null,disabled:state==='Disabled',error:state==='Error'?'Проверьте значение':undefined,feed:{view:state==='Open'?{q:'Электро',items:[{value:'Электромонтажные работы'},{value:'Электрооборудование'}]}:null,typed:noop,openNow:noop,close:noop}},undefined,400,state);
for(const variant of ['filled','tonal','outlined','text'])for(const state of ['Default','Disabled','Loading','Open'])add('SplitButton',`Variant=${variant}, State=${state}`,{variant,onClick:noop,icon:icon(),disabled:state==='Disabled',loading:state==='Loading',menuLabel:'Другие действия',items:[{label:'Из файла',icon:glyphs.UploadFile,onClick:noop}]},'Добавить',undefined,state);
const action={id:'add',label:'Добавить документ',icon:glyphs.Add,to:'#'};
for(const busy of [false,true]){add('PrimaryActionButton',`Busy=${busy}`,{action,loading:busy});add('PrimaryFab',`Busy=${busy}`,{action,busy},undefined,400);}
for(const state of ['Default','Dirty','Saving'])add('DetailHeader',`State=${state}`,{heading:el('DetailTitle',{},'Счёт № 125'),dirty:state==='Dirty',saving:state==='Saving',onSaveAll:async()=>{},onRevert:noop},undefined,600);
const columns=[{key:'name',label:'Наименование',rowHeader:true},{key:'quantity',label:'Количество',align:'right'},{key:'sum',label:'Сумма',align:'right'}];
const rows=[{id:'a',name:'Кабель ВВГнг',quantity:120,sum:'24 000,00'},{id:'b',name:'Светильник',quantity:8,sum:'16 000,00'}];
for(const density of ['regular','compact'])for(const state of ['Default','Empty','Sorted','Unavailable','Warning'])add('DataGrid',`Density=${density}, State=${state}`,{columns:state==='Unavailable'?columns.map((c,i)=>i===2?{...c,unavailable:'no-right'}:c):columns,rows:state==='Empty'?[]:rows,density,rowKey:(r:{id:string})=>r.id,sort:state==='Sorted'?[{column:'sum',descending:true}]:undefined,onSort:noop,tone:state==='Warning'?()=> 'warning':undefined},undefined,640);
for(const state of ['Default','Empty','Unavailable'])add('DataGridValue',`State=${state}`,{row:state==='Empty'?{}:rows[0],column:state==='Unavailable'?{...columns[0],unavailable:'removed'}:columns[0]});
add('EditGrid','State=Default',{columns:columns.map(c=>({...c,cell:(row:Record<string,unknown>)=>el('TextField',{label:c.label,density:'cell',value:String(row[c.key]),onChange:noop})})),rows,rowKey:(r:{id:string})=>r.id},undefined,640);
for(const state of ['Default','Empty','Pending','Missing'])add('EmployeePicker',`State=${state}`,{candidates:state==='Empty'?[]:[{employeeId:'a',name:'Иван Петров',personnelNumber:'125',eligible:true},{employeeId:'b',name:'Анна Смирнова',personnelNumber:'126',eligible:false,reason:'Уже назначена'}],selected:new Set(['a']),search:'',onSearch:noop,onChange:noop,pending:state==='Pending',missing:state==='Missing'?'Нет доступа к сотрудникам':null},undefined,700);
for(const state of ['Default','Empty'])add('Timeline',`State=${state}`,{today:'2026-11-21',rows:state==='Empty'?[]:['primary','secondary','outline','muted'].map((tone,i)=>({key:String(i),label:['Кабельные трассы','Щиты','Светильники','Проверка'][i],from:'2026-11-16',to:'2026-12-05',tone})),nameLabel:'Работы','aria-label':'Периоды работ'},undefined,900);
const body=el('TextField',{label:'Наименование',value:'Электромонтажные работы',onChange:noop});
const footer=el('ModalButtons',{},[el('ModalExit',{key:'exit'}),el('ModalPrimary',{key:'save',onClick:noop},'Сохранить')]);
for(const state of ['Default','Wide','Busy','Dirty','Acknowledge'])add('Modal',`State=${state}`,{open:true,onOpenChange:noop,title:'Редактирование документа',wide:state==='Wide',busy:state==='Busy',isDirty:state==='Dirty',acknowledge:state==='Acknowledge'?'Понятно':undefined,footer:state==='Acknowledge'?undefined:footer},body,800);
for(const name of ['ConfirmDialog','LeaveGuardDialog','LeaveQuestion','ChangePasswordModal','BugReportDialog','SendMessageDialog','ShortcutsHelp'])add(name,'State=Open',{open:true,prefill:{},onOpenChange:noop,onClose:noop,onConfirm:noop,onSave:noop,onDiscard:noop,onCancel:noop,onStay:noop,onLeave:noop,title:'Подтверждение действия',description:'Проверьте выбранные документы',confirmLabel:'Удалить',confirmDanger:true,requireCheckbox:name==='ConfirmDialog'?'Удалить связанные записи':undefined,candidates:[{id:'a',displayName:'Иван Петров',email:'ivan@example.test'}]},undefined,800);
for(const name of ['BottomSheet','SideSheet','SectionPanelDrawer'])add(name,'State=Open',{open:true,onClose:noop,title:'Документы',label:'Документы'},el('ListItem',{headline:'Акт освидетельствования'}),600);
add('TypePicker','State=Open',{open:true,onOpenChange:noop,types,onSelect:noop,noneOption:{label:'Без типа'},onSelectNone:noop},undefined,800);
for(const state of ['Default','Dirty','Busy'])add('WindowStep',`State=${state}`,{host:'dialog',title:'Следующий шаг',busy:state==='Busy',isDirty:state==='Dirty',onBack:noop,footer},body,600);
for(const name of ['ModalPrimary','ModalExit','ModalAction'])for(const state of ['Default','Disabled','Loading'])add(name,`State=${state}`,{onClick:noop,disabled:state==='Disabled',loading:state==='Loading'},name==='ModalExit'?'Отмена':'Сохранить');
add('ModalButtons','State=Default',{},[el('Button',{key:'exit'},'Отмена'),el('Button',{key:'save',variant:'filled'},'Сохранить')],500);
add('ModalNote','State=Default',{},'Изменения будут сохранены',400);
add('ModalBanner','State=Error',{tone:'error'},'Не удалось сохранить изменения',500);
add('ModalAppBar','State=Default',{title:'Редактирование документа',back:false,busy:false,onExit:noop,primaryRef:{current:null},noteRef:{current:null},actions:[]},undefined,500);
add('OutlinedField','State=Default',{label:'Наименование',raise:'always',density:'form'},el('input',{value:'Документ',readOnly:true}),340);
add('SegmentProblemMark','State=Error',{id:'error',problem:'Некорректная дата',showIcon:true});
add('TabPanel','State=Default',{id:'tabs',value:'a'},'Содержимое вкладки',400);
for(const state of ['loading','chunk','old-browser','not-pdf','password','no-pages','worker','broken'])add('ViewerMessage',`State=${state}`,{state,source:null},undefined,560);
for(const narrow of [false,true])add('ViewerToolbar',`Narrow=${narrow}`,{numPages:12,page:3,onPage:noop,mode:'width',onMode:noop,onZoomIn:noop,onZoomOut:noop,canZoomIn:true,canZoomOut:true,onRotate:noop,narrow,scale:1,loupe:null},undefined,narrow?360:720);
add('ViewerTitleBar','State=Default',{title:'Счёт № 125.pdf',source:null,onClose:noop},undefined,600);
add('PdfViewer','State=Loading',{source:null,documentKey:'example',title:'Счёт № 125.pdf'},undefined,700);
add('PdfViewerOverlay','State=Open',{open:true,onOpenChange:noop,source:null,documentKey:'example',title:'Счёт № 125.pdf'},undefined,900);
add('CodeEditor','State=Default',{value:'#set text(lang: "ru")\n= Акт освидетельствования',onChange:noop,language:'typst',height:'240px'},undefined,650);
add('BrandLogo','State=Default',{iconSize:40});
add('ThemeToggle','State=Default',{label:'Оформление'},undefined,300);
add('AccountMenu','State=Default',{},undefined,280);
add('WorkspaceSwitcher','State=Default',{},undefined,320);
const navItems=[{to:'/home',label:'Главная',icon:glyphs.Home},{to:'/costs/invoices',label:'Счета',icon:glyphs.ReceiptLong}];
const sections=[{def:{id:'home',label:'Главная',icon:glyphs.Home},home:'/home',items:[navItems[0]],groups:[{items:[navItems[0]]}]},{def:{id:'costs',label:'Затраты',icon:glyphs.ReceiptLong},home:'/costs/invoices',items:[navItems[1]],groups:[{items:[navItems[1]]}]}];
add('SectionRail','State=Default',{sections,active:'costs',panelHidden:false,onShowPanel:noop},undefined,100);
add('BottomNav','State=Default',{sections,active:'home'},undefined,400);
add('PageList','State=Default',{groups:[{label:'Документы',items:navItems}],pinnable:()=>true},undefined,320);
add('SectionPanel','State=Default',{def:sections[1].def,groups:[{label:'Документы',items:navItems}],outside:[],pinned:[],pinnable:()=>true,onHide:noop},undefined,320);
const entries=[{item:{to:'/costs/invoices',label:'Счета',icon:glyphs.ReceiptLong},missing:false}];
add('PinnedGroup','State=Default',{entries},undefined,320);
add('PinnedLink','State=Default',{entry:entries[0]},undefined,320);
add('PageRow','State=Default',{actions:[{label:'Закрепить',icon:glyphs.Keep,onClick:noop}],label:'Счета'},'Счета',320);
add('ListDetailShell','State=Default',{title:'Документы',nav:el('NavItem',{label:'Акты',onClick:noop}),detail:el('Card',{title:'Акт № 125'},'Реквизиты документа')},undefined,1000);
for(const state of ['Default','Open'])add('RowActionsMenu',`State=${state}`,{actions:[{label:'Открыть',icon:glyphs.OpenInNew,onClick:noop},{label:'Удалить',icon:glyphs.Delete,onClick:noop,danger:true}]},undefined,300,state);
const menuBody=[el('MenuLabel',{key:'label'},'Действия'),el('MenuItem',{key:'normal',icon:glyphs.Edit,trailing:'Ctrl+E'},'Редактировать'),el('MenuItem',{key:'off',disabled:true,description:'Нет права'},'Недоступное действие'),el('MenuCheckboxItem',{key:'check',checked:true},'Показывать архив'),el('MenuRadioGroup',{key:'radio',value:'a'},[el('MenuRadioItem',{key:'a',value:'a'},'По дате'),el('MenuRadioItem',{key:'b',value:'b'},'По имени')]),el('MenuSeparator',{key:'separator'}),el('MenuItem',{key:'danger',icon:glyphs.Delete,danger:true},'Удалить'),el('MenuNote',{key:'note'},'Действия над выбранной записью')];
for(const dense of [false,true])add('Menu',`Dense=${dense}`,{trigger:el('Button',{},'Действия'),open:true,dense},menuBody,500);
add('Tooltip','State=Open',{content:'Редактировать документ',open:true},el('Button',{},'Редактировать'),400);
add('RichTooltip','State=Default',{title:'Документ',text:'Откройте документ для проверки реквизитов',actions:[{label:'Подробнее',onClick:noop}]},el('Button',{},'Документ'),400,'Hover');
function SnackbarSample({variant,action}:{variant:string;action:boolean}){const toast=useSnackbar();React.useEffect(()=>{toast[variant as 'info'|'success'|'error']('Изменения сохранены',{duration:60000,action:action?{label:'Отменить',onClick:noop}:undefined});},[toast,variant,action]);return null;}
registry.Snackbar=SnackbarSample;
for(const variant of ['info','success','error'])for(const action of [false,true])add('Snackbar',`Variant=${variant}, Action=${action}`,{variant,action},undefined,600);
add('CommandPalette','State=Open',{open:true,onOpenChange:noop},undefined,1000);
add('AppShell','State=Desktop',{},undefined,1200);
for(const state of ['Default','Disabled','Badge','Icons'])add('Tabs',`Variant=primary, State=${state}`,{id:'primary',variant:'primary',items:[{value:'a',label:'Редакции',count:state==='Badge'?3:undefined,icon:state==='Icons'?glyphs.HistoryIcon:undefined},{value:'b',label:'Приложения',disabled:state==='Disabled',icon:state==='Icons'?glyphs.AttachFile:undefined}],value:'a',onChange:noop},undefined,420);
for(const name of ['DateField','DateInput'])for(const precision of ['month','year'])add(name,`Precision=${precision}, State=Filled`,{label:'Дата документа',value:'2026-11-21',onChange:noop,precision,density:name==='DateInput'?'dense':'form'},undefined,340);
for(const state of ['Ready','Over','Rejected','Phone'])add('FileDropZone',`State=${state}`,{label:'Файл счёта',hint:'PDF, PNG и JPEG · до 50 МБ',onFile:noop},undefined,450,state);
for(const state of ['First','Last','Busy'])add('MoveButtons',`State=${state}`,{onUp:noop,onDown:noop,isFirst:state==='First',isLast:state==='Last',busy:state==='Busy'});
for(const variant of ['standard','filled','tonal','outlined'])add('IconButton',`Variant=${variant}, State=Dense`,{variant,icon:glyphs.Star,label:'Избранное'},undefined,undefined,'Dense');
add('RadioGroup','Orientation=vertical, State=Confirm',{label:'Проверка',confirm:true,items:[{value:'a',label:'Подтверждаю данные'},{value:'b',label:'Требуется исправление'}],value:'a',onChange:noop},undefined,400);
for(const state of ['Checked','WithActions','Depth'])add('ListItem',`State=${state}`,{headline:'Акт освидетельствования',checked:state==='Checked',depth:state==='Depth'?2:0,leadingAction:state==='WithActions'?el('Checkbox',{checked:true,onChange:noop}):undefined,action:state==='WithActions'?el('IconButton',{icon:glyphs.MoreVert,label:'Действия'}):undefined},undefined,450);
registry.Popover=()=>el('Root',{open:true,onOpenChange:noop},[el('Trigger',{key:'trigger',asChild:true},el('Button',{},'Сведения')),el('Portal',{key:'portal'},el('Content',{className:'p-4 bg-surface-container text-on-surface rounded-xs shadow-elevation-2'},['Дополнительные сведения',el('Close',{key:'close'},'Закрыть'),el('Arrow',{key:'arrow'})]))]);
add('Popover','State=Open',{},undefined,600);
add('Menu','Dense=false, State=Submenu',{open:true,trigger:el('Button',{},'Экспорт')},el('MenuSub',{label:'Экспорт',icon:glyphs.Download},el('MenuItem',{},'Сохранить файл')),600,'Submenu');
add('AccountMenu','State=Open',{},undefined,300,'Open');
add('WorkspaceSwitcher','State=Open',{},undefined,350,'Open');
function documentPdf(){
  const stream='BT /F1 20 Tf 50 740 Td (BHS.CRG - DOCUMENT) Tj 0 -40 Td /F1 12 Tf (Invoice 125 - 21.11.2026) Tj 0 -30 Td (Cable installation works) Tj ET';
  const objects=['<< /Type /Catalog /Pages 2 0 R >>','<< /Type /Pages /Kids [3 0 R] /Count 1 >>','<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>','<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>','<< /Length '+stream.length+' >>\nstream\n'+stream+'\nendstream'];
  let result='%PDF-1.4\n';const offsets=[0];objects.forEach((o,i)=>{offsets.push(result.length);result+=(i+1)+' 0 obj\n'+o+'\nendobj\n';});const xref=result.length;result+='xref\n0 6\n0000000000 65535 f \n';for(const offset of offsets.slice(1))result+=String(offset).padStart(10,'0')+' 00000 n \n';result+='trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n'+xref+'\n%%EOF';return new TextEncoder().encode(result);
}
const pdf=documentPdf();
for(const dimmed of [false,true])add('PdfViewer',`State=Loaded, Dimmed=${dimmed}`,{source:pdf,documentKey:'fixture',title:'Счёт № 125.pdf',dimmed},undefined,700);
const imageFixture=new Blob(['<svg xmlns="http://www.w3.org/2000/svg" width="595" height="842"><rect width="595" height="842" fill="white"/><text x="50" y="80" font-family="Arial" font-size="24">BHS.CRG</text><text x="50" y="120" font-family="Arial" font-size="16">Invoice 125</text><path d="M50 150H545M50 200H545M50 250H545" stroke="#aaa"/></svg>'],{type:'image/svg+xml'});
for(const dimmed of [false,true])add('ImageView',`State=Loaded, Dimmed=${dimmed}`,{source:imageFixture,documentKey:'image',title:'Счёт № 125',dimmed},undefined,700);
add('ImageView','State=Magnifier, Dimmed=false',{source:imageFixture,documentKey:'image',title:'Счёт № 125',dimmed:false},undefined,700,'Magnifier');
for(const origin of ['Parsed','Recognized','System'])for(const stale of [false,true])add('SourceOriginIcon',`Origin=${origin}, Stale=${stale}`,{origin,stale,plural:false});
for(const variant of ['outlined','filled','elevated'])for(const state of ['Flush','Actions','Icon'])add('Card',`Variant=${variant}, State=${state}`,{variant,title:'Документы стройки',flush:state==='Flush',actions:state==='Actions'?el('Button',{variant:'tonal'},'Открыть'):undefined,icon:state==='Icon'?glyphs.Warning:undefined,iconTone:state==='Icon'?'warning':undefined},'Комплект исполнительной документации',360);
for(const checked of [false,true])add('Checkbox',`Checked=${checked}, State=Filled`,{checked,onChange:noop,label:'Выбрать строку',filled:true});
for(const variant of ['standard','modal'])add('SideSheet',`Variant=${variant}, State=Open`,{variant,open:true,onClose:noop,title:'Документы'},el('ListItem',{headline:'Акт освидетельствования'}),600);
for(const state of ['no-data','filtered-out','module-off'])add('DataGrid',`Density=regular, State=${state}`,{columns,rows:[],state},undefined,640);
for(const tone of ['warning','success','info'])add('ModalBanner',`State=${tone}`,{tone},'Результат обработки документа',500);
for(const precision of ['month','year'])add('DateRange',`State=${precision}`,{label:'Период',from:'2026-01-01',to:'2026-11-01',onChange:noop,precision},undefined,600);
for(const inset of ['start','both'])add('Divider',`Orientation=horizontal, Inset=${inset}`,{orientation:'horizontal',inset,className:'w-80'});
for(const name of ['PlainField','SuggestField','SuggestBox'])for(const density of ['dense','cell'])add(name,`State=${density}`,{label:'Наименование',value:'Электро',onChange:noop,density,options:[{value:'Электромонтажные работы'}],source:null,feed:{view:null,typed:noop,openNow:noop,close:noop}},undefined,400);
for(const form of ['menu'])for(const state of ['Default','Selected','Disabled'])add('ListOption',`State=${form}-${state}`,{form,id:'row',headline:'Акт освидетельствования',active:state==='Selected',disabled:state==='Disabled',onChoose:noop},undefined,400);
for(const name of ['Tooltip','RichTooltip'])for(const side of ['top','right','bottom','left'])add(name,`State=${side}`,{side,content:'Редактировать документ',open:true,title:'Документ',text:'Откройте документ для проверки реквизитов'},el('Button',{},'Документ'),400,name==='RichTooltip'?'Hover':undefined);
for(const name of ['ModalPrimary','ModalExit','ModalAction'])for(const variant of name==='ModalAction'?['tonal','outlined']:name==='ModalPrimary'?['tonal','outlined','text']:['tonal','outlined','filled'])add(name,`State=${variant}`,{variant,onClick:noop},'Продолжить');
for(const density of ['form','dense','cell'])add('FieldErrorMark',`State=${density}`,{density,message:'Проверьте значение'});
for(const density of ['form','dense','cell'])for(const raise of ['auto','always'])add('OutlinedField',`State=${density}-${raise}`,{label:'Наименование',density,raise},el('input',{value:raise==='auto'?'':'Документ',readOnly:true,className:density==='form'?'h-14 px-4 w-full bg-transparent outline-none':'h-10 px-4 w-full bg-transparent outline-none'}),340);
for(const staleReason of ['FileReplaced','NotParsedAgainstNewFile','TableBoundariesChanged','ProfileChanged'])add('SourceOriginIcon',`Reason=${staleReason}`,{origin:'Recognized',stale:true,staleReason});
add('ListDetailShell','State=NarrowNavigation',{title:'Документы',navWidth:'w-64',nav:el('NavItem',{label:'Акты',onClick:noop}),detail:el('Card',{title:'Акт № 125'},'Реквизиты документа')},undefined,1000);
for(const mode of ['page',1])add('ViewerToolbar',`Narrow=false, Mode=${mode}`,{numPages:12,page:3,onPage:noop,mode,onMode:noop,onZoomIn:noop,onZoomOut:noop,canZoomIn:true,canZoomOut:true,onRotate:noop,narrow:false,scale:1,loupe:{on:false,available:true,toggle:noop}},undefined,720);
add('EmptyState','State=Action',{title:'Документов пока нет',description:'Добавьте первый документ',icon:icon('Description',48),action:el('Button',{variant:'filled'},'Добавить')},undefined,500);
for(const size of [18,20,24,40,48])for(const filled of [false,true])add('Icon',`Size=${size}, Filled=${filled}`,{icon:glyphs.Star,size,filled});
add('FieldScanMark','State=Open',{open:true});
for(const tone of ['error','warning']){
  add('Chip',`Selected=false, State=${tone}`,{icon:glyphs.Warning,iconTone:tone,iconLabel:'Требует проверки',onClick:noop},'Проверить');
  add('Select',`Density=chip, State=${tone}`,{variant:'chip',value:'a',onValueChange:noop,emptyItem:'Все документы',icon:glyphs.Warning,iconTone:tone,iconLabel:'Требует проверки','aria-label':'Тип документа'},el('SelectItem',{value:'a',icon:glyphs.Warning,iconTone:tone},'Акты'),undefined,'Open');
}
for(const variant of ['outlined','filled','elevated'])add('Card',`Variant=${variant}, State=ErrorIcon`,{variant,title:'Документы стройки',icon:glyphs.ErrorIcon,iconTone:'error'},'Проверьте реквизиты',360);
const completeSections=railSections.map(def=>{const items=[...workNav,...settingsNav].filter(item=>item.rail===def.id);return {def,home:items[0]?.to??'/home',items,groups:[{items}]};});
for(const section of completeSections)add('SectionRail',`State=${section.def.id}`,{sections:completeSections,active:section.def.id,panelHidden:false,onShowPanel:noop},undefined,100);
for(const active of ['data','home','constructions','costs','plan','work','system'])add('BottomNav',`State=${active}`,{sections:completeSections,active},undefined,400);
function TooltipLayerExample({side,align}:{side:string;align:string}){const ref=React.useRef<HTMLButtonElement>(null);return <>{el('Button',{ref},'Сведения')}{el('TooltipLayer',{anchor:ref,side,align,onClose:noop,className:'p-3 bg-inverse-surface text-inverse-on-surface rounded-xs shadow-elevation-2'},'Дополнительные сведения')}</>;}
registry.TooltipLayerExample=TooltipLayerExample;
for(const side of ['top','right','bottom','left'])for(const align of ['start','center','end'])add('TooltipLayerExample',`Side=${side}, Align=${align}`,{side,align},undefined,400);
function ThrowExample():never{throw new Error('Пример ошибки отображения документа');}
registry.ErrorBoundaryExample=({variant}:{variant:string})=>el('ErrorBoundary',{variant,allowReload:true,onClose:noop},<ThrowExample/>);
for(const variant of ['inline','page'])add('ErrorBoundaryExample',`Variant=${variant}, State=Error`,{variant},undefined,700);
add('ErrorBoundaryExample','Variant=inline, State=Expanded',{variant:'inline'},undefined,700,'Expanded');
for(const scope of ['None','Source','File'])add('StaleSourceAction',`Scope=${scope}`,{source:{id:'fixture',recognitionStale:true,recognizeScope:scope,staleReason:'ProfileChanged',bindingCount:2}},undefined,500);
add('StaleSourceAction','Scope=Source, State=Confirm',{source:{id:'fixture',recognitionStale:true,recognizeScope:'Source',staleReason:'ProfileChanged',bindingCount:2}},undefined,600,'Confirm');
samples.find(s=>s.component==='OutsideBanner')!.props={items:[{to:'/costs/invoices',label:'Счета',module:'costs'}],inside:[{to:'/home',label:'Главная'}]};
for(const s of samples.filter(s=>s.component==='Card'))if(!['Static','Interactive','Hover','Focus'].some(state=>s.variant.endsWith('State='+state)))s.props.onClick=undefined;

return normalizeStories(samples);
}
