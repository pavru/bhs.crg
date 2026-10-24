# Эталоны разбора ГРАНД-Сметы — контрольные точки по ПЕЧАТНЫМ ФОРМАМ программы (plan-05, вариант Г).
#
# Форма — собственное прочтение файла программой, от разбора она не зависит; поэтому эталон снимается
# с неё, а не с Data.xml образца: снятый с XML эталон повторил бы ошибки разбора. Скрипт XML не
# открывает вовсе. Формы — данные заказчика, в репозитории их нет:
#   E:\Development\Office\BHS.CRG-research\testdata\grand-smeta\forms\<образец> - <форма>.xlsx
#
# Запуск (нужен openpyxl):
#   python -X utf8 etalons_from_forms.py <каталог форм>
# Пишет <образец>.etalon.json рядом с собой. Руками эталоны не правят: расхождение с разбором
# разбирают, а не подгоняют. Числа — строками, ровно как в ячейке формы (без двоичной погрешности).
import json
import os
import re
import sys
from decimal import Decimal

import openpyxl

HERE = os.path.dirname(os.path.abspath(__file__))
FORM_NOTE = ("Контрольные точки сняты скриптом etalons_from_forms.py с печатной формы программы, "
             "не с XML образца (plan-05, вариант Г). Руками не править.")


def num(v):
    """Число ячейки строкой: '11,268' и 1.1e-05 — одинаково, без двоичной погрешности."""
    if v is None or (isinstance(v, str) and v.strip() == ''):
        return None
    if isinstance(v, (int, float)):
        d = Decimal(repr(v)) if isinstance(v, float) else Decimal(v)
    else:
        d = Decimal(v.strip().replace(' ', '').replace(',', '.'))
    return format(d.normalize(), 'f')


def text(v):
    return None if v is None else str(v).strip() or None


def rows_of(path, last_col):
    ws = openpyxl.load_workbook(path, data_only=True).worksheets[0]
    for r in ws.iter_rows(max_col=last_col):
        yield r[0].row, {c.column_letter: c.value for c in r
                         if c.value is not None and str(c.value).strip() != ''}


def chapter_caption(a):
    """«Раздел 1. Каркас» → «Каркас»: номер раздела форма приписывает сама — снимается один раз."""
    return re.sub(r'^Раздел\s+\d+\.\s*', '', a.strip(), count=1)


def spread(items, n=4):
    """Первая, последняя и равномерно между ними — до n штук, в исходном порядке."""
    if len(items) <= n:
        return list(items)
    idx = sorted({round(i * (len(items) - 1) / (n - 1)) for i in range(n)})
    return [items[i] for i in idx]


POS_NUMBER = re.compile(r'^\d+(\.\d+)*$')
# «Раздел N.» — не просто «Раздел …»: примечание раздела ВОР бывает «Раздел только для договора …»
CHAPTER = re.compile(r'^Раздел\s+\d+\.')
QUARTERS = {'I': 1, 'II': 2, 'III': 3, 'IV': 4}


# ---------------------------------------------------------------- ЛСР
def lsr(path):
    rows = list(rows_of(path, 14))
    e = {'kind': 'LocalEstimate', 'requisites': {}}
    prev_a = None
    table = False
    chapters, cur_ch, pos = [], None, None
    doc_mode = False
    after_estimate_total = False
    for rn, c in rows:
        a, b, cc = text(c.get('A')), text(c.get('B')), text(c.get('C'))
        if not table:
            if a and a.startswith('ЛОКАЛЬНЫЙ СМЕТНЫЙ РАСЧЕТ (СМЕТА) №'):
                e['requisites']['number'] = a.split('№', 1)[1].strip()
                e['_await_name'] = True
            elif a and e.pop('_await_name', False):
                e['requisites']['name'] = a
            elif a == '(наименование стройки)':
                e['requisites']['construction'] = prev_a
            elif a == '(наименование объекта капитального строительства)':
                e['requisites']['object'] = prev_a
            elif a == 'Составлен' and b:  # «Составлен » — пробел снят text()
                e['method'] = {'базисно-индексным': 'Bim', 'ресурсно-индексным': 'Rim',
                               'ресурсным': 'Rm'}[b]
            elif a and a.startswith('Составлен(а) в текущем'):
                m = re.match(r'^(I{1,3}|IV) квартал (\d{4})', text(c.get('D')))
                e['priceQuarter'] = {'year': int(m.group(2)), 'quarter': QUARTERS[m.group(1)]}
            elif a == '1' and b == '2' and cc == '3':
                table = True
            if a and not a.startswith('('):
                prev_a = a
            continue

        if a and CHAPTER.match(a):
            cur_ch = {'caption': chapter_caption(a), 'positions': [], 'totalBase': None}
            chapters.append(cur_ch)
            continue
        first = a.split('\n')[0].strip() if a else None
        if first and POS_NUMBER.match(first):
            pos = {'chapter': len(chapters), 'number': first, 'code': b, 'caption': cc,
                   'units': text(c.get('F')), 'quantity': num(c.get('I')),
                   'coefficients': [], 'materials': [], 'labor': None}
            if b and b.startswith('ТЦ_'):
                # ТЦ_<КСР>_<регион>_<ИНН>_<дата>_… — код строки по КА (421/пр); цена — графа 8 формы
                parts = b.split('_')
                pos['marketAnalysis'] = {'price': num(c.get('J')), 'inn': parts[3], 'date': parts[4]}
            cur_ch['positions'].append(pos)
            continue
        if pos is not None:
            if cc == 'Всего по позиции':
                # нулевую сумму форма не печатает (ЛСР ЭОМ, 32 «Вспомогательная позиция»)
                pos['totalBase'] = num(c.get('L')) or '0'
                pos['totalCurrent'] = num(c.get('N')) or '0'
                pos = None
            elif cc == 'ЗТ':
                pos['labor'] = num(c.get('I'))
            elif b and not c.get('F') and not c.get('L') and not c.get('N') and '/' in b \
                    and not b.startswith('Пр/'):
                pos['coefficients'].append(b)
            elif b and c.get('F') and c.get('F') not in ('%', 'чел.-ч', 'маш.-ч') \
                    and b not in ('1', '2', '3', '4'):
                pos['materials'].append({'code': b, 'caption': cc, 'units': text(c.get('F')),
                                         'quantity': num(c.get('I'))})
            continue
        line = cc.strip() if cc else None
        if line and line.startswith('Всего по разделу'):
            cur_ch['totalBase'] = num(c.get('L'))
        elif line == 'Итоги по смете:':
            doc_mode = True
        elif doc_mode and line == 'Всего' and 'total' not in e:
            e['total'] = {'base': num(c.get('L')), 'current': num(c.get('N'))}
        elif doc_mode and line == 'ВСЕГО по смете':
            e['estimateTotal'] = {'base': num(c.get('L')), 'current': num(c.get('N'))}
            after_estimate_total = True
        elif line == 'Затраты труда рабочих' and not b:
            if after_estimate_total:
                e['laborTotal'] = num(c.get('I'))
            elif not doc_mode:
                cur_ch['labor'] = num(c.get('I'))

    picked = []
    for cls in ('rate', 'material', 'ka', 'other'):
        group = [p for ch in chapters for p in ch['positions'] if classify(p['code']) == cls]
        picked += spread(group)
    e['counts'] = {'chapters': len(chapters),
                   'printedPositions': sum(len(ch['positions']) for ch in chapters)}
    e['chapters'] = [{'caption': ch['caption'], 'printedPositions': len(ch['positions']),
                      'totalBase': ch['totalBase'],
                      # Итог раздела в текущих ценах форма не печатает — сумма «Всего по позиции» её же
                      'positionsTotalCurrent': format(sum(Decimal(p['totalCurrent'])
                                                          for p in ch['positions']), 'f'),
                      'labor': ch.get('labor')} for ch in chapters]
    e['positions'] = sorted(picked, key=lambda p: (p['chapter'], order_key(p['number'])))
    return e


def classify(code):
    if not code:
        return 'other'  # строка без обоснования бывает: ЛСР ЭОМ, 23.7
    if code.startswith('ТЦ_'):
        return 'ka'
    if code.startswith(('ФСБЦ', 'ФССЦ')):
        return 'material'
    if re.match(r'^(ФЕР|ГЭСН|ТЕР)', code):
        return 'rate'
    return 'other'


def order_key(number):
    return [int(x) for x in number.split('.')]


# ---------------------------------------------------------------- КА
KA_COLUMNS = [  # подпись графы в шапке → поле; раскладок у формы две, графы ищутся по подписи
    ('number', lambda h: h in ('Номер по порядку', '№ п.п.')),
    ('code', lambda h: h.startswith('Код ')),
    ('caption', lambda h: h.startswith('Наименование ресурса, затрат')),
    ('offerCaption', lambda h: h.startswith('Полное наименование ресурса')),
    ('units', lambda h: h.startswith('Единица измерения ресурса, затрат') and 'обосновывающем' not in h),
    ('priceWithVat', lambda h: 'в обосновывающем документе с ' in h),
    ('priceWithoutVat', lambda h: 'в обосновывающем документе без' in h),
    ('month', lambda h: h.startswith('Месяц и год')),
    ('year', lambda h: h == 'Год'),
    ('quarter', lambda h: h == 'Квартал'),
    ('supplier', lambda h: h.startswith('Полное и (или) сокращенное')),
    ('country', lambda h: h.startswith('Страна')),
    ('kpp', lambda h: h.startswith('КПП')),
    ('inn', lambda h: h.startswith('ИНН')),
]


def ka(path):
    rows = list(rows_of(path, 30))
    e = {'kind': 'Conjuncture', 'requisites': {}}
    cols, chapters, positions, prev = None, [], {}, None
    for rn, c in rows:
        a = text(c.get('A'))
        if cols is None:
            if a and a.startswith('(наименование объекта'):
                e['requisites']['object'] = prev
            if a in ('Номер по порядку', '№ п.п.'):
                cols = {}
                for col, v in c.items():
                    for field, match in KA_COLUMNS:
                        if field not in cols and match(str(v).strip()):
                            cols[field] = col
            if a:
                prev = a
            continue
        if a and CHAPTER.match(a):
            chapters.append({'caption': chapter_caption(a), 'positions': []})
            continue
        if not a or not re.match(r'^\d+\.\d+$', a):
            continue
        pn, on = a.split('.')
        g = lambda f: c.get(cols[f]) if f in cols else None
        key = (len(chapters), pn)
        if key not in positions:
            positions[key] = {'chapter': len(chapters), 'number': pn, 'caption': text(g('caption')),
                              'units': text(g('units')), 'offers': []}
            chapters[-1]['positions'].append(positions[key])
        period = ({'month': text(g('month'))} if 'month' in cols
                  else {'year': int(g('year')), 'quarter': int(g('quarter'))})
        positions[key]['offers'].append({
            'number': on, 'code': text(g('code')), 'caption': text(g('offerCaption')),
            'priceWithVat': num(g('priceWithVat')), 'priceWithoutVat': num(g('priceWithoutVat')),
            'supplier': text(g('supplier')), 'inn': text(g('inn')), 'kpp': text(g('kpp')),
            'country': text(g('country')), **period})
    allp = [p for ch in chapters for p in ch['positions']]
    for p in allp:
        p['minPriceWithoutVat'] = format(min(Decimal(o['priceWithoutVat']) for o in p['offers']), 'f')
    offers = [o for p in allp for o in p['offers']]
    e['counts'] = {'chapters': len(chapters), 'positions': len(allp), 'offers': len(offers),
                   'suppliers': len({(o['supplier'], o['inn']) for o in offers})}
    e['chapters'] = [{'caption': ch['caption'], 'positions': len(ch['positions'])} for ch in chapters]
    most = max(allp, key=lambda p: len(p['offers']))
    picked = spread(allp, 5)
    if most not in picked:
        picked.append(most)
    e['positions'] = sorted(picked, key=lambda p: (p['chapter'], allp.index(p)))
    return e


# ---------------------------------------------------------------- ВОР
VOR_REQUISITES = {'Наименование стройки': 'construction',
                  'Наименование объекта капитального строительства': 'object',
                  'Ведомость объемов работ №': 'number', 'Дата составления': 'date'}


def vor(path):
    rows = list(rows_of(path, 9))
    e = {'kind': 'VolumeStatement', 'requisites': {}}
    table, chapters, pos = False, [], None
    for rn, c in rows:
        a = text(c.get('A'))
        if not table:
            if a in VOR_REQUISITES and text(c.get('C')):
                e['requisites'][VOR_REQUISITES[a]] = text(c.get('C'))
            if a == '1' and text(c.get('B')) == '2':
                table = True
            continue
        if a and CHAPTER.match(a):
            chapters.append({'caption': chapter_caption(a), 'notes': [], 'positions': []})
            pos = None
            continue
        if a and POS_NUMBER.match(a):
            pos = {'chapter': len(chapters), 'number': a, 'caption': text(c.get('B')),
                   'units': text(c.get('C')), 'quantity': num(c.get('D')),
                   'formula': text(c.get('E')), 'comment': text(c.get('I')),
                   'references': [], 'files': [], 'pages': []}
            chapters[-1]['positions'].append(pos)
        elif a:
            # заголовок или примечание раздела — строка без номера
            chapters[-1]['notes'].append(a)
            pos = None
            continue
        if pos is not None:
            for col, field in (('F', 'references'), ('G', 'files'), ('H', 'pages')):
                v = text(c.get(col))
                if v and v not in pos[field]:
                    pos[field].append(v)
    allp = [p for ch in chapters for p in ch['positions']]
    e['counts'] = {'chapters': len(chapters), 'positions': len(allp)}
    e['chapters'] = [{'caption': ch['caption'], 'positions': len(ch['positions']),
                      'notes': ch['notes']} for ch in chapters]
    picked = spread(allp, 5)
    for want in (lambda p: p['formula'] and '\n' in p['formula'], lambda p: p['files'],
                 lambda p: p['comment']):
        hit = next((p for p in allp if want(p)), None)
        if hit and hit not in picked:
            picked.append(hit)
    e['positions'] = sorted(picked, key=allp.index)
    return e


READERS = {'lsr': lsr, 'ka': ka, 'vor': vor}


def main(forms_dir):
    for fn in sorted(os.listdir(forms_dir)):
        if not fn.endswith('.xlsx'):
            continue
        sample, form = fn[:-5].split(' - ', 1)
        if not os.path.exists(os.path.join(HERE, sample + '.gsfx')):
            continue
        e = {'$comment': FORM_NOTE, 'sample': sample + '.gsfx', 'form': form,
             # версия программы формой не печатается — она из описи README (имя образца)
             'programVersion': sample.split('-')[1]}
        e.update(READERS[sample.split('-')[0]](os.path.join(forms_dir, fn)))
        with open(os.path.join(HERE, sample + '.etalon.json'), 'w', encoding='utf-8', newline='\n') as f:
            json.dump(e, f, ensure_ascii=False, indent=2)
            f.write('\n')
        print(sample, e['counts'])


if __name__ == '__main__':
    main(sys.argv[1])
