# Lists the callers of native methods, from Il2CppInterop's MethodXrefScanCache.db.
# Usage: python xrefs.py "AlertUtility::AlertNearestGuardRoom". Run build_names.py first.
import struct, pickle, bisect, sys, os
INTEROP = os.environ.get('NDM_INTEROP', r'C:\Program Files (x86)\Steam\steamapps\common\NDM\BepInEx\interop')
GAMEASM = os.environ.get('NDM_GAMEASSEMBLY', r'C:\Program Files (x86)\Steam\steamapps\common\NDM\GameAssembly.dll')
WORK = os.environ.get('NDM_READER_WORK', os.path.dirname(os.path.abspath(__file__)))
W=WORK
rn=pickle.load(open(W+'/rva_names.pkl','rb')); rvas=[a for a,_ in rn]
byname={}
for a,n in rn: byname.setdefault(n,[]).append(a)
b=open(os.path.join(INTEROP,'MethodXrefScanCache.db'),'rb').read()
n=(len(b)-16)//24
import array
ent=[struct.unpack_from('<qqq',b,16+24*i) for i in range(n)]
def owner(rva):
    i=bisect.bisect_right(rvas,rva)-1
    return rn[i][1] if i>=0 else '?'
def callers(name):
    out=set()
    for a in byname.get(name,[]):
        for t,f,ty in ent:
            if t==a: out.add(owner(f))
    return sorted(out)
if __name__=='__main__':
    for q in sys.argv[1:]:
        print('==',q,'callers:'); [print('   ',c) for c in callers(q)]
