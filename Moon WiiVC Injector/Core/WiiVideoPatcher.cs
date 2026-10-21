using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Moon_WiiVC_Injector.Services;

namespace Moon_WiiVC_Injector.Core;

/// <summary>
/// Deflicker (vertical filter) modes, mirroring USB Loader GX.
/// </summary>
public enum DeflickerMode
{
    /// <summary>Leave the game untouched.</summary>
    Keep = 0,
    /// <summary>Replace all known vfilter tables with a pass-through filter (0,0,21,22,21,0,0).</summary>
    OffSafe = 1,
    /// <summary>OffSafe plus patching GXSetCopyFilter so the game cannot re-enable the filter. May break fades/brightness effects.</summary>
    OffExtended = 2,
    /// <summary>Low-strength filter (4,4,16,16,16,4,4).</summary>
    Low = 3,
    /// <summary>Medium-strength filter (4,8,12,16,12,8,4).</summary>
    Medium = 4,
    /// <summary>High-strength filter, the SDK default (8,8,10,12,10,8,8).</summary>
    High = 5,
}

/// <summary>Result of a patch run, for logging and UI feedback.</summary>
public sealed record WiiVideoPatchResult(
    int VideoModeTablesPatched,
    int RogueFiltersPatched,
    bool CopyFilterPatched,
    bool DitheringPatched,
    bool SkippedExcludedGame,
    bool FramebufferWidthPatched = false)
{
    public bool AnyPatched => VideoModeTablesPatched > 0 || RogueFiltersPatched > 0 || CopyFilterPatched || DitheringPatched || FramebufferWidthPatched;
}

/// <summary>
/// Patches a Wii main.dol to remove or adjust the deflicker (vertical) filter and, optionally, dithering.
///
/// Ported from USB Loader GX (source/patches/gamepatches.c, GPL-2.0-or-later):
/// patch_vfilters, patch_vfilters_rogue, deflicker_patch, dithering_patch, and patch_width
/// ("Video Width: Framebuffer", together with find_safe_space).
/// https://github.com/wiidev/usbloadergx
///
/// USB Loader GX applies these to the game's DOL sections in memory at boot. Injected games cannot be
/// patched at runtime, so the same patches are applied to main.dol on disc instead.
/// </summary>
public static class WiiVideoPatcher
{
    private const string Tag = "[WiiVideoPatcher]";

    // Filter presets (identical to USB Loader GX)
    private static readonly byte[] VFilterOff = [0, 0, 21, 22, 21, 0, 0];
    private static readonly byte[] VFilterLow = [4, 4, 16, 16, 16, 4, 4];
    private static readonly byte[] VFilterMedium = [4, 8, 12, 16, 12, 8, 4];
    private static readonly byte[] VFilterHigh = [8, 8, 10, 12, 10, 8, 8];

    // Filters found outside GXRModeObj structures in some games
    private static readonly byte[][] KnownRogueVFilters =
    [
        [8, 8, 10, 12, 10, 8, 8],
        [4, 8, 12, 16, 12, 8, 4],
        [7, 7, 12, 12, 12, 7, 7],
        [5, 5, 15, 14, 15, 5, 5],
        [4, 4, 15, 18, 15, 4, 4],
        [4, 4, 16, 16, 16, 4, 4],
        [2, 2, 17, 22, 17, 2, 2],
    ];

    // GXRModeObj layout (big-endian, 0x3C bytes)
    private const int GXRModeObjSize = 0x3C;
    private const int OffFbWidth = 0x04;
    private const int OffFieldRendering = 0x18;
    private const int OffAa = 0x19;
    private const int OffSamplePattern = 0x1A; // 12 x 2 bytes
    private const int OffVFilter = 0x32;       // 7 bytes

    private static readonly byte[] SamplePattern =
        [6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6];

    private static readonly byte[] SamplePatternAa =
        [3, 2, 9, 6, 3, 10, 3, 2, 9, 6, 3, 10, 9, 2, 3, 6, 9, 10, 9, 2, 3, 6, 9, 10];

    // GXSetCopyFilter tail; the last word (beq) becomes an unconditional branch
    private static readonly uint[] CopyFilterPattern =
    [
        0x3D20CC01, 0x39400061, 0x99498000,
        0x2C050000, 0x38800053, 0x39600000,
        0x90098000, 0x38000054, 0x39800000,
        0x508BC00E, 0x99498000, 0x500CC00E,
        0x90698000, 0x99498000, 0x90E98000,
        0x99498000, 0x91098000, 0x41820040,
    ];
    private const uint CopyFilterReplacement = 0x48000040; // beq -> b

    // GXSetDither; the word before the match becomes a branch past the function body
    private static readonly uint[] DitherPattern =
    [
        0x3C80CC01, 0x38A00061,
        0x38000000, 0x80C70220,
        0x5066177A, 0x98A48000,
        0x90C48000, 0x90C70220,
        0xB0070002, 0x4E800020,
    ];
    private const uint DitherReplacement = 0x48000028;

    /// <summary>
    /// Games USB Loader GX excludes from these patches because they break.
    /// Prince of Persia, Driver, Tintin and We Dare.
    /// </summary>
    private static readonly string[] ExcludedIdPrefixes = ["RPW", "SPX", "SDV", "STN", "SLVP41"];

    public static bool IsExcludedGame(string? gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return false;
        foreach (var prefix in ExcludedIdPrefixes)
        {
            if (gameId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Reads the 6-character game ID from an extracted disc's sys/boot.bin, if present.</summary>
    public static string? ReadGameId(string extractedDiscDir)
    {
        string bootBin = Path.Combine(extractedDiscDir, "sys", "boot.bin");
        if (!File.Exists(bootBin)) return null;
        using var fs = File.OpenRead(bootBin);
        Span<byte> id = stackalloc byte[6];
        if (fs.Read(id) != 6) return null;
        return Encoding.ASCII.GetString(id);
    }

    /// <summary>
    /// Patches a main.dol file in place.
    /// </summary>
    public static WiiVideoPatchResult PatchFile(string dolPath, DeflickerMode mode, bool removeDithering, string? gameId = null, bool framebufferWidth = false)
    {
        if (!File.Exists(dolPath))
        {
            AppLogger.Warning($"{Tag} File not found: {dolPath}");
            return new WiiVideoPatchResult(0, 0, false, false, false);
        }

        byte[] dol = File.ReadAllBytes(dolPath);
        var result = Patch(dol, mode, removeDithering, gameId, framebufferWidth);

        if (result.AnyPatched)
        {
            File.WriteAllBytes(dolPath, dol);
            AppLogger.Info($"{Tag} Patched DOL written back to disk.");
        }
        return result;
    }

    /// <summary>
    /// Patches a DOL image in memory and logs what was found.
    /// </summary>
    public static WiiVideoPatchResult Patch(byte[] dol, DeflickerMode mode, bool removeDithering, string? gameId = null, bool framebufferWidth = false)
    {
        ArgumentNullException.ThrowIfNull(dol);

        if (IsExcludedGame(gameId))
        {
            AppLogger.Warning($"{Tag} {gameId} is known to break with video patches (USB Loader GX exclusion list). Skipping.");
            return new WiiVideoPatchResult(0, 0, false, false, true);
        }

        int tables = 0, rogue = 0;
        bool copyFilter = false, dither = false;

        byte[]? filter = mode switch
        {
            DeflickerMode.OffSafe or DeflickerMode.OffExtended => VFilterOff,
            DeflickerMode.Low => VFilterLow,
            DeflickerMode.Medium => VFilterMedium,
            DeflickerMode.High => VFilterHigh,
            _ => null,
        };

        if (filter != null)
        {
            tables = PatchVideoModeTables(dol, filter);
            rogue = PatchRogueVFilters(dol, filter);
            AppLogger.Info($"{Tag} Deflicker {mode}: {tables} video mode table(s), {rogue} standalone filter(s) patched.");

            if (mode == DeflickerMode.OffExtended)
            {
                copyFilter = PatchCopyFilter(dol);
                AppLogger.Info(copyFilter
                    ? $"{Tag} GXSetCopyFilter patched (filter can no longer be re-enabled by the game)."
                    : $"{Tag} GXSetCopyFilter pattern not found; only the table patches apply.");
            }

            if (tables == 0 && rogue == 0 && !copyFilter)
                AppLogger.Warning($"{Tag} No deflicker filter found in main.dol. The filter may live in a REL module or use an unknown pattern.");
        }

        if (removeDithering)
        {
            dither = PatchDithering(dol);
            AppLogger.Info(dither
                ? $"{Tag} GXSetDither patched (dithering disabled; may cause colour banding)."
                : $"{Tag} GXSetDither pattern not found; dithering unchanged.");
        }

        bool width = false;
        if (framebufferWidth)
        {
            width = PatchFramebufferWidth(dol);
            AppLogger.Info(width
                ? $"{Tag} VIConfigure patched: VI width = framebuffer width (no horizontal VI scaling), image centered."
                : $"{Tag} VIConfigure width pattern or safe space not found; video width unchanged.");
        }

        return new WiiVideoPatchResult(tables, rogue, copyFilter, dither, false, width);
    }

    // ---- Video width = framebuffer width (port of patch_width / find_safe_space) ----

    private static readonly byte[] SafeSpacePatternA = [0x80, 0x1E, 0x00, 0x00, 0x3C, 0x60, 0x80, 0x00, 0x83]; // most games
    private static readonly byte[] SafeSpacePatternB = [0x80, 0x1F, 0x00, 0x00, 0x3C, 0x60, 0x80, 0x00, 0x83]; // Mortal Kombat

    private static readonly byte[] WidthPattern =
    [
        0x40, 0x82, 0x00, 0x08, 0x48, 0x00, 0x00, 0x1C,
        0x28, 0x09, 0x00, 0x03, 0x40, 0x82, 0x00, 0x08,
        0x48, 0x00, 0x00, 0x10, 0x2C, 0x03, 0x00, 0x00,
        0x40, 0x82, 0x00, 0x08, 0x54, 0xA5, 0x0C, 0x3C,
    ];

    /// <summary>
    /// Makes VIConfigure use the framebuffer width (GXRModeObj.fbWidth, usually 640) as the VI output width
    /// instead of viWidth (usually 670-720), so the VI no longer resamples the image horizontally, and
    /// re-centers it with viXOrigin = (720 - fbWidth) / 2. A small code cave is placed at the "safe space"
    /// USB Loader GX uses. Branch offsets are computed from virtual addresses via the DOL section table,
    /// because the DOL is patched on disk rather than in memory.
    /// </summary>
    internal static bool PatchFramebufferWidth(byte[] dol)
    {
        int safe = FindSafeSpace(dol);
        if (safe < 0) return false;
        int cave = safe + 12; // first crclr after the safe-space marker, as in USB Loader GX
        if (cave + 0x14 > dol.Length) return false;

        for (int i = 0x70; i <= dol.Length - WidthPattern.Length; i += 4)
        {
            if (!dol.AsSpan(i, WidthPattern.Length).SequenceEqual(WidthPattern)) continue;
            if (!(dol[i - 0x70] == 0xA0 && dol[i - 0x6E] == 0x00 && dol[i - 0x6D] == 0x0A)) continue; // lhz rA, 0x0A(rX)  viXOrigin
            if (!(dol[i - 0x44] == 0xA0 && dol[i - 0x42] == 0x00 && dol[i - 0x41] == 0x0E)) continue; // lhz rB, 0x0E(rX)  viWidth

            uint caveVa = FileOffsetToVirtual(dol, cave);
            uint siteVa = FileOffsetToVirtual(dol, i - 0x70);
            if (caveVa == 0 || siteVa == 0) return false;

            uint regA = (uint)(dol[i - 0x6F] >> 5);
            uint regB = (uint)(dol[i - 0x43] >> 5);

            // viWidth load -> fbWidth load
            dol[i - 0x41] = 0x04;

            // Code cave: rA = fbWidth; rB = 720 - rA; rA = 2; rA = rB / rA; branch back
            uint orig = (uint)(dol[i - 0x70] << 24 | dol[i - 0x6F] << 16);
            WriteWord(dol, cave + 0x00, orig | 4);                                        // lhz rA, 0x04(rX)
            WriteWord(dol, cave + 0x04, 0x200002D0u | (regB << 21) | (regA << 16));       // subfic rB, rA, 720
            WriteWord(dol, cave + 0x08, 0x38000002u | (regA << 21));                      // li rA, 2
            WriteWord(dol, cave + 0x0C, 0x7C000396u | (regA << 21) | (regB << 16) | (regA << 11)); // divwu rA, rB, rA
            WriteWord(dol, cave + 0x10, 0x48000000u + (((siteVa + 4) - (caveVa + 0x10)) & 0x3FFFFFF)); // b site+4
            WriteWord(dol, i - 0x70, 0x48000000u + ((caveVa - siteVa) & 0x3FFFFFF));      // b cave
            return true;
        }
        return false;
    }

    private static int FindSafeSpace(byte[] dol)
    {
        for (int i = 0; i <= dol.Length - 40; i += 4)
        {
            var s = dol.AsSpan(i, SafeSpacePatternA.Length);
            if ((s.SequenceEqual(SafeSpacePatternA) || s.SequenceEqual(SafeSpacePatternB))
                && BinaryPrimitives.ReadUInt32BigEndian(dol.AsSpan(i + 36, 4)) == 0x38000001)
            {
                return i + 36;
            }
        }
        return -1;
    }

    /// <summary>Maps a file offset inside a DOL text/data section to its load address (0 if not in a section).</summary>
    internal static uint FileOffsetToVirtual(byte[] dol, int fileOffset)
    {
        if (dol.Length < 0x100) return 0;
        for (int s = 0; s < 18; s++)
        {
            uint off = BinaryPrimitives.ReadUInt32BigEndian(dol.AsSpan(0x00 + s * 4, 4));
            uint addr = BinaryPrimitives.ReadUInt32BigEndian(dol.AsSpan(0x48 + s * 4, 4));
            uint size = BinaryPrimitives.ReadUInt32BigEndian(dol.AsSpan(0x90 + s * 4, 4));
            if (size == 0) continue;
            if ((uint)fileOffset >= off && (uint)fileOffset < off + size)
                return addr + ((uint)fileOffset - off);
        }
        return 0;
    }

    /// <summary>
    /// Finds GXRModeObj structures by their sample pattern and sanity checks, and replaces their vfilter.
    /// Port of patch_vfilters.
    /// </summary>
    internal static int PatchVideoModeTables(byte[] buf, byte[] vfilter)
    {
        int count = 0;
        int i = 0;
        while (i + GXRModeObjSize <= buf.Length)
        {
            if (IsVideoModeObject(buf, i))
            {
                Buffer.BlockCopy(vfilter, 0, buf, i + OffVFilter, 7);
                count++;
                i += GXRModeObjSize - 4;
            }
            i += 4;
        }
        return count;
    }

    private static bool IsVideoModeObject(byte[] buf, int i)
    {
        var pattern = buf.AsSpan(i + OffSamplePattern, 24);
        if (!pattern.SequenceEqual(SamplePattern) && !pattern.SequenceEqual(SamplePatternAa))
            return false;

        ushort fbWidth = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(i + OffFbWidth, 2));
        if (fbWidth != 640 && fbWidth != 608 && fbWidth != 512)
            return false;

        byte fieldRendering = buf[i + OffFieldRendering];
        byte aa = buf[i + OffAa];
        return fieldRendering <= 1 && aa <= 1;
    }

    /// <summary>
    /// Replaces known vfilter arrays that are stored outside GXRModeObj structures.
    /// Port of patch_vfilters_rogue.
    /// </summary>
    internal static int PatchRogueVFilters(byte[] buf, byte[] vfilter)
    {
        int count = 0;
        int i = 0;
        while (i + 8 <= buf.Length)
        {
            // Skip filters that already hold the target values (e.g. tables patched just before)
            if (buf[i + 7] == 0 && !buf.AsSpan(i, 7).SequenceEqual(vfilter))
            {
                foreach (var known in KnownRogueVFilters)
                {
                    if (buf.AsSpan(i, 7).SequenceEqual(known))
                    {
                        Buffer.BlockCopy(vfilter, 0, buf, i, 7);
                        count++;
                        i += 7;
                        break;
                    }
                }
            }
            i += 1;
        }
        return count;
    }

    /// <summary>Port of deflicker_patch: makes GXSetCopyFilter always take the "no vertical filter" path.</summary>
    internal static bool PatchCopyFilter(byte[] buf)
    {
        int at = FindWords(buf, CopyFilterPattern);
        if (at < 0) return false;
        WriteWord(buf, at + (CopyFilterPattern.Length - 1) * 4, CopyFilterReplacement);
        AppLogger.Info($"{Tag} GXSetCopyFilter found at file offset 0x{at:X}.");
        return true;
    }

    /// <summary>Port of dithering_patch: skips the body of GXSetDither.</summary>
    internal static bool PatchDithering(byte[] buf)
    {
        int at = FindWords(buf, DitherPattern);
        if (at < 4) return false;
        WriteWord(buf, at - 4, DitherReplacement);
        AppLogger.Info($"{Tag} GXSetDither found at file offset 0x{at:X}.");
        return true;
    }

    private static int FindWords(byte[] buf, uint[] words)
    {
        int len = words.Length * 4;
        byte[] needle = new byte[len];
        for (int w = 0; w < words.Length; w++)
            BinaryPrimitives.WriteUInt32BigEndian(needle.AsSpan(w * 4, 4), words[w]);

        for (int i = 0; i + len <= buf.Length; i += 4)
        {
            if (buf.AsSpan(i, len).SequenceEqual(needle))
                return i;
        }
        return -1;
    }

    private static void WriteWord(byte[] buf, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset, 4), value);
}
