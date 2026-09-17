using System;
using System.IO;
using System.Text;

namespace Atypik.LLM;

/// <summary>
/// Coarse model "family" the loaded GGUF belongs to, so each mode's prompt
/// (correction, rewrite, poetry) can be tuned to it automatically when the user
/// swaps models. See <see cref="ModelProfile"/>.
/// </summary>
public enum ModelFamily
{
    /// <summary>Small instruct model (Qwen-class). The base prompts target this.</summary>
    Default,

    /// <summary>Chain-of-thought / "thinking" model that emits reasoning traces.</summary>
    Thinking,
}

/// <summary>What we learned about the loaded model.</summary>
public readonly record struct ModelInfo(ModelFamily Family, string? Name, string? Architecture);

/// <summary>
/// Detects the model family so the correct prompt variant is chosen for every
/// mode. Detection reads the GGUF header (<c>general.name</c> / <c>general.architecture</c>)
/// FIRST - this keeps working even when the file was renamed (the app stores its
/// managed model as <c>textualiser.gguf</c>, so the file name alone tells us
/// nothing) - then falls back to the file name for anything the header misses.
///
/// The mechanism is a short family-specific PREAMBLE prepended to each base
/// prompt (one source file per mode, no duplication). <see cref="ModelFamily.Default"/>
/// yields an empty preamble, so Qwen-class models use the base prompts unchanged.
/// Adding a new family later = one enum value + one preamble, no other changes.
/// </summary>
public static class ModelProfile
{
    // Lower-cased substrings that mark a chain-of-thought / reasoning model.
    private static readonly string[] ThinkingTokens =
    {
        "think", "thinker", "reason", "qwq", "-r1", "r1-", "_r1",
        "-cot", "o1-", "marco-o1", "deepseek-r1", "vibethinker",
    };

    /// <summary>Family only (convenience over <see cref="Inspect"/>).</summary>
    public static ModelFamily Detect(string? modelPath) => Inspect(modelPath).Family;

    /// <summary>
    /// Inspect the model: read its GGUF header for a name/architecture, classify
    /// the family, and fall back to the file name. Never throws.
    /// </summary>
    public static ModelInfo Inspect(string? modelPath)
    {
        string? name = null, arch = null;
        if (!string.IsNullOrEmpty(modelPath) && File.Exists(modelPath))
            (name, arch) = ReadGgufMeta(modelPath!);

        // The model NAME is the useful signal for "thinking"; architecture is
        // family-level (e.g. "qwen2") and rarely marks reasoning, but we include
        // it for completeness. File name is the last resort.
        var family = Classify(name);
        if (family == ModelFamily.Default) family = Classify(arch);
        if (family == ModelFamily.Default) family = Classify(Path.GetFileName(modelPath ?? string.Empty));

        return new ModelInfo(family, name, arch);
    }

    /// <summary>
    /// A short family-specific instruction prepended to every mode prompt.
    /// Empty for <see cref="ModelFamily.Default"/> (base prompts untouched).
    /// </summary>
    public static string Preamble(ModelFamily family) => family switch
    {
        ModelFamily.Thinking =>
            "Do not think out loud. Do not write any reasoning, analysis, notes, or <think> tags. "
          + "Reply with only the final result described below - nothing before it and nothing after it.",
        _ => string.Empty,
    };

    private static ModelFamily Classify(string? text)
    {
        if (string.IsNullOrEmpty(text)) return ModelFamily.Default;
        string s = text.ToLowerInvariant();
        foreach (var token in ThinkingTokens)
            if (s.Contains(token, StringComparison.Ordinal))
                return ModelFamily.Thinking;
        return ModelFamily.Default;
    }

    // -- Minimal GGUF header reader -------------------------------------------
    // GGUF: "GGUF" magic, uint32 version, uint64 tensor_count, uint64 kv_count,
    // then kv_count entries of {string key, uint32 type, value}. The general.*
    // keys sit at the very top, so we read only the header and stop early.

    private const uint GgufMagic = 0x46554747; // 'G','G','U','F' little-endian

    private static (string? name, string? arch) ReadGgufMeta(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 1 << 16);
            using var br = new BinaryReader(fs);

            if (br.ReadUInt32() != GgufMagic) return (null, null);
            uint version = br.ReadUInt32();
            if (version < 2 || version > 3) return (null, null);   // v1 used int32 counts
            _ = br.ReadUInt64();                                    // tensor_count
            ulong kvCount = br.ReadUInt64();

            string? name = null, arch = null;
            for (ulong i = 0; i < kvCount; i++)
            {
                string key = ReadGgufString(br);
                uint type = br.ReadUInt32();
                object? val = ReadGgufValue(br, type);

                if (key == "general.name" && val is string sn) name = sn;
                else if (key == "general.architecture" && val is string sa) arch = sa;

                if (name is not null && arch is not null) break;   // got both
                if (fs.Position > 4_000_000) break;                // safety: general.* are at the top
            }
            return (name, arch);
        }
        catch { return (null, null); }   // never block model load on a header quirk
    }

    private static string ReadGgufString(BinaryReader br)
    {
        ulong len = br.ReadUInt64();
        if (len > 1_000_000) throw new InvalidDataException("gguf string too long");
        byte[] bytes = br.ReadBytes((int)len);
        return Encoding.UTF8.GetString(bytes);
    }

    // Reads (and thereby skips) one value so the cursor lands on the next KV.
    private static object? ReadGgufValue(BinaryReader br, uint type)
    {
        switch (type)
        {
            case 0:  return br.ReadByte();                 // uint8
            case 1:  return br.ReadSByte();                // int8
            case 2:  return br.ReadUInt16();               // uint16
            case 3:  return br.ReadInt16();                // int16
            case 4:  return br.ReadUInt32();               // uint32
            case 5:  return br.ReadInt32();                // int32
            case 6:  return br.ReadSingle();               // float32
            case 7:  return br.ReadByte() != 0;            // bool (1 byte)
            case 8:  return ReadGgufString(br);            // string
            case 10: return br.ReadUInt64();               // uint64
            case 11: return br.ReadInt64();                // int64
            case 12: return br.ReadDouble();               // float64
            case 9:                                        // array
            {
                uint elemType = br.ReadUInt32();
                ulong count = br.ReadUInt64();
                for (ulong j = 0; j < count; j++) ReadGgufValue(br, elemType);
                return null;
            }
            default: throw new InvalidDataException($"unknown gguf value type {type}");
        }
    }
}
