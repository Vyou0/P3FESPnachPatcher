# P3FES Pnach Patcher

A command-line tool that **permanently patch `.pnach` cheat into a
Persona 3 FES (SLUS_216.21) ELF or ISO**

## Features

<<<<<<< HEAD
- **`word` and `extended` patch types only!!**: Any other pnach type is
  skipped with a warning, never silently misapplied.
- **Extend ELF to load patches**: The tool extends the
  ELF so that address physically exists in the file.
- **Relocate some patches**: Addresses below `0x100000` are free/unbacked
=======
- **`word` and `extended` patch types only**: any other pnach type is
  skipped with a warning, never silently misapplied.
- **Extend ELF to load patches**: The tool extends the
  ELF so that address physically exists in the file.
- **Relocate some patches**: addresses below `0x100000` are free/unbacked
>>>>>>> fa07ad9c5d3788d0ab7d6279fa70e6f6225983c8
  kernel RAM at runtime (not present in any file). The tool automatically
  clusters nearby addresses (gap ≤ 16 bytes) into logical code blocks,
  relocates each block into freshly appended ELF space, fixes up internal
  absolute `j`/`jal` instructions, and finds and redirects **every** entry
<<<<<<< HEAD
  jump into that block even if multiple call sites jump into the same
=======
  jump into that block — even if multiple call sites jump into the same
>>>>>>> fa07ad9c5d3788d0ab7d6279fa70e6f6225983c8
  injected code.
- **Custom code compatibility**: Optional explicit `//CUSTOM CODE START` / `//CUSTOM CODE END` markers are
  still honored if present, to force specific addresses into one block.
- **Conditional patches detection for (`E`-type) & (`D`-type) with features to export them**: These type of cheats can't be applied
  statically, so the tool skips them and export each one it found instead of
  apply incorrect patch.
- **Compatible with cheat devices**: This tool protect the mastercode, making it capable to cheat
<<<<<<< HEAD

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
=======
- **ISO mode**: can extract a named executable directly from a `.iso`, run it
  through the same patch pipeline, and dump the full disc contents (with the
  patched executable substituted in) to a folder — ready to be rebuilt into a
  proper bootable ISO with CD-DVD GenTool +
  iml2iso.

## Usage

**Raw ELF:**
```bash
dotnet run -- <input.elf> <output.elf> <pnach-file-or-folder> [more...]
```

**ISO:**
```bash
dotnet run -- <input.iso> <output-folder> <elf-name-in-iso> <pnach-file-or-folder> [more...]
```
Example:
```bash
dotnet run -- game.iso game_patched_folder/ SLUS_216.21 pnach_folder/
```

After ISO mode finishes, import the output folder into CD-DVD GenTool,
export as `.IML`, then convert to a final `.iso` with iml2iso. Always test
the rebuilt ISO in an emulator before using it on real hardware.
>>>>>>> fa07ad9c5d3788d0ab7d6279fa70e6f6225983c8

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download)

## Building

```bash
dotnet build
```
<<<<<<< HEAD
=======

## Disclaimer

This is an unofficial, fan-made tool for personal modding/emulation use.
*Persona 3 FES* and related names are trademarks of Atlus/Sega. This
repository contains no game assets, ROM data, or copyrighted game code — only
original tooling written to manipulate an executable's structure. You are
responsible for only using this with games you legally own.
>>>>>>> fa07ad9c5d3788d0ab7d6279fa70e6f6225983c8
