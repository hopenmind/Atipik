using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Atypik.KineticEngine;

/// <summary>
/// 2D keyboard layout matrix for topographical error injection.
///
/// Replaces the naive linear string approach - adjacency is spatial,
/// not sequential. A real typo is a neighboring key on the physical grid.
///
/// Supports both AZERTY (FR) and QWERTY (EN) layouts.
/// </summary>
public sealed class LayoutMatrix
{
    // [row][col] = character
    private readonly char[][] _grid;
    private readonly Dictionary<char, (int Row, int Col)> _index;

    /// <summary>The concrete layout this matrix was built from (never Auto).</summary>
    public KeyboardLayout ActiveLayout { get; }

    // -- AZERTY layout (French standard) --------------------------------------
    private static readonly char[][] AzertyGrid =
    [
        ['a','z','e','r','t','y','u','i','o','p'],
        ['q','s','d','f','g','h','j','k','l','m'],
        ['w','x','c','v','b','n',',',';',':','!'],
    ];

    // -- QWERTY layout (English standard) -------------------------------------
    private static readonly char[][] QwertyGrid =
    [
        ['q','w','e','r','t','y','u','i','o','p'],
        ['a','s','d','f','g','h','j','k','l',';'],
        ['z','x','c','v','b','n','m',',','.','/' ],
    ];

    public LayoutMatrix(KeyboardLayout layout = KeyboardLayout.Azerty)
    {
        ActiveLayout = layout == KeyboardLayout.Auto ? KeyboardLayout.Azerty : layout;
        _grid  = layout == KeyboardLayout.Qwerty ? QwertyGrid : AzertyGrid;
        _index = BuildIndex(_grid);
    }

    /// <summary>
    /// Returns true and a plausible topographical typo for the given character.
    /// Selects from 8-directional neighbors on the physical grid.
    /// </summary>
    public bool TryGetTypo(char c, Random rng, out char typo)
    {
        typo = c;
        char lower = char.ToLower(c);

        if (!_index.TryGetValue(lower, out var pos))
            return false;

        var neighbors = GetNeighbors(pos.Row, pos.Col);
        if (neighbors.Count == 0)
            return false;

        char candidate = neighbors[rng.Next(neighbors.Count)];

        // Preserve case
        typo = char.IsUpper(c) ? char.ToUpper(candidate) : candidate;
        return true;
    }

    /// <summary>
    /// Returns the physical distance between two keys (Euclidean on grid).
    /// Used by BigramTable to modulate scale λ for same-hand vs. alt-hand.
    /// </summary>
    public double KeyDistance(char a, char b)
    {
        char la = char.ToLower(a);
        char lb = char.ToLower(b);

        if (!_index.TryGetValue(la, out var pa) || !_index.TryGetValue(lb, out var pb))
            return 2.0; // default: medium distance

        double dr = pa.Row - pb.Row;
        double dc = pa.Col - pb.Col;
        return Math.Sqrt(dr * dr + dc * dc);
    }

    /// <summary>
    /// Whether two keys are struck by the same finger (heuristic).
    /// Same-finger bigrams are the slowest category.
    /// </summary>
    public bool IsSameFinger(char a, char b)
    {
        // Finger columns: [0]=pinky, [1,2]=ring, [3,4]=middle, [5,6]=index, [7,8,9]=right hand
        // Left hand: cols 0-4, Right hand: cols 5-9
        static int FingerOf(int col) => col switch
        {
            0     => 0,          // left pinky
            1     => 1,          // left ring
            2     => 2,          // left middle
            3 or 4 => 3,         // left index
            5 or 6 => 4,         // right index
            7     => 5,          // right middle
            8     => 6,          // right ring
            9     => 7,          // right pinky
            _     => -1
        };

        char la = char.ToLower(a);
        char lb = char.ToLower(b);

        if (!_index.TryGetValue(la, out var pa) || !_index.TryGetValue(lb, out var pb))
            return false;

        return FingerOf(pa.Col) == FingerOf(pb.Col);
    }

    // -- Private helpers -------------------------------------------------------

    private List<char> GetNeighbors(int row, int col)
    {
        var result = new List<char>(8);
        for (int dr = -1; dr <= 1; dr++)
        for (int dc = -1; dc <= 1; dc++)
        {
            if (dr == 0 && dc == 0) continue;
            int r = row + dr;
            int c = col + dc;
            if (r >= 0 && r < _grid.Length && c >= 0 && c < _grid[r].Length)
                result.Add(_grid[r][c]);
        }
        return result;
    }

    private static Dictionary<char, (int, int)> BuildIndex(char[][] grid)
    {
        var idx = new Dictionary<char, (int, int)>();
        for (int r = 0; r < grid.Length; r++)
        for (int c = 0; c < grid[r].Length; c++)
            idx[grid[r][c]] = (r, c);
        return idx;
    }
}

public enum KeyboardLayout { Azerty, Qwerty, Auto }

/// <summary>
/// Runtime layout detection: maps the foreground window's active keyboard
/// layout (KLID) to one of the supported physical grids.
/// </summary>
internal static class LayoutDetector
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    /// <summary>French KLIDs -> AZERTY, English KLIDs -> QWERTY, else QWERTY.</summary>
    public static KeyboardLayout DetectForeground()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return KeyboardLayout.Qwerty;
            uint threadId = GetWindowThreadProcessId(fg, out _);
            IntPtr hkl = GetKeyboardLayout(threadId);
            uint klid = ((uint)hkl.ToInt64()) & 0xFFFF;

            // 0x040c fr-FR, 0x080c fr-BE, 0x0c0c fr-CA, 0x100c fr-CH -> AZERTY
            if (klid is 0x040c or 0x080c or 0x0c0c or 0x100c) return KeyboardLayout.Azerty;
            // 0x0409 en-US, 0x0809 en-GB, etc. -> QWERTY
            return KeyboardLayout.Qwerty;
        }
        catch
        {
            return KeyboardLayout.Qwerty;
        }
    }

    /// <summary>Resolve an effective layout, detecting when Auto is requested.</summary>
    public static KeyboardLayout Resolve(KeyboardLayout configured)
        => configured == KeyboardLayout.Auto ? DetectForeground() : configured;
}
