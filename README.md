# Furikiri

Furikiri (フリキリ / 福里吉里) is a managed TJS2 bytecode disassembler/decompiler.

## CLI usage

```text
Furikiri init.tjs                      # Decompile (default)
Furikiri decompile init.tjs            # Explicit decompile command
Furikiri disasm init.tjs               # Disassemble
Furikiri scripts                       # Batch decompile a folder
Furikiri disasm scripts                # Batch disassemble a folder
Furikiri scripts --recursive           # Include subfolders (-r)
Furikiri disasm scripts --recursive
Furikiri scripts -r -o decompiled      # Preserve paths and filenames in a new directory
Furikiri disasm scripts -r -o assembly # Preserve paths, write .tjsasm files
Furikiri scripts other.tjs --print     # Print results instead of writing files (-p)
```

## Related projects
* [KirikiriSharp](https://github.com/Project-AZUSA/KirikiriSharp)
* [IronTJS](https://github.com/Project-AZUSA/IronTJS)

---

by Ulysses (wdwxy12345@gmail.com)

Furikiri is licensed under **LGPLv3**.
