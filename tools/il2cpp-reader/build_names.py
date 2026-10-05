# Builds rva_names.pkl / rva_names.tsv: every native method RVA of GameAssembly.dll with its interop name.
# Inputs: BepInEx interop folder (MethodAddressToToken.db + interop DLLs). Requires: pip install dnfile
import dnfile, pickle, os, bisect
INTEROP = os.environ.get('NDM_INTEROP', r'C:\Program Files (x86)\Steam\steamapps\common\NDM\BepInEx\interop')
GAMEASM = os.environ.get('NDM_GAMEASSEMBLY', r'C:\Program Files (x86)\Steam\steamapps\common\NDM\GameAssembly.dll')
WORK = os.environ.get('NDM_READER_WORK', os.path.dirname(os.path.abspath(__file__)))
import struct
def read7(b,p):
    r=0;s=0
    while True:
        c=b[p];p+=1;r|=(c&0x7f)<<s;s+=7
        if c<0x80: return r,p
def load_matt():
    b=open(os.path.join(INTEROP,'MethodAddressToToken.db'),'rb').read()
    magic,ver,nasm,nmeth,dataoff=struct.unpack_from('<4sIIII',b,0)
    assert magic==b'UMTM' and ver==1, (magic, ver)
    p=20; names=[]
    for i in range(nasm):
        n,p=read7(b,p); names.append(b[p:p+n].decode()); p+=n
    addrs=struct.unpack_from(f'<{nmeth}q',b,dataoff)
    pairs=struct.unpack_from(f'<{nmeth*2}i',b,dataoff+8*nmeth)
    return names,addrs,pairs
names,addrs,pairs=load_matt()
W=WORK
cache={}
def method_names(asm):
    if asm in cache: return cache[asm]
    path=os.path.join(INTEROP,asm.split(',')[0]+'.dll')
    res={}
    if os.path.exists(path):
        pe=dnfile.dnPE(path)
        md=pe.net.mdtables
        tdefs=md.TypeDef.rows if md.TypeDef else []
        mdefs=md.MethodDef.rows if md.MethodDef else []
        nmd=len(mdefs)
        # method list ranges
        starts=[]
        for i,t in enumerate(tdefs):
            ml=t.MethodList
            idx = ml.row_index if hasattr(ml,'row_index') else (ml[0].row_index if ml else nmd+1)
            starts.append(idx)
        # nested type -> enclosing
        enclosing={}
        if md.NestedClass:
            for r in md.NestedClass.rows:
                enclosing[r.NestedClass.row_index]=r.EnclosingClass.row_index
        def tname(i):
            t=tdefs[i-1]; n=str(t.TypeName); ns=str(t.TypeNamespace)
            if i in enclosing: return tname(enclosing[i])+'/'+n
            return (ns+'.' if ns else '')+n
        for ti in range(len(tdefs)):
            s=starts[ti]; e=starts[ti+1] if ti+1<len(starts) else nmd+1
            if s==0: continue
            tn=tname(ti+1)
            for mi in range(s, e):
                if 1<=mi<=nmd:
                    res[0x06000000|mi]=tn+'::'+str(mdefs[mi-1].Name)
    cache[asm]=res; return res
out=[]
for i,a in enumerate(addrs):
    tok,asmi=pairs[2*i],pairs[2*i+1]
    asm=names[asmi] if 0<=asmi<len(names) else '?'
    n=method_names(asm).get(tok, f'{asm.split(",")[0]}!{tok:#x}')
    out.append((a,n))
out.sort()
pickle.dump(out,open(W+'/rva_names.pkl','wb'))
with open(W+'/rva_names.tsv','w') as f:
    for a,n in out: f.write(f'{a:#x}\t{n}\n')
print(len(out)); 
