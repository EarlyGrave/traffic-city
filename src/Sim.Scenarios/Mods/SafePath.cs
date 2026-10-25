using System;
using System.IO;

namespace Sim.Scenarios.Mods;

/// <summary>Проверка путей внутри пакета (modding.md §7): только относительные, без выхода из каталога.</summary>
public static class SafePath
{
    /// <summary>
    /// Лексическая проверка относительного пути с <c>/</c>: не пуст, без <c>\</c>, <c>:</c>, NUL, управляющих символов,
    /// без ведущего <c>/</c>, пустых сегментов и сегментов, начинающихся с <c>.</c> (в т.ч. <c>..</c>).
    /// </summary>
    public static bool IsSafeRelative(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] == '/' || path[^1] == '/')
        {
            return false;
        }

        foreach (char c in path)
        {
            if (c is '\\' or ':' || c < ' ')
            {
                return false;
            }
        }

        foreach (string seg in path.Split('/'))
        {
            if (seg.Length == 0 || seg[0] == '.')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Файловая система: ни один сегмент пути не симлинк, итог внутри корня пакета.</summary>
    public static bool IsInsideRoot(string root, string relative)
    {
        string rootFull = Path.GetFullPath(root);
        string cur = rootFull;
        foreach (string seg in relative.Split('/'))
        {
            cur = Path.Combine(cur, seg);
            FileSystemInfo info = Directory.Exists(cur) ? new DirectoryInfo(cur) : new FileInfo(cur);
            if (info.LinkTarget != null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }
        }

        string full = Path.GetFullPath(cur);
        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
