using System.IO;

namespace Shadowrocket.Core;

/// <summary>
/// 状态文件原子写：先写临时文件，再用 File.Replace 原子交换，同时自动保留 .bak 备份。
/// 避免写中途崩溃/断电把 JSON 截断成半成品，导致下次启动配置整体丢失。
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        if (File.Exists(path))
        {
            // Replace = 原子交换，且把旧内容留作 .bak（供损坏时回读）
            File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, path);
        }
    }
}
