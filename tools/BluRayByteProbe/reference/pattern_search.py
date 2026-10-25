import json
import argparse
from fractions import Fraction
from pathlib import Path
from analyze import word, cycle_bits, finite

def fast_cycle(pattern, previous=0):
    n=len(pattern)
    repeated=pattern*(2+16//n)
    seen={}
    outputs=[]
    phase=0
    while (phase,previous) not in seen:
        seen[(phase,previous)]=len(outputs)
        consumed,modulation=word(repeated[phase:phase+16],previous)
        outputs.append(modulation)
        phase=(phase+consumed)%n
        previous=int(modulation[-1])
    output=''.join(outputs[seen[(phase,previous)]:])
    return Fraction(output.count('1'),len(output))

def main():
    parser=argparse.ArgumentParser(description='Exhaustive 17PP periodic-input search; both previous-bit states are checked.')
    parser.add_argument('--output-dir',type=Path,default=Path.cwd())
    parser.add_argument('--max-period',type=int,default=16)
    args=parser.parse_args()
    args.output_dir.mkdir(parents=True,exist_ok=True)
    best_low=Fraction(1);best_high=Fraction(0)
    low_patterns=[];high_patterns=[];by_length=[];vectors={}
    for n in range(2,args.max_period+1):
        lowest=Fraction(1);highest=Fraction(0);lows=[];highs=[];classes={}
        for x in range(2**n):
            pattern=f'{x:0{n}b}'
            # Check every raw string, then group verified equivalent results.
            # A +2 rotation generates all rotations for odd n, and only the
            # even-offset rotations for even n (thus retaining both phases).
            canonical=min(pattern[(2*i)%n:]+pattern[:(2*i)%n] for i in range(n))
            d=fast_cycle(pattern)
            alternate=fast_cycle(pattern,1)
            assert d==alternate, ('previous-bit dependence',pattern,d,alternate)
            if canonical in classes:
                assert d==classes[canonical], ('even-rotation density mismatch',pattern,canonical,d,classes[canonical])
                continue
            classes[canonical]=d
            if d < lowest:lowest=d;lows=[pattern]
            elif d==lowest:lows.append(pattern)
            if d > highest:highest=d;highs=[pattern]
            elif d==highest:highs.append(pattern)
        if lowest<best_low:best_low=lowest;low_patterns=lows[:]
        elif lowest==best_low:low_patterns+=lows
        if highest>best_high:best_high=highest;high_patterns=highs[:]
        elif highest==best_high:high_patterns+=highs
        by_length.append({'input_period_bits':n,'raw_strings_tested':2**n,'previous_bit_states_per_string':2,'verified_rotation_classes':len(classes),'min_density':str(lowest),'max_density':str(highest),'low_patterns':lows,'high_patterns':highs})
        for p in lows[:8]+highs[:8]:
            for previous in (0,1):
                bits=p*16
                vectors[f'{p}:{previous}']={'data_bits':bits,'previous_modulation_bit':previous,'modulation_bits':finite(bits,previous)}
        print(n,2**n,'strings',len(classes),'classes','min',lowest,lows[:8],'max',highest,highs[:8],flush=True)
    result={'raw_strings_tested':sum(x['raw_strings_tested'] for x in by_length),'rotation_equivalence_verified':True,'previous_bit_density_independence_verified':True,'lengths':by_length,'minimum':str(best_low),'maximum':str(best_high),
            'min_candidates':[{'input':p,'even':cycle_bits(p),'odd':cycle_bits(p,1)} for p in low_patterns],
            'max_candidates':[{'input':p,'even':cycle_bits(p),'odd':cycle_bits(p,1)} for p in high_patterns]}
    out=args.output_dir/'periodic-bitpattern-search.json'
    out.write_text(json.dumps(result,indent=2),encoding='utf-8')
    (args.output_dir/'independent-pattern-vectors.json').write_text(json.dumps(vectors,indent=2),encoding='utf-8')
    print('MIN',best_low,low_patterns,flush=True)
    print('MAX',best_high,high_patterns,flush=True)

if __name__=='__main__':main()
