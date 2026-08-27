# P3FES Pnach Patcher

A command-line tool that **permanently patch `.pnach` cheat into a
Persona 3 FES (SLUS_216.21) ELF or ISO**

## Features

- **`word` and `extended` patch types only!!**: Any other pnach type is
  skipped with a warning, never silently misapplied.
- **Extend ELF to load patches**: The tool extends the
  ELF so that address physically exists in the file.
- **Relocate some patches**: Addresses below `0x100000` are free/unbacked
  kernel RAM at runtime (not present in any file). The tool automatically
  clusters nearby addresses (gap ≤ 16 bytes) into logical code blocks,
  relocates each block into freshly appended ELF space, fixes up internal
  absolute `j`/`jal` instructions, and finds and redirects **every** entry
  jump into that block even if multiple call sites jump into the same
  injected code.
- **Custom code compatibility**: Optional explicit `//CUSTOM CODE START` / `//CUSTOM CODE END` markers are
  still honored if present, to force specific addresses into one block.
- **Conditional patches detection for (`E`-type) & (`D`-type) with features to export them**: These type of cheats can't be applied
  statically, so the tool skips them and export each one it found instead of
  apply incorrect patch.
- **Compatible with cheat devices**: This tool protect the mastercode, making it capable to cheat

## Usage

### **ISO** - Mode

1. Put the iso directory onto its respective box
2. Choose where the extracted path
3. Make sure the elf name (*SLXX_XXX.XX) is correct
4. Add pnach files or folders containing it to its respective box
5. Press the `Patch Executable` button
6. once it finished, prepare to rebuild it into **ISO** with CD-DVD GenTool + iml2iso or imgburn

### **ELF** - Mode

1. Extract all files on **ISO** into a folder
1. Put the elf from the extracted folder directory onto its respective box
2. Choose where the output path
3. Make sure the elf name (*SLXX_XXX.XX) is correct
4. Add pnach files or folders containing it to its respective box
5. Press the `Patch Executable` button
6. once it finished, moved the elf back to the **ISO** extracted folder and prepare to rebuild it into **ISO** with CD-DVD GenTool + iml2iso or imgburn

#### **Important!!**
Always test the rebuilt ISO in an emulator before using it on real hardware.

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download)

## Building

```bash
dotnet build
```
