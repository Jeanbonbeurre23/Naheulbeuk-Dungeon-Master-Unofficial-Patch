# Disassembles native methods of GameAssembly.dll by interop name, annotating calls with method names.
# Usage: python ndmdis.py "MinionUtility::HireBarmansForCounter". Requires: pip install capstone pefile; run build_names.py first.
import pickle, pefile, bisect, sys, os, re, struct
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
INTEROP = os.environ.get('NDM_INTEROP', r'C:\Program Files (x86)\Steam\steamapps\common\NDM\BepInEx\interop')
GAMEASM = os.environ.get('NDM_GAMEASSEMBLY', r'C:\Program Files (x86)\Steam\steamapps\common\NDM\GameAssembly.dll')
WORK = os.environ.get('NDM_READER_WORK', os.path.dirname(os.path.abspath(__file__)))
W=WORK
rn=pickle.load(open(W+'/rva_names.pkl','rb'))
rvas=[a for a,_ in rn]; byname={}
for a,n in rn: byname.setdefault(n,[]).append(a)
namemap=dict(rn)
pe=pefile.PE(GAMEASM, fast_load=True)
data=open(GAMEASM,'rb').read()
base=pe.OPTIONAL_HEADER.ImageBase
def off(rva): return pe.get_offset_from_rva(rva)
md=Cs(CS_ARCH_X86, CS_MODE_64); md.detail=False
def name_at(rva):
    if rva in namemap: return namemap[rva]
    i=bisect.bisect_right(rvas,rva)-1
    if i>=0 and rva-rvas[i]<0x40: return f'{rn[i][1]}+{rva-rvas[i]:#x}'
    return None
def disasm(rva, maxlen=0x3000):
    i=bisect.bisect_right(rvas,rva)
    end=rvas[i] if i<len(rvas) else rva+maxlen
    end=min(end, rva+maxlen)
    code=data[off(rva):off(rva)+(end-rva)]
    lines=[]
    for ins in md.disasm(code, rva):
        s=f'{ins.address:#x}: {ins.mnemonic} {ins.op_str}'
        m=re.match(r'(call|jmp|je|jne|jz|jnz|ja|jb|jae|jbe|jg|jl|jge|jle|js|jns)\s+(0x[0-9a-f]+)$', ins.mnemonic+' '+ins.op_str)
        if m and ins.mnemonic in ('call','jmp'):
            t=int(m.group(2),16); n=name_at(t)
            if n: s+=f'    ; {n}'
        m2=re.search(r'\[rip ([+-]) (0x[0-9a-f]+)\]', ins.op_str)
        if m2:
            t=ins.address+ins.size+(int(m2.group(2),16)*(1 if m2.group(1)=='+' else -1))
            s+=f'    ; [{t:#x}]'
        lines.append(s)
        if ins.mnemonic in ('int3',) : break
    return lines
if __name__=='__main__':
    for q in sys.argv[1:]:
        for a in byname.get(q,[]):
            print(f'==== {q} @ {a:#x}')
            print('\n'.join(disasm(a)))
