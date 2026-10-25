import json
import argparse
import math
from fractions import Fraction
from pathlib import Path
from analyze import cycle_bits
from pattern_search import fast_cycle

def with_dc(pattern,controls):
    count=math.lcm(len(pattern)//math.gcd(45,len(pattern)),len(controls))
    raw=(pattern*((45*count)//len(pattern)+1))[:45*count]
    return cycle_bits(''.join(raw[i*45:(i+1)*45]+controls[i%len(controls)] for i in range(count)))

def main():
    parser=argparse.ArgumentParser(description='Search short input patterns robust to either bit-pair phase.')
    parser.add_argument('--output-dir',type=Path,default=Path.cwd())
    parser.add_argument('--max-period',type=int,default=16)
    args=parser.parse_args()
    args.output_dir.mkdir(parents=True,exist_ok=True)
    lowest=Fraction(1);highest=Fraction(0);lows=[];highs=[]
    avg_low=Fraction(1);avg_high=Fraction(0);avg_lows=[];avg_highs=[]
    for n in range(2,args.max_period+1):
        count=0
        for x in range(2**n):
            p=f'{x:0{n}b}'
            if p!=min(p[i:]+p[:i] for i in range(n)):continue
            count+=1
            a=fast_cycle(p);b=fast_cycle(p[1:]+p[:1])
            low=max(a,b);high=min(a,b);avg=(a+b)/2
            if low<lowest:lowest=low;lows=[p]
            elif low==lowest:lows.append(p)
            if high>highest:highest=high;highs=[p]
            elif high==highest:highs.append(p)
            if avg<avg_low:avg_low=avg;avg_lows=[p]
            elif avg==avg_low:avg_lows.append(p)
            if avg>avg_high:avg_high=avg;avg_highs=[p]
            elif avg==avg_high:avg_highs.append(p)
        print(n,count,'robust limits',lowest,highest,'average limits',avg_low,avg_high,flush=True)
    result={'robust_low_ratio':str(lowest),'robust_high_ratio':str(highest),
       'robust_low':[{'input':p,'even':cycle_bits(p),'odd':cycle_bits(p,1)} for p in lows],
       'robust_high':[{'input':p,'even':cycle_bits(p),'odd':cycle_bits(p,1)} for p in highs],
       'average_low_ratio':str(avg_low),'average_high_ratio':str(avg_high),
       'average_low':avg_lows,'average_high':avg_highs}
    (args.output_dir/'robust-bitpattern-search.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    for k in ('robust_low','robust_high'):
        print(k,[(v['input'],v['even']['density_ratio'],v['odd']['density_ratio']) for v in result[k]],flush=True)
    print('AVERAGE_LOW',avg_lows,flush=True);print('AVERAGE_HIGH',avg_highs,flush=True)
    patterns=['0000101101110010','0111011110','0001','00111101']+[x['input'] for x in result['robust_low']]+[x['input'] for x in result['robust_high']]
    dc={p:{ctrl:{k:v for k,v in with_dc(p,ctrl).items() if k not in ('trace','prefix','period')} for ctrl in ('0','1','01')} for p in dict.fromkeys(patterns)}
    (args.output_dir/'bitpattern-fixed-dc.json').write_text(json.dumps(dc,indent=2),encoding='utf-8')
    print('FIXED DC')
    for p,res in dc.items():
        print(p,[(k,v['density_ratio']) for k,v in res.items()],flush=True)

if __name__=='__main__':main()
