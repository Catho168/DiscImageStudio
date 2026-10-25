"""Independent periodic 17PP reference, transcribed from ISO/IEC 30190:2021 Fig. 41.

Q means data entering 17PP, no DC insertions, ECC, BIS, or sync in this model.
This is a finite-state cycle analysis, so reported densities are exact ratios.
"""
import collections
import argparse
import csv
import fractions
import json
from pathlib import Path

TABLE = (
    ('00000000', '010100100100'),
    ('00001000', '000100100100'),
    ('000000', '010100000'),
    ('000001', '010100100'),
    ('000010', '000100000'),
    ('000011', '000100100'),
    ('0001', '000100'),
    ('0010', '010000'),
    ('0011', '010100'),
    ('01', '010'),
    ('10', '001'),
)

def base(bits, previous):
    for data, modulation in TABLE:
        if bits.startswith(data):
            return len(data), modulation
    if bits.startswith('11'):
        return 2, '000' if previous else '101'
    if bits == '0000':
        return 4, '010100'
    if bits == '00':
        return 2, '000'
    raise AssertionError(bits)

def word(bits, previous):
    if bits.startswith('110111') and len(bits) >= 8:
        if base(bits[6:], 1)[1].startswith('010'):
            return 6, '001000000'
    return base(bits, previous)

def finite(data_bits, previous=0):
    output = []
    while data_bits:
        consumed, modulation = word(data_bits, previous)
        output.append(modulation)
        previous = int(modulation[-1])
        data_bits = data_bits[consumed:]
    return ''.join(output)

def cycle_bits(data_period, phase=0, previous=0):
    initial_phase = phase
    repeated = data_period * (2 + 16//len(data_period))
    seen, segments, trace = {}, [], []
    state = (phase, previous)
    while state not in seen:
        seen[state] = len(segments)
        phase, previous = state
        consumed, modulation = word(repeated[phase:phase + 16], previous)
        segments.append(modulation)
        trace.append({'phase':phase, 'previous':previous, 'data':repeated[phase:phase+consumed], 'modulation':modulation})
        state = ((phase + consumed) % len(data_period), int(modulation[-1]))
    start = seen[state]
    prefix = ''.join(segments[:start])
    modulation = ''.join(segments[start:])
    transitions = [i for i,b in enumerate(modulation) if b == '1']
    run_hist = collections.Counter((transitions[(i+1)%len(transitions)] - t) % len(modulation) or len(modulation) for i,t in enumerate(transitions))
    levels = []
    level = 0
    for bit in modulation * (2 if len(transitions) % 2 else 1):
        level ^= int(bit)
        levels.append(level)
    density = fractions.Fraction(len(transitions), len(modulation))
    occupancy = fractions.Fraction(sum(levels), len(levels))
    return {'input_phase':initial_phase, 'prefix':prefix, 'period':modulation,
            'period_channel_bits':len(modulation),'period_transitions':len(transitions),
            'density_ratio':str(density),'density':float(density),
            'mean_run_t':float(1/density),
            'run_hist':dict(sorted(run_hist.items())),
            'level_one_fraction':float(occupancy), 'level_one_ratio':str(occupancy),
            'trace':trace[start:]}

def cycle(q, phase=0, previous=0):
    return {'q':q,'hex':f'{q:02X}',**cycle_bits(f'{q:08b}',phase,previous)}

def cycle_with_fixed_dc(q, controls):
    controls = controls * (8//len(controls))
    raw = f'{q:08b}' * (45*len(controls)//8)
    period = ''.join(raw[i*45:(i+1)*45] + c for i,c in enumerate(controls))
    return cycle_bits(period)

def canonical_rotation(bits):
    return min(bits[i:] + bits[:i] for i in range(len(bits)))

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir',type=Path,default=Path.cwd())
    out = parser.parse_args().output_dir
    out.mkdir(parents=True,exist_ok=True)
    rows = [cycle(q) for q in range(256)]
    phase_results = {f'{q:02X}':[cycle(q,p,previous) for p in range(8) for previous in range(2)] for q in range(256)}
    (out/'independent-periodic-results.json').write_text(json.dumps({'reference':'ISO/IEC 30190:2021 Fig. 41','domain':'repeated post-scramble Q, no DC/ECC/BIS/sync','all_bytes':rows,'phase_results':phase_results},indent=2),encoding='utf-8')
    with (out/'independent-periodic-results.csv').open('w',newline='',encoding='utf-8-sig') as f:
        writer=csv.writer(f)
        writer.writerow(['Q','Density','Exact ratio','Mean run T','NRZI level one','Min run T','Max run T','Cycle channel pattern','2T','3T','4T','5T','6T','7T','8T'])
        for r in rows:
            writer.writerow([r['hex'],r['density'],r['density_ratio'],r['mean_run_t'],r['level_one_fraction'],min(r['run_hist']),max(r['run_hist']),r['period'],*[r['run_hist'].get(i,0) for i in range(2,9)]])
    groups=collections.defaultdict(list)
    for r in rows:
        groups[r['density_ratio']].append(r['hex'])
    print('DENSITY GROUPS')
    for ratio,qs in sorted(groups.items(),key=lambda x:fractions.Fraction(x[0])):
        print(ratio, len(qs), ' '.join(qs))
    print('LOW/HIGH DETAIL')
    for r in sorted(rows,key=lambda r:r['density'])[:20] + sorted(rows,key=lambda r:r['density'],reverse=True)[:20]:
        print(r['hex'],r['density_ratio'],r['run_hist'],r['level_one_ratio'],r['period'])
    print('INITIAL PREV DEPENDENCE', [key for key,rs in phase_results.items() if any(rs[p*2]['density']!=rs[p*2+1]['density'] for p in range(8))])
    print('EVEN PHASE DEPENDENCE', [key for key,rs in phase_results.items() if len(set(r['density_ratio'] for i,r in enumerate(rs) if i//2%2==0))>1])
    print('EVEN ODD PHASE DIFFERENCES')
    for key,rs in phase_results.items():
        if rs[0]['density_ratio']!=rs[2]['density_ratio']:
            print(key,rs[0]['density_ratio'],rs[2]['density_ratio'])
    selected=[0x08,0x11,0x22,0x44,0x88,0x7e,0x55,0xaa,0x00,0xff]
    print('SELECTED INPUT / OUTPUT')
    for q in selected:
        print(f'{q:02X}',finite(f'{q:08b}'*4))
    finite_examples = {f'{q:02X}':finite(f'{q:08b}'*4) for q in range(256)}
    (out/'independent-finite-vectors.json').write_text(json.dumps(finite_examples,indent=2),encoding='utf-8')
    dc = {f'{q:02X}':{controls:{k:v for k,v in cycle_with_fixed_dc(q,controls).items() if k not in ('trace','prefix','period')} for controls in ('0','1','01')} for q in range(256)}
    (out/'independent-fixed-dc-results.json').write_text(json.dumps(dc,indent=2),encoding='utf-8')
    print('FIXED PERIODIC DC SELECTED')
    for q in (0x11,0x22,0x44,0x88,0x3d,0x4f,0x7a,0x9e,0xa7,0xd3,0xe9,0xf4,0x07,0x37,0x00,0xff,0x55,0xaa):
        print(f'{q:02X}',[(key,v['density_ratio'],v['level_one_ratio']) for key,v in dc[f'{q:02X}'].items()])

if __name__=='__main__':
    main()
