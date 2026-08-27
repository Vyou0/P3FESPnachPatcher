// Notes on the program-header handling, since it's the part most likely
// to bite someone editing this later:
//
// Two TLB Miss crashes on real boot have come from the same root cause:
// anything that changes e_phnum forces e_phoff to relocate away from its
// original disc offset (52), and the actual PS2 loader ("loadelf 3.30")
// doesn't tolerate that, unlike lenient tools like uLaunchELF. Booting
// fine in PCSX2 does not prove this is fixed — it just means the
// emulator's loader was more forgiving.
//
// Both normal out-of-range word patches and kernel-RAM custom-code
// blocks used to solve this by minting a brand-new PT_LOAD each time,
// which is what broke it. Both paths now grow an existing trailing
// PT_LOAD in place instead (see the block-relocation loop and the
// out-of-range handling further down), so e_phnum/e_phoff never move.
// Verified: rebuilt ELF keeps phnum=2, e_phoff=52 (same as the original
// disc ELF), passes structural validation, and boots clean in PCSX2.
// Not yet verified on real hardware.
//
// Known gap: when a kernel-RAM block is relocated, only j/jal
// instructions pointing into it get rewritten automatically. Absolute
// address loads (lui+ori / lui+addiu pairs, i.e. `li $reg, addr`) are
// only detected and printed for manual review, never rewritten — the
// same bit pattern can just as easily be an ordinary integer constant,
// and guessing wrong risks silently corrupting a value that was never
// an address. Any pnach batch that trips this needs a manual look
// before it's trusted, especially before testing on real hardware.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DiscUtils.Iso9660;

namespace P3FesPnachPatcher;

internal static class GameConstants
{
    public const uint SifSendCmdCaller = 0x506658;
    public const uint KernelRamCeiling = 0x100000;
    public const uint BlockClusterGap = 0x10;
    // PS2 EE MMU page size. New PT_LOAD regions must start (and their sizes
    // should round up to) this boundary so the kernel can install a clean
    // TLB entry for them -- misaligned regions are one of the real causes
    // behind reported TLB MISS crashes, not a cosmetic detail.
    public const uint PageSize = 0x1000;
    // The CodeBreaker mastercode this project's whole cheat setup depends
    // on (see project notes: sceSifSendCmd hook at 0x506658). Without this
    // line present in a CodeBreaker/PS2rd code list, the cheat engine has
    // no hook into the game's frame loop -- codes can be "enabled" in the
    // UI and still never execute, which is exactly the "conditional is
    // active but does nothing" symptom this constant exists to prevent.
    // Every raw CodeBreaker export MUST start with this line.
    public const string CodeBreakerMastercode = "90506658 0C14193E";
}

internal sealed class ProgramHeader
{
    public uint Type, Offset, VAddr, PAddr, FileSize, MemSize, Flags, Align;
    public const int Size = 32;

    public static ProgramHeader Read(byte[] d, int o) => new()
    {
        Type = BitConverter.ToUInt32(d, o), Offset = BitConverter.ToUInt32(d, o + 4),
        VAddr = BitConverter.ToUInt32(d, o + 8), PAddr = BitConverter.ToUInt32(d, o + 12),
        FileSize = BitConverter.ToUInt32(d, o + 16), MemSize = BitConverter.ToUInt32(d, o + 20),
        Flags = BitConverter.ToUInt32(d, o + 24), Align = BitConverter.ToUInt32(d, o + 28),
    };

    public void WriteInto(byte[] d, int o)
    {
        BitConverter.GetBytes(Type).CopyTo(d, o); BitConverter.GetBytes(Offset).CopyTo(d, o + 4);
        BitConverter.GetBytes(VAddr).CopyTo(d, o + 8); BitConverter.GetBytes(PAddr).CopyTo(d, o + 12);
        BitConverter.GetBytes(FileSize).CopyTo(d, o + 16); BitConverter.GetBytes(MemSize).CopyTo(d, o + 20);
        BitConverter.GetBytes(Flags).CopyTo(d, o + 24); BitConverter.GetBytes(Align).CopyTo(d, o + 28);
    }
}

internal static class Program
{
    private const uint PT_LOAD = 1;
    private static readonly Regex PatchRe = new(@"^patch=(\d+),EE,([0-9A-Fa-f]+),(\w+),([0-9A-Fa-f]+)", RegexOptions.Compiled);

    private static byte[] _data = Array.Empty<byte>();
    private static readonly List<ProgramHeader> Segs = new();

    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage (ELF): P3FesPnachPatcher <input.elf> <output.elf> <pnach-file-or-folder> [more...]");
            Console.Error.WriteLine("Usage (ISO): P3FesPnachPatcher <input.iso> <output-folder> <elf-name-in-iso> <pnach-file-or-folder> [more...]");
            return 1;
        }

        try
        {
            bool isIso = Path.GetExtension(args[0]).Equals(".iso", StringComparison.OrdinalIgnoreCase);
            if (isIso)
            {
                if (args.Length < 4)
                {
                    Console.Error.WriteLine("ISO mode needs: <input.iso> <output-folder> <elf-name-in-iso> <pnach...>");
                    return 1;
                }
                RunIso(args[0], args[1], args[2], args.Skip(3).ToArray());
            }
            else
            {
                RunElf(args[0], args[1], args.Skip(2).ToArray());
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    // ============================================================
    // ISO mode
    // ============================================================
    private static void RunIso(string inIso, string outFolder, string elfName, string[] pnachArgs)
    {
        Console.WriteLine($"Opening ISO (read-only): {inIso}");
        byte[] elfBytes;
        using (var isoStream = File.OpenRead(inIso))
        using (var reader = new CDReader(isoStream, joliet: false))
        {
            string? foundPath = FindFile(reader, reader.Root, elfName);
            if (foundPath is null)
                throw new FileNotFoundException($"Could not find '{elfName}' anywhere in the ISO. " +
                                                 "Check the exact filename (case-insensitive match attempted).");

            Console.WriteLine($"Found executable at: {foundPath}");
            using var s = reader.OpenFile(foundPath, FileMode.Open);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            elfBytes = ms.ToArray();
        }

        Console.WriteLine($"Extracted {elfBytes.Length} bytes. Running patch pipeline...");
        byte[] patchedElf = PatchElfBytes(elfBytes, pnachArgs);
        Console.WriteLine($"\nPatched executable: {patchedElf.Length} bytes " +
                           $"(was {elfBytes.Length}, +{patchedElf.Length - elfBytes.Length})");

        Directory.CreateDirectory(outFolder);
        Console.WriteLine($"\nDumping full disc contents to: {outFolder}");

        using (var isoStream = File.OpenRead(inIso))
        using (var reader = new CDReader(isoStream, joliet: false))
        {
            ExtractAllFiles(reader, reader.Root, outFolder, elfName, patchedElf);
        }

        Console.WriteLine("\nDone. Next steps:");
        Console.WriteLine($"  1. Open CD-DVD GenTool, import the contents of: {outFolder}");
        Console.WriteLine("  2. Export as .IML");
        Console.WriteLine("  3. Convert the .IML to a final .ISO with iml2iso");
        Console.WriteLine("  4. Test the resulting ISO in PCSX2 before using it on real hardware.");
    }

    private static string? FindFile(CDReader reader, DiscUtils.DiscDirectoryInfo dir, string name)
    {
        foreach (var f in dir.GetFiles())
        {
            string baseName = f.Name.Split(';')[0];
            if (baseName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return f.FullName;
        }
        foreach (var sub in dir.GetDirectories())
        {
            var found = FindFile(reader, sub, name);
            if (found is not null) return found;
        }
        return null;
    }

    private static void ExtractAllFiles(CDReader reader, DiscUtils.DiscDirectoryInfo dir, string outDir,
        string elfName, byte[] patchedElf)
    {
        Directory.CreateDirectory(outDir);
        foreach (var f in dir.GetFiles())
        {
            string baseName = f.Name.Split(';')[0];
            string destFile = Path.Combine(outDir, baseName);

            if (baseName.Equals(elfName, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllBytes(destFile, patchedElf);
                Console.WriteLine($"  [PATCHED] {destFile}");
                continue;
            }

            using var s = reader.OpenFile(f.FullName, FileMode.Open);
            using var outFs = File.Create(destFile);
            s.CopyTo(outFs);
        }
        foreach (var sub in dir.GetDirectories())
        {
            string baseName = sub.Name.Split(';')[0];
            ExtractAllFiles(reader, sub, Path.Combine(outDir, baseName), elfName, patchedElf);
        }
    }

    // ============================================================
    // Raw ELF mode
    // ============================================================
    private static void RunElf(string inElf, string outElf, string[] pnachArgs)
    {
        byte[] inputBytes = File.ReadAllBytes(inElf);
        byte[] result = PatchElfBytes(inputBytes, pnachArgs);
        File.WriteAllBytes(outElf, result);
        Console.WriteLine($"\nWrote {outElf}: {result.Length} bytes (was {inputBytes.Length}, +{result.Length - inputBytes.Length})");
    }

    // ============================================================
    // Shared patch pipeline
    // ============================================================
    private static byte[] PatchElfBytes(byte[] inputBytes, string[] pnachArgs)
    {
        _data = (byte[])inputBytes.Clone();
        int origLen = _data.Length;

        if (_data.Length < 52 || _data[0] != 0x7F || _data[1] != (byte)'E' || _data[2] != (byte)'L' || _data[3] != (byte)'F')
            throw new InvalidDataException("Not a valid ELF file.");
        if (_data[4] != 1)
            throw new InvalidDataException("Only 32-bit ELF is supported.");

        Segs.Clear();
        uint ePhoff = BitConverter.ToUInt32(_data, 28);
        ushort ePhentsize = BitConverter.ToUInt16(_data, 42);
        ushort ePhnum = BitConverter.ToUInt16(_data, 44);
        for (int i = 0; i < ePhnum; i++)
            Segs.Add(ProgramHeader.Read(_data, (int)ePhoff + i * ePhentsize));

        uint before = ReadWord(GameConstants.SifSendCmdCaller);
        Console.WriteLine($"sceSifSendCmd caller @ {GameConstants.SifSendCmdCaller:X} BEFORE: {before:08X}");

        // Handle the case where there are no PT_LOAD segments at all
        var loadSegs = Segs.Where(s => s.Type == PT_LOAD).ToList();
        if (loadSegs.Count == 0)
            throw new InvalidDataException("ELF has no PT_LOAD segments.");
        // Use MemSize, not FileSize. If the last segment has BSS
        // (MemSize > FileSize -- common for .bss/.sbss sections), the game's
        // own runtime footprint extends past FileSize even though nothing is
        // backed on disk there. Starting our new region at FileSize would put
        // it INSIDE the game's own BSS virtual address range -- both segments
        // then claim the same VAddr, and the PS2 EE MMU/loader has to resolve
        // an ambiguous mapping. This is one of the real causes behind the
        // reported TLB MISS, not just a hygiene issue.
        uint nextFreeVAddr = loadSegs.Max(s => s.VAddr + Math.Max(s.FileSize, s.MemSize));
        Console.WriteLine($"Free RAM starts at: {nextFreeVAddr:X}");

        foreach (var pnachPath in ExpandPnachPaths(pnachArgs))
        {
            Console.WriteLine($"\n=== Processing: {Path.GetFileName(pnachPath)} ===");
            var (normal, forcedBlocks, conditionals) = ParsePnach(pnachPath);

            if (conditionals.Count > 0)
            {
                Console.WriteLine($"  {conditionals.Count} conditional (E-type/D-type) line(s) found -- these need live " +
                                   "per-frame evaluation and CANNOT be baked statically. Skipped:");
                foreach (var c in conditionals)
                {
                    string tag = Regex.IsMatch(c, @"patch=\d+,EE,[Dd]", RegexOptions.None) ? "D-type" : "E-type";
                    Console.WriteLine($"    [{tag}] {c}");
                }

                // Export the skipped conditional lines to an external
                // PS2rd/OPL .cht file, so they aren't just discarded -- the
                // player can still use them via OPL's PS2RD cheat engine
                // instead of losing the effect entirely. This does NOT change
                // what gets baked into the ELF; conditionals are still never
                // written to _data, exactly as before.
                string chtOutDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pnachPath)) ?? ".", "cht_export");
                ExportConditionalsToCht(conditionals, chtOutDir, Path.GetFileNameWithoutExtension(pnachPath));
            }

            var claimed = new HashSet<uint>(forcedBlocks.SelectMany(b => b.Keys));
            var lowAddrs = normal.Keys.Where(a => a < GameConstants.KernelRamCeiling && !claimed.Contains(a))
                                       .OrderBy(a => a).ToList();

            var autoBlocks = new List<Dictionary<uint, uint>>();
            if (lowAddrs.Count > 0)
            {
                var cur = new Dictionary<uint, uint> { [lowAddrs[0]] = normal[lowAddrs[0]] };
                uint lastAddr = lowAddrs[0];
                for (int i = 1; i < lowAddrs.Count; i++)
                {
                    uint a = lowAddrs[i];
                    if (a - lastAddr <= GameConstants.BlockClusterGap)
                    {
                        cur[a] = normal[a];
                    }
                    else
                    {
                        autoBlocks.Add(cur);
                        cur = new Dictionary<uint, uint>();
                        cur[a] = normal[a];
                    }
                    lastAddr = a;
                }
                autoBlocks.Add(cur);
            }
            foreach (var addr in lowAddrs) normal.Remove(addr);

            var allBlocks = forcedBlocks.Concat(autoBlocks).ToList();
            Console.WriteLine($"  {normal.Count} normal (in-segment / high-RAM) patches, {allBlocks.Count} " +
                               $"kernel-RAM custom-code block(s) ({forcedBlocks.Count} marked, {autoBlocks.Count} auto-detected)");

            var pendingSegments = new List<ProgramHeader>();
            var pendingBytes = new List<byte>();

            // Kernel-RAM blocks used to each mint their own new PT_LOAD,
            // which is the same e_phnum/e_phoff problem the out-of-range
            // path below already had to solve. Reuse the same fix here:
            // grow the existing trailing PT_LOAD in place (via InsertBytes)
            // instead of adding a new header, no matter how many blocks
            // get relocated. Bonus: pendingSegments stays empty afterward,
            // so the out-of-range logic further down can keep growing
            // this same segment too.
            var blockGrowSeg = Segs.Where(s => s.Type == PT_LOAD &&
                    s.VAddr + Math.Max(s.FileSize, s.MemSize) == nextFreeVAddr)
                .OrderByDescending(s => s.VAddr)
                .FirstOrDefault();

            foreach (var block in allBlocks)
            {
                // Page-align every new region's start (EE MMU/TLB works in
                // fixed page granularity; a mid-page start can collide with
                // the previous page's TLB entry).
                nextFreeVAddr = AlignUp(nextFreeVAddr, GameConstants.PageSize);

                uint regionVAddr = nextFreeVAddr;
                var (words, entries, regionSize) = RelocateBlock(block, normal, regionVAddr);

                // Pad the region itself up to a full page so the NEXT region
                // (or the very next thing the kernel maps) also starts page-aligned.
                uint paddedSize = AlignUp(regionSize, GameConstants.PageSize);
                uint pad = paddedSize - regionSize;
                var regionBytes = new List<byte>(words.Length * 4 + (int)pad);
                foreach (var w in words) regionBytes.AddRange(BitConverter.GetBytes(w));
                if (pad > 0) regionBytes.AddRange(new byte[pad]);

                if (blockGrowSeg != null)
                {
                    int insertOffset = (int)(blockGrowSeg.Offset + blockGrowSeg.FileSize);
                    InsertBytes(insertOffset, regionBytes.ToArray(), blockGrowSeg);
                    blockGrowSeg.FileSize += (uint)regionBytes.Count;
                    blockGrowSeg.MemSize = blockGrowSeg.FileSize;
                    blockGrowSeg.Flags = 7; // RWE -- this region now holds injected machine code, not just data

                    Console.WriteLine($"    block -> grown into existing PT_LOAD (VAddr {blockGrowSeg.VAddr:X}) at " +
                                       $"{regionVAddr:X} (size {regionSize}, page-aligned to {paddedSize}), " +
                                       $"{entries.Count} entry point(s) -- adds ZERO new program header entries.");
                }
                else
                {
                    // Fallback: no existing trailing PT_LOAD to grow (should
                    // not happen for this game, which always has the empty
                    // 'heap' placeholder segment) -- mint a new PT_LOAD like
                    // the old code did, but warn loudly since this WILL
                    // force e_phoff to relocate at write-back time.
                    uint regionOffset = (uint)(_data.Length + pendingBytes.Count);
                    pendingBytes.AddRange(regionBytes);
                    pendingSegments.Add(new ProgramHeader
                    {
                        Type = PT_LOAD, Offset = regionOffset, VAddr = regionVAddr, PAddr = regionVAddr,
                        FileSize = regionSize, MemSize = regionSize, Flags = 7, Align = GameConstants.PageSize,
                    });
                    Console.WriteLine($"    block -> relocated to {regionVAddr:X} (size {regionSize}, page-aligned " +
                                       $"to {paddedSize}), {entries.Count} entry point(s) -- WARNING: no existing " +
                                       "segment available to grow, this mints a new PT_LOAD and WILL move e_phoff.");
                }

                foreach (var (addr, instr, origTarget) in entries)
                {
                    Console.WriteLine($"      entry @ {addr:X} (was -> {origTarget:X}) now -> " +
                                       $"{regionVAddr + (origTarget - block.Keys.Min()):X}");
                    normal[addr] = instr;
                }

                nextFreeVAddr = regionVAddr + paddedSize;
            }

            var allCurrentSegs = Segs.Concat(pendingSegments).ToList();
            // Check MemSize too, not just FileSize -- an address that
            // falls inside a segment's BSS tail (MemSize > FileSize) is still
            // "covered" and must not be treated as out-of-range.
            bool InAnySegment(uint a) => allCurrentSegs.Any(s => s.Type == PT_LOAD &&
                a >= s.VAddr && a < s.VAddr + Math.Max(s.FileSize, s.MemSize));

            var outOfRange = normal.Keys.Where(a => !InAnySegment(a)).OrderBy(a => a).ToList();
            if (outOfRange.Count > 0)
            {
                // An out-of-range address can either sit PAST everything
                // relocated so far, or fall in a GAP between two existing
                // segments -- the two need different handling, so split
                // them first: "growable" (>= nextFreeVAddr) vs "remaining"
                // (a genuine gap, handled by the per-cluster fallback below).
                //
                // For the growable case, try growing the existing trailing
                // PT_LOAD first instead of minting a new one -- zero new
                // program header entries, e_phoff never moves. Covers the
                // common case of a mod appending new ASM routines right
                // after the game's own image (e.g. Manual Inheritance).
                var growable = outOfRange.Where(a => a >= nextFreeVAddr).OrderBy(a => a).ToList();
                var remaining = outOfRange.Where(a => a < nextFreeVAddr).OrderBy(a => a).ToList();

                if (growable.Count > 0 && pendingSegments.Count == 0)
                {
                    // Prefer the candidate with the HIGHEST VAddr: if two
                    // segments both technically "end" at nextFreeVAddr
                    // (e.g. the main segment ending there AND a zero-size
                    // trailing placeholder segment starting there), growing
                    // the trailing one is what's safe -- growing the main
                    // segment instead would make its new end overlap the
                    // placeholder segment's own VAddr and trip the overlap
                    // check in ValidateElfStructure.
                    var growSeg = Segs.Where(s => s.Type == PT_LOAD &&
                            s.VAddr + Math.Max(s.FileSize, s.MemSize) == nextFreeVAddr)
                        .OrderByDescending(s => s.VAddr)
                        .FirstOrDefault();

                    if (growSeg != null)
                    {
                        uint maxNeeded = growable.Max() + 4;
                        uint newEndVAddr = AlignUp(maxNeeded, GameConstants.PageSize);
                        uint growBy = newEndVAddr - nextFreeVAddr;

                        Console.WriteLine($"  Growing existing PT_LOAD (VAddr {growSeg.VAddr:X}) in place by " +
                                           $"0x{growBy:X} bytes to cover {growable.Count} out-of-range patch " +
                                           $"target(s) between {growable.Min():X} and {growable.Max():X} -- adds " +
                                           "ZERO new program header entries, so e_phoff/e_phnum stay completely " +
                                           "untouched.");

                        int insertOffset = (int)(growSeg.Offset + growSeg.FileSize);
                        InsertBytes(insertOffset, new byte[growBy], growSeg);

                        growSeg.FileSize += growBy;
                        growSeg.MemSize = growSeg.FileSize;
                        growSeg.Flags = 7; // RWE -- this region now holds injected machine code, not just data
                        nextFreeVAddr = newEndVAddr;
                    }
                    else
                    {
                        remaining = remaining.Concat(growable).OrderBy(a => a).ToList();
                    }
                }
                else if (growable.Count > 0)
                {
                    remaining = remaining.Concat(growable).OrderBy(a => a).ToList();
                }

                // ---- old per-cluster new-PT_LOAD fallback, now only for
                // addresses that couldn't be satisfied by growth above
                // (a genuine gap between two existing segments, or a run
                // where earlier kernel-RAM blocks already forced a phoff
                // relocation this pass anyway) ----
                if (remaining.Count > 0)
                {
                var sorted = remaining;
                var clusters = new List<(uint min, uint max)>();
                uint curMin = sorted[0], curMax = sorted[0] + 4;
                for (int oi = 1; oi < sorted.Count; oi++)
                {
                    uint a = sorted[oi];
                    if (a - curMax <= GameConstants.BlockClusterGap)
                    {
                        curMax = a + 4;
                    }
                    else
                    {
                        clusters.Add((curMin, curMax));
                        curMin = a; curMax = a + 4;
                    }
                }
                clusters.Add((curMin, curMax));

                var allSegsSoFar = Segs.Concat(pendingSegments).ToList();
                bool OverlapsAny(uint start, uint end) => allSegsSoFar.Any(s => s.Type == PT_LOAD &&
                    start < s.VAddr + Math.Max(s.FileSize, s.MemSize) && end > s.VAddr);

                foreach (var (cMin, cMax) in clusters)
                {
                    uint extendFrom = cMin;
                    uint rawSize = cMax - cMin;
                    uint extendSize = AlignUp(rawSize, GameConstants.PageSize);

                    if (OverlapsAny(extendFrom, extendFrom + extendSize))
                    {
                        Console.WriteLine($"  WARNING: patch(es) targeting {cMin:X}-{cMax:X} fall in a gap that " +
                                           "can't be safely extended without overlapping an existing segment -- " +
                                           "these addresses were NOT written. Investigate manually (this pnach " +
                                           "may target an address inside the original ELF that this tool doesn't " +
                                           "recognize as covered).");
                        foreach (var a in sorted.Where(a => a >= cMin && a < cMax)) normal.Remove(a);
                        continue;
                    }

                    Console.WriteLine($"  extending image by 0x{extendSize:X} bytes at {extendFrom:X} to cover " +
                                       $"{cMax - cMin} byte(s) of out-of-range patch target(s) (no relocation, " +
                                       "addresses unchanged)");

                    uint regionOffset = (uint)(_data.Length + pendingBytes.Count);
                    pendingBytes.AddRange(new byte[extendSize]);

                    var newSeg = new ProgramHeader
                    {
                        Type = PT_LOAD, Offset = regionOffset, VAddr = extendFrom, PAddr = extendFrom,
                        FileSize = extendSize, MemSize = extendSize, Flags = 7, Align = GameConstants.PageSize,
                    };
                    pendingSegments.Add(newSeg);
                    allSegsSoFar.Add(newSeg);

                    if (extendFrom + extendSize > nextFreeVAddr)
                        nextFreeVAddr = extendFrom + extendSize;
                }
                }
            }

            if (pendingBytes.Count > 0 || pendingSegments.Count > 0)
            {
                var grown = new byte[_data.Length + pendingBytes.Count];
                Buffer.BlockCopy(_data, 0, grown, 0, _data.Length);
                pendingBytes.CopyTo(grown, _data.Length);
                _data = grown;
                Segs.AddRange(pendingSegments);
            }

            foreach (var (addr, val) in normal) WriteWord(addr, val);
        }

        // --- Final step: write the program header table back. ---
        //
        // Only relocate the program header table when the segment count
        // actually grew. Relocating it unconditionally used to be the
        // default here, but a moved e_phoff is exactly what "loadelf 3.30"
        // (the real loader, unlike lenient tools like uLaunchELF) chokes
        // on -- it's what produced the "pc=0x1fff000 addr=0x2000000" TLB
        // Miss on boot. If Segs.Count didn't change, write the table back
        // at its original offset so e_phoff/e_phnum stay untouched.
        // back at its original offset so e_phoff/e_phnum stay untouched.
        if (Segs.Count == ePhnum)
        {
            for (int i = 0; i < Segs.Count; i++)
                Segs[i].WriteInto(_data, (int)ePhoff + i * ePhentsize);
        }
        else
        {
            Console.WriteLine($"  NOTE: segment count grew ({ePhnum} -> {Segs.Count}); program header table " +
                               "must be relocated to fit the new entries. This changes e_phoff away from the " +
                               "original ELF's layout -- verify this build still boots via normal disc/ELF " +
                               "load (not just a lenient loader) before relying on it.");

            var finalData = new byte[_data.Length + Segs.Count * ProgramHeader.Size];
            Buffer.BlockCopy(_data, 0, finalData, 0, _data.Length);

            int newPhoff = _data.Length;
            for (int i = 0; i < Segs.Count; i++)
                Segs[i].WriteInto(finalData, newPhoff + i * ProgramHeader.Size);

            BitConverter.GetBytes(newPhoff).CopyTo(finalData, 28);
            BitConverter.GetBytes((ushort)Segs.Count).CopyTo(finalData, 44);

            _data = finalData;
        }

        uint after = ReadWord(GameConstants.SifSendCmdCaller);
        Console.WriteLine($"\nsceSifSendCmd caller @ {GameConstants.SifSendCmdCaller:X} AFTER: {after:08X}");
        if (after != before)
            throw new InvalidOperationException(
                "sceSifSendCmd caller was modified! Refusing to produce output -- " +
                "something in these pnach files touches the mastercode hook address.");

        // Validate ELF structure before returning
        ValidateElfStructure(_data, Segs);
        
        // Check for known TLB-risk patterns before returning
        CheckTlbRisk(_data, Segs);

        Console.WriteLine($"Patch pipeline complete: {_data.Length} bytes (was {origLen}, +{_data.Length - origLen})");
        return _data;
    }

    private static void ValidateElfStructure(byte[] data, List<ProgramHeader> segs)
    {
        Console.WriteLine("\n[VALIDATION] Checking ELF structure integrity...");

        var ptLoads = segs.Where(s => s.Type == PT_LOAD).ToList();

        // Every PT_LOAD must stay within the file's bounds
        foreach (var seg in ptLoads)
        {
            if (seg.Offset + seg.FileSize > (uint)data.Length)
                throw new InvalidDataException(
                    $"PT_LOAD segment out of bounds: offset={seg.Offset:X}, filesize={seg.FileSize:X}, data.len={data.Length:X}");
            
            if (seg.MemSize < seg.FileSize)
                throw new InvalidDataException(
                    $"PT_LOAD MemSize < FileSize: mem={seg.MemSize:X}, file={seg.FileSize:X}");
        }

        // The entry point must be covered by some PT_LOAD
        uint eEntry = BitConverter.ToUInt32(data, 24);
        bool entryMapped = ptLoads.Any(s => eEntry >= s.VAddr && eEntry < s.VAddr + s.FileSize);
        if (!entryMapped)
            throw new InvalidDataException(
                $"Entry point {eEntry:X} not covered by any PT_LOAD segment");

        // Program header table must sit within the file
        uint ePhoff = BitConverter.ToUInt32(data, 28);
        if (ePhoff >= (uint)data.Length)
            throw new InvalidDataException($"e_phoff {ePhoff:X} beyond file size");

        // No two PT_LOAD segments may overlap
        var loads = ptLoads.OrderBy(s => s.VAddr).ToList();
        for (int i = 0; i < loads.Count - 1; i++)
        {
            uint thisEnd = loads[i].VAddr + loads[i].MemSize;
            uint nextStart = loads[i + 1].VAddr;
            if (thisEnd > nextStart)
                throw new InvalidDataException(
                    $"PT_LOAD segment overlap: seg[{i}] end={thisEnd:X}, seg[{i + 1}] start={nextStart:X}");
        }

        Console.WriteLine("  All ELF header validations passed");
    }

    private static void CheckTlbRisk(byte[] data, List<ProgramHeader> segs)
    {
        var ptLoads = segs.Where(s => s.Type == PT_LOAD).ToList();
        uint eEntry = BitConverter.ToUInt32(data, 24);
        
        // Entry point should be mapped
        bool entryMapped = ptLoads.Any(s => eEntry >= s.VAddr && eEntry < s.VAddr + s.FileSize);
        
        if (!entryMapped)
        {
            Console.WriteLine("\nTLB RISK WARNING:");
            Console.WriteLine($"   Entry point {eEntry:X} is not covered by any PT_LOAD segment.");
            Console.WriteLine("   This patched ELF cannot be loaded directly as SLUS_216.21.");
            Console.WriteLine("\n   SOLUTION:");
            Console.WriteLine("   1. Use uLaunchELF as bootloader (replace SLUS_216.21 with uLaunchELF)");
            Console.WriteLine("   2. uLaunchELF loads this patched ELF from external location");
            Console.WriteLine("   3. uLaunchELF handles proper memory mapping → jump to entry point");
            Console.WriteLine("\n   DO NOT attempt to load this directly. Will cause TLB MISS crash.\n");
        }
    }

    private static uint AlignUp(uint value, uint align) => (value + align - 1) & ~(align - 1);

    private static IEnumerable<string> ExpandPnachPaths(string[] args)
    {
        foreach (var a in args)
        {
            if (Directory.Exists(a))
                foreach (var f in Directory.GetFiles(a, "*.pnach").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    yield return f;
            else
                yield return a;
        }
    }

    // Insert bytes into _data at an arbitrary offset, fixing up every
    // offset field past the insertion point (e_shoff, e_phoff, every
    // OTHER segment's p_offset). Used to grow an existing PT_LOAD in
    // place instead of appending a new one.
    // `growingSeg` is excluded from the fixup -- its own p_offset must
    // NOT move; only its FileSize/MemSize grow, handled by the caller.
    private static void InsertBytes(int insertOffset, byte[] newBytes, ProgramHeader growingSeg)
    {
        var grown = new byte[_data.Length + newBytes.Length];
        Buffer.BlockCopy(_data, 0, grown, 0, insertOffset);
        Buffer.BlockCopy(newBytes, 0, grown, insertOffset, newBytes.Length);
        Buffer.BlockCopy(_data, insertOffset, grown, insertOffset + newBytes.Length, _data.Length - insertOffset);
        _data = grown;

        uint delta = (uint)newBytes.Length;

        uint eShoff = BitConverter.ToUInt32(_data, 32);
        if (eShoff >= insertOffset) BitConverter.GetBytes(eShoff + delta).CopyTo(_data, 32);

        uint ePhoffCur = BitConverter.ToUInt32(_data, 28);
        if (ePhoffCur >= insertOffset) BitConverter.GetBytes(ePhoffCur + delta).CopyTo(_data, 28);

        // Section headers aren't used by the PS2 loader at boot (only
        // program headers are), but fix their sh_offset fields too so the
        // ELF stays fully correct for readelf/objdump/IDA/etc. e_shoff
        // was already adjusted above; re-read it post-adjustment since
        // that's where the (now-shifted) table itself physically lives.
        uint shoffNow = BitConverter.ToUInt32(_data, 32);
        ushort eShentsize = BitConverter.ToUInt16(_data, 46);
        ushort eShnum = BitConverter.ToUInt16(_data, 48);
        for (int i = 0; i < eShnum; i++)
        {
            int shOff = (int)shoffNow + i * eShentsize + 16; // sh_offset is the 5th uint32 field
            uint shOffset = BitConverter.ToUInt32(_data, shOff);
            if (shOffset >= insertOffset) BitConverter.GetBytes(shOffset + delta).CopyTo(_data, shOff);
        }

        foreach (var s in Segs)
        {
            if (ReferenceEquals(s, growingSeg)) continue;
            if (s.Offset >= insertOffset) s.Offset += delta;
        }
    }

    private static int FileOffsetFor(uint addr)
    {
        foreach (var s in Segs)
            if (s.Type == PT_LOAD && addr >= s.VAddr && addr < s.VAddr + s.FileSize)
                return checked((int)(s.Offset + (addr - s.VAddr)));
        return -1;
    }

    private static uint ReadWord(uint addr)
    {
        int off = FileOffsetFor(addr);
        if (off < 0) throw new InvalidDataException($"Address {addr:X} not in any segment.");
        return BitConverter.ToUInt32(_data, off);
    }

    private static void WriteWord(uint addr, uint val)
    {
        int off = FileOffsetFor(addr);
        if (off < 0) throw new InvalidDataException($"Address {addr:X} not in any segment -- cannot apply patch.");
        BitConverter.GetBytes(val).CopyTo(_data, off);
    }

    private static (Dictionary<uint, uint> normal, List<Dictionary<uint, uint>> forcedBlocks, List<string> conditionals)
        ParsePnach(string path)
    {
        var normal = new Dictionary<uint, uint>();
        var forcedBlocks = new List<Dictionary<uint, uint>>();
        var conditionals = new List<string>();
        Dictionary<uint, uint>? currentForced = null;

        // FIX: read into an array (not a streaming foreach) so a
        // conditional header can look ahead at its own payload lines.
        //
        // Root cause being fixed: CodeBreaker E-type/D-type codes are not
        // single lines -- the header (e.g. "e0020000") is followed by N
        // payload lines that only apply while the header's condition holds
        // (N is encoded in the header itself: for e0NNvvvv, NN is the
        // count). Those payload lines share their address with the OTHER
        // branch's payload lines (e.g. an if-true value and an if-false
        // value at the same target address). The previous version of this
        // parser only recognized the header line as "conditional" and let
        // every payload line fall through to the normal word-patch path --
        // so the two branches silently overwrote each other in `normal`,
        // and whichever branch was parsed last got permanently baked into
        // the ELF with no warning. That defeats the entire point of the
        // condition.
        //
        // Fix: when a header is found, read the next N lines (N = the
        // header's own count field) as ITS payload -- add all of them,
        // header included, to `conditionals`, and do not let them reach
        // `normal`. We still validate structurally rather than trusting N
        // blindly: only patch lines are counted (comments/blanks in
        // between are skipped without consuming the count), and if fewer
        // matching lines exist than N before EOF or another header, we
        // warn and treat whatever was found as the payload rather than
        // guessing further.
        var lines = File.ReadAllLines(path);
        int i = 0;
        while (i < lines.Length)
        {
            string s = lines[i].Trim();
            i++;

            if (s.Contains("CUSTOM CODE START", StringComparison.OrdinalIgnoreCase))
            {
                currentForced = new Dictionary<uint, uint>();
                continue;
            }
            if (s.Contains("CUSTOM CODE END", StringComparison.OrdinalIgnoreCase))
            {
                if (currentForced is { Count: > 0 }) forcedBlocks.Add(currentForced);
                currentForced = null;
                continue;
            }

            var m = PatchRe.Match(s);
            if (!m.Success) continue;

            string addrStr = m.Groups[2].Value, typeStr = m.Groups[3].Value, valStr = m.Groups[4].Value;

            bool isConditionalHeader =
                addrStr.StartsWith("e0", StringComparison.OrdinalIgnoreCase) ||
                addrStr.StartsWith("e1", StringComparison.OrdinalIgnoreCase) ||
                addrStr.StartsWith("d0", StringComparison.OrdinalIgnoreCase) ||
                addrStr.StartsWith("d1", StringComparison.OrdinalIgnoreCase) ||
                addrStr.StartsWith("d2", StringComparison.OrdinalIgnoreCase) ||
                addrStr.StartsWith("d3", StringComparison.OrdinalIgnoreCase);

            if (isConditionalHeader)
            {
                conditionals.Add(s);

                // Count field lives at addrStr[2..4] for both e0/e1/d0-d3
                // headers (verified against the CodeBreaker/ps2rd spec: the
                // header is TNNVVVVV where T=type nibble, NN=payload line
                // count). This is the SAME field Gemini's version read --
                // the difference is we don't just trust it silently.
                int expectedCount = 0;
                if (addrStr.Length >= 4 &&
                    int.TryParse(addrStr.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int parsedCount))
                {
                    expectedCount = parsedCount;
                }

                int consumed = 0;
                while (consumed < expectedCount && i < lines.Length)
                {
                    string payloadLine = lines[i].Trim();
                    var pm = PatchRe.Match(payloadLine);

                    if (!pm.Success)
                    {
                        // Blank line, comment-only line, etc. -- doesn't
                        // count against the payload budget, just skip it
                        // without consuming i beyond this iteration's
                        // increment below.
                        if (string.IsNullOrWhiteSpace(payloadLine) || payloadLine.StartsWith("//"))
                        {
                            i++;
                            continue;
                        }
                        // A non-empty, non-comment, non-patch line where a
                        // payload line was expected -- stop consuming here
                        // rather than guessing further.
                        break;
                    }

                    string payloadAddr = pm.Groups[2].Value;
                    bool payloadIsAnotherHeader =
                        payloadAddr.StartsWith("e0", StringComparison.OrdinalIgnoreCase) ||
                        payloadAddr.StartsWith("e1", StringComparison.OrdinalIgnoreCase) ||
                        payloadAddr.StartsWith("d0", StringComparison.OrdinalIgnoreCase) ||
                        payloadAddr.StartsWith("d1", StringComparison.OrdinalIgnoreCase) ||
                        payloadAddr.StartsWith("d2", StringComparison.OrdinalIgnoreCase) ||
                        payloadAddr.StartsWith("d3", StringComparison.OrdinalIgnoreCase);
                    if (payloadIsAnotherHeader)
                    {
                        // Another conditional header appeared before this
                        // one's declared count was satisfied -- the count
                        // in the source .pnach doesn't match its actual
                        // structure. Stop here and let the outer loop
                        // process that header on its own next iteration,
                        // rather than misattributing its payload to us.
                        Console.WriteLine($"  WARNING: conditional header '{s}' declared {expectedCount} " +
                                           $"payload line(s) but only {consumed} were found before the next " +
                                           "header -- pnach count mismatch, stopping this group here.");
                        break;
                    }

                    conditionals.Add(payloadLine);
                    consumed++;
                    i++;
                }

                if (consumed < expectedCount)
                {
                    Console.WriteLine($"  WARNING: conditional header '{s}' declared {expectedCount} " +
                                       $"payload line(s) but only {consumed} were found before end of file -- " +
                                       "pnach may be truncated or malformed.");
                }

                continue;
            }

            if (!typeStr.Equals("word", StringComparison.OrdinalIgnoreCase) &&
                !typeStr.Equals("extended", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  WARNING: patch type '{typeStr}' is not word-sized; skipping line: {s}");
                continue;
            }

            // Use TryParse instead of Parse so a malformed line is skipped with a warning, not a crash
            if (!uint.TryParse(addrStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint addrRaw))
            {
                Console.WriteLine($"  WARNING: invalid hex address '{addrStr}', skipping line: {s}");
                continue;
            }
            
            if (!uint.TryParse(valStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint val))
            {
                Console.WriteLine($"  WARNING: invalid hex value '{valStr}', skipping line: {s}");
                continue;
            }

            uint addr = addrRaw & 0x1FFFFFFFu;

            if (currentForced is not null) currentForced[addr] = val;
            else normal[addr] = val;
        }
        return (normal, forcedBlocks, conditionals);
    }

    // ============================================================
    // Exports skipped conditional (E-type/D-type) lines to a
    // PS2rd/OPL .cht file, per the official libcheats text format:
    //   https://github.com/mlafeldt/ps2rd/blob/master/Documentation/cheats_format.txt
    //
    // Format (verified against the spec, not guessed):
    //   "Game title /ID NAME SIZE"
    //   Cheat description 1
    //   AAAAAAAA VVVVVVVV
    //   AAAAAAAA VVVVVVVV
    //   Cheat description 2
    //   ...
    //
    // Design notes vs. a naive approach:
    // - We do NOT try to infer how many lines belong to one conditional
    //   group from a count embedded in the address (e.g. treating the
    //   "02" in e0020000 as "the next 2 lines belong to this group").
    //   That count is real in the CodeBreaker/PS2rd runtime format, but
    //   trusting it blindly from hand-edited community .pnach files is
    //   fragile -- a wrong count silently swallows unrelated lines into
    //   the wrong group with no warning. Instead we group by contiguous
    //   run in `conditionals` as already collected by ParsePnach (each
    //   E-type/D-type header immediately followed by its own address/value
    //   lines in the source file), which mirrors how the .pnach was
    //   actually written without re-deriving structure from a number that
    //   might be stale.
    // - Comments trailing a line (e.g. "//Set original value") are
    //   stripped before writing, since PS2rd's own comment syntax differs
    //   in placement and we must not leak pnach-style trailing comments
    //   into the AAAAAAAA VVVVVVVV code lines themselves.
    // ============================================================
    private static void ExportConditionalsToCht(List<string> conditionalLines, string outDir, string sourceName)
    {
        var codeLines = new List<string>();
        int groupNum = 0;
        bool inGroup = false;

        foreach (var raw in conditionalLines)
        {
            var (addrStr, valStr) = ParseRawPatchLine(raw);
            if (addrStr is null || valStr is null) continue;

            bool isHeader = addrStr.StartsWith("e0", StringComparison.OrdinalIgnoreCase) ||
                            addrStr.StartsWith("e1", StringComparison.OrdinalIgnoreCase) ||
                            addrStr.StartsWith("d0", StringComparison.OrdinalIgnoreCase) ||
                            addrStr.StartsWith("d1", StringComparison.OrdinalIgnoreCase) ||
                            addrStr.StartsWith("d2", StringComparison.OrdinalIgnoreCase) ||
                            addrStr.StartsWith("d3", StringComparison.OrdinalIgnoreCase);

            if (isHeader)
            {
                groupNum++;
                inGroup = true;
                codeLines.Add($"Conditional block {groupNum}");
            }
            else if (!inGroup)
            {
                // A payload line appeared without a preceding E/D header in
                // this collected list -- write it under its own group rather
                // than silently attaching it to the wrong one.
                groupNum++;
                codeLines.Add($"Conditional block {groupNum}");
            }

            codeLines.Add($"{addrStr.ToUpperInvariant()} {valStr.ToUpperInvariant()}");
        }

        if (codeLines.Count == 0) return;

        Directory.CreateDirectory(outDir);

        // --- Output 1: PS2rd/OPL .cht format (per official spec) ---
        // PS2rd has its own cheat-engine hook baked into the emulator/loader
        // itself -- it does NOT need this project's mastercode line, because
        // it isn't running these codes through the same in-game CodeBreaker
        // hook. The mastercode is specific to the raw CodeBreaker device
        // path (Output 2 below).
        string chtPath = Path.Combine(outDir, $"{sourceName}.cht");
        var chtOutput = new List<string>
        {
            "\"Persona 3 FES conditional cheats (exported) /ID SLUS_216.21\"",
            "// Auto-exported from skipped E-type/D-type pnach lines.",
            "// These require PS2RD's live per-frame evaluation and cannot",
            "// be baked into the ELF -- see P3FesPnachPatcher README.",
            ""
        };
        chtOutput.AddRange(codeLines);
        chtOutput.Add("");
        File.WriteAllLines(chtPath, chtOutput);

        // --- Output 2: raw CodeBreaker .txt, WITH the required mastercode ---
        // FIX: this project's CodeBreaker setup depends on the
        // sceSifSendCmd mastercode being present as the FIRST code in the
        // list -- it's what lets the cheat device hook into the game's
        // frame loop at all. Earlier versions of this exporter only wrote
        // the .cht (PS2rd) file, so a user running these codes through
        // CodeBreaker directly (not PS2rd) got codes that show as
        // "enabled" but never actually execute, because nothing hooked
        // the frame loop for them. This is the fix for that gap.
        string cbPath = Path.Combine(outDir, $"{sourceName}_CodeBreaker.txt");
        var cbOutput = new List<string>
        {
            $"\"Persona 3 FES conditional cheats (exported)\"",
            "Mastercode (required -- do not remove or reorder)",
            GameConstants.CodeBreakerMastercode,
            ""
        };
        cbOutput.AddRange(codeLines);
        cbOutput.Add("");
        File.WriteAllLines(cbPath, cbOutput);

        Console.WriteLine($"  Exported {groupNum} conditional group(s) ({codeLines.Count - groupNum} code line(s)):");
        Console.WriteLine($"    PS2rd/OPL format: {chtPath}");
        Console.WriteLine($"    Raw CodeBreaker format (includes required mastercode {GameConstants.CodeBreakerMastercode}): {cbPath}");
        Console.WriteLine("    If using CodeBreaker directly, use the _CodeBreaker.txt file, not the .cht -- " +
                           "the mastercode is what makes the cheat device hook into the game's frame loop. " +
                           "Without it, codes can show as enabled and still do nothing.");
    }

    // Parses a single raw pnach line (already known to match PatchRe) back
    // into its address/value hex strings, without re-running full
    // validation -- ParsePnach already validated these lines once; this is
    // purely a re-extraction for the export step operating on the stored
    // raw strings.
    private static (string? addr, string? val) ParseRawPatchLine(string raw)
    {
        var m = PatchRe.Match(raw);
        if (!m.Success) return (null, null);
        return (m.Groups[2].Value, m.Groups[4].Value);
    }

    private static (uint[] words, List<(uint addr, uint instr, uint origTarget)> entries, uint regionSize)
        RelocateBlock(Dictionary<uint, uint> block, Dictionary<uint, uint> normalPatches, uint newBase)
    {
        uint cmin = block.Keys.Min();
        uint cmaxIncl = block.Keys.Max();
        uint cmax = cmaxIncl + 4;
        int nWords = (int)((cmax - cmin) / 4);

        var words = new uint[nWords];
        foreach (var (addr, val) in block) words[(addr - cmin) / 4] = val;

        long delta = (long)newBase - cmin;
        for (int i = 0; i < words.Length; i++)
        {
            uint val = words[i];
            uint op = (val >> 26) & 0x3F;
            if (op == 2 || op == 3)
            {
                uint field = val & 0x03FFFFFFu;
                uint target = field << 2;
                if (target >= cmin && target < cmax)
                {
                    uint newTarget = (uint)(target + delta);
                    uint newField = (newTarget >> 2) & 0x03FFFFFFu;
                    words[i] = (op << 26) | newField;
                    Console.WriteLine($"      fixed internal {(op == 3 ? "jal" : "j")} at block+0x{i * 4:X}: {target:X} -> {newTarget:X}");
                }
            }
        }

        // KNOWN OPEN LIMITATION (see PROGRESS LOG at top of file): detect
        // -- but do NOT auto-fix -- lui/ori pairs forming an address inside
        // this relocated block. Unlike j/jal, the bit pattern is ambiguous
        // (could be a real address or a coincidental integer constant), so
        // this is only reported as a candidate for manual review.
        DetectLikelyAddressLoads(words, cmin, cmax, "inside relocated block");

        var entries = new List<(uint, uint, uint)>();
        foreach (var (addr, val) in normalPatches)
        {
            uint op = (val >> 26) & 0x3F;
            if (op != 2 && op != 3) continue;
            uint field = val & 0x03FFFFFFu;
            uint target = field << 2;
            if (target >= cmin && target < cmax)
            {
                uint newTarget = newBase + (target - cmin);
                uint newField = (newTarget >> 2) & 0x03FFFFFFu;
                entries.Add((addr, (op << 26) | newField, target));
            }
        }

        if (entries.Count == 0)
            Console.WriteLine("      WARNING: no entry jump found for this block -- relocated but nothing calls it. Verify manually.");

        // Same scan applied to OUTSIDE-block patches: code that may load
        // (not jump to) an address now living inside the relocated region.
        var outsideWords = normalPatches
            .Where(kv => (kv.Value >> 26 & 0x3F) == 0x0F) // lui only, ori has no fixed high bits to pre-filter on
            .OrderBy(kv => kv.Key)
            .ToList();
        if (outsideWords.Count > 0)
        {
            var orderedOutside = normalPatches.OrderBy(kv => kv.Key).ToList();
            DetectLikelyAddressLoadsInSparsePatchSet(orderedOutside, cmin, cmax, "outside block, targets relocated range");
        }

        return (words, entries, (uint)(cmax - cmin));
    }

    // lui/ori pair detection -- report-only, never auto-fixed.
    // Scans a contiguous run of words (as they sit inside one relocated
    // block) for `lui $r, hi` immediately followed by `ori $r, lo` (or
    // `addiu $r, $r, lo`) into the SAME register, forming a 32-bit
    // constant. If that constant falls inside [cmin, cmax) -- i.e. it
    // numerically matches an address that is part of this relocation --
    // it's printed as a candidate. The user (or mod author) has to decide
    // whether it's really an address reference or just a coincidental
    // integer constant; the tool refuses to guess.
    private static void DetectLikelyAddressLoads(uint[] words, uint cmin, uint cmax, string context)
    {
        for (int i = 0; i + 1 < words.Length; i++)
        {
            uint hi = words[i];
            uint hiOp = (hi >> 26) & 0x3F;
            if (hiOp != 0x0F) continue; // lui
            uint hiReg = (hi >> 16) & 0x1F; // rt field holds the destination for lui

            uint lo = words[i + 1];
            uint loOp = (lo >> 26) & 0x3F;
            bool isOri = loOp == 0x0D;   // ori
            bool isAddiu = loOp == 0x09; // addiu
            if (!isOri && !isAddiu) continue;

            uint loRs = (lo >> 21) & 0x1F; // source reg for ori/addiu
            uint loRt = (lo >> 16) & 0x1F; // dest reg for ori/addiu
            if (loRs != hiReg || loRt != hiReg) continue; // must chain into the same register

            uint hiImm = hi & 0xFFFF;
            uint loImm = lo & 0xFFFF;
            uint candidate = (hiImm << 16) + loImm; // '+' matches addiu's sign-extend behavior; ori would use OR, close enough for candidate detection

            if (candidate >= cmin && candidate < cmax)
            {
                Console.WriteLine($"      CANDIDATE li ({context}) at word+0x{i * 4:X}: " +
                                   $"lui/{(isAddiu ? "addiu" : "ori")} $r{hiReg} -> {candidate:X} " +
                                   "-- NOT auto-fixed (could be a real address OR a coincidental integer). Review manually.");
            }
        }
    }

    // Same detection, but over a sparse (address -> instruction) patch set
    // instead of a contiguous word array -- used for patches OUTSIDE the
    // relocated block, which are not necessarily adjacent to each other in
    // memory the way words inside one block are. Only adjacent-by-address
    // pairs (consecutive patch lines 4 bytes apart) are checked, since a
    // lui/ori pair must be consecutive instructions to be valid MIPS.
    private static void DetectLikelyAddressLoadsInSparsePatchSet(
        List<KeyValuePair<uint, uint>> ordered, uint cmin, uint cmax, string context)
    {
        for (int i = 0; i + 1 < ordered.Count; i++)
        {
            var (addrHi, hi) = (ordered[i].Key, ordered[i].Value);
            var (addrLo, lo) = (ordered[i + 1].Key, ordered[i + 1].Value);
            if (addrLo != addrHi + 4) continue; // must be the very next instruction

            uint hiOp = (hi >> 26) & 0x3F;
            if (hiOp != 0x0F) continue;
            uint hiReg = (hi >> 16) & 0x1F;

            uint loOp = (lo >> 26) & 0x3F;
            bool isOri = loOp == 0x0D;
            bool isAddiu = loOp == 0x09;
            if (!isOri && !isAddiu) continue;

            uint loRs = (lo >> 21) & 0x1F;
            uint loRt = (lo >> 16) & 0x1F;
            if (loRs != hiReg || loRt != hiReg) continue;

            uint hiImm = hi & 0xFFFF;
            uint loImm = lo & 0xFFFF;
            uint candidate = (hiImm << 16) + loImm;

            if (candidate >= cmin && candidate < cmax)
            {
                Console.WriteLine($"      CANDIDATE li ({context}) at {addrHi:X}: " +
                                   $"lui/{(isAddiu ? "addiu" : "ori")} $r{hiReg} -> {candidate:X} " +
                                   "-- NOT auto-fixed (could be a real address OR a coincidental integer). Review manually.");
            }
        }
    }
}
