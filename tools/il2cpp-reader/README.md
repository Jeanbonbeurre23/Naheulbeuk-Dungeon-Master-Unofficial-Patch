# il2cpp-reader

Three Python scripts that let us read the game's native code without Ghidra, using files BepInEx already generated.

- `build_names.py` pairs every method address in `GameAssembly.dll` with its name, from `BepInEx\interop\MethodAddressToToken.db` and the interop DLLs. Run it once per game build.
- `ndmdis.py "Class::Method"` disassembles a method and names every call it makes.
- `xrefs.py "Class::Method"` lists the methods that call it, from `BepInEx\interop\MethodXrefScanCache.db`.

Requirements: Python 3, `pip install dnfile capstone pefile`. Paths default to the standard Steam library (`C:\Program Files (x86)\Steam\steamapps\common\NDM`) and can be changed with the environment variables `NDM_INTEROP`, `NDM_GAMEASSEMBLY` and `NDM_READER_WORK`.

Limits: calls made through delegates or virtual tables do not appear in `xrefs.py`; field accesses appear as raw offsets; generic code inlined by IL2CPP appears as unnamed calls.
