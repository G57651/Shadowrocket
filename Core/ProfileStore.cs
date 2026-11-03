using System.IO;
using System.Text.Json;

namespace Shadowrocket.Core;

/// <summary>一条用户导入的订阅（远程 URL 或本地 YAML 文件）。</summary>
public class ProfileItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";        // 远程订阅；本地文件为空
    public string LocalFile { get; set; } = "";  // 内核读取的 providers/{id}.yaml；远程订阅为空
    public string SourceFile { get; set; } = ""; // 用户当初选择的原始文件，用于「更新」时重新读取
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime? LastUpdated { get; set; }

    /// <summary>自动更新模式："off"（手动）/ "interval"（固定间隔）/ "daily"（每天定时）/ "cron"（Cron 表达式）。</summary>
    public string AutoUpdateMode { get; set; } = "off";
    /// <summary>interval 模式的间隔（分钟，5-10080）。</summary>
    public int AutoUpdateIntervalMinutes { get; set; } = 60;
    /// <summary>daily 模式的每日更新时间（HH:mm）。</summary>
    public string AutoUpdateTime { get; set; } = "08:00";
    /// <summary>cron 模式的 5 段式 Cron 表达式（分 时 日 月 周）。</summary>
    public string AutoUpdateCron { get; set; } = "";
    /// <summary>上次自动更新时间（调度去重与补跑判断用）。</summary>
    public DateTime? LastAutoUpdate { get; set; }
}

/// <summary>订阅列表持久化（profiles.json）。节点信息本身由内核解析，GUI 不存节点。</summary>
public class ProfileStore
{
    private static readonly object Lock = new();
    private readonly string _filePath;

    public ProfileStore(AppSettings settings)
    {
        _filePath = Path.Combine(AppSettings.Dir, "profiles.json");
        Items = Load();
    }

    public List<ProfileItem> Items { get; private set; }

    /// <summary>加载期发现的问题（profiles.json 损坏等）。非 null 时调用方必须向用户显示告警，不得静默。</summary>
    public string? LoadIssue { get; private set; }

    public event Action? Changed;

    private List<ProfileItem> Load()
    {
        if (File.Exists(_filePath))
        {
            try
            {
                return JsonSerializer.Deserialize<List<ProfileItem>>(File.ReadAllText(_filePath))
                       ?? throw new System.Text.Json.JsonException("反序列化结果为 null");
            }
            catch (System.Exception e)
            {
                // 主文件损坏：不静默清空订阅。原文件改名存档，尝试从 .bak 恢复，并上报调用方弹窗提示
                var corrupt = _filePath + ".corrupt-" + System.DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Move(_filePath, corrupt); } catch { corrupt = _filePath; }

                var bak = _filePath + ".bak";
                if (File.Exists(bak))
                {
                    try
                    {
                        var recovered = JsonSerializer.Deserialize<List<ProfileItem>>(File.ReadAllText(bak));
                        if (recovered != null)
                        {
                            LoadIssue =
                                $"订阅记录文件已损坏（{e.Message}），已从备份 {bak} 恢复。损坏原件保留在：{corrupt}";
                            // 恢复结果立即回写主文件：否则下次启动见主文件缺失会静默清空订阅。
                            // 此时主文件不存在，AtomicFile 走 Move 分支，不会触碰 .bak
                            try
                            {
                                AtomicFile.WriteAllText(_filePath,
                                    JsonSerializer.Serialize(recovered, new JsonSerializerOptions { WriteIndented = true }));
                            }
                            catch { }
                            return recovered;
                        }
                    }
                    catch
                    {
                        // 备份也不可用，落到空列表分支
                    }
                }

                LoadIssue =
                    $"订阅记录文件已损坏且无可用备份（{e.Message}），订阅列表已置空。损坏原件保留在：{corrupt}";
                return [];
            }
        }

        // 主文件不存在：防御"主文件被删/改名但 .bak 仍在"（上次损坏恢复的回写失败等），不得静默清空订阅
        var orphanBak = _filePath + ".bak";
        if (File.Exists(orphanBak))
        {
            try
            {
                var recovered = JsonSerializer.Deserialize<List<ProfileItem>>(File.ReadAllText(orphanBak));
                if (recovered != null)
                {
                    LoadIssue = $"订阅记录主文件缺失，已从备份 {orphanBak} 恢复。";
                    try
                    {
                        AtomicFile.WriteAllText(_filePath,
                            JsonSerializer.Serialize(recovered, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    catch { }
                    return recovered;
                }
            }
            catch
            {
                // 备份不可用，落到空列表
            }
        }
        return [];
    }

    private void Save()
    {
        AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>线程安全的订阅列表快照：后台调度器遍历用，避免与 UI 侧增删并发冲突。</summary>
    public ProfileItem[] Snapshot()
    {
        lock (Lock)
        {
            return Items.ToArray();
        }
    }

    public void Add(ProfileItem item)
    {
        lock (Lock)
        {
            Items.Add(item);
            Save();
        }
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        lock (Lock)
        {
            var p = Items.FirstOrDefault(x => x.Id == id);
            if (p?.LocalFile is { Length: > 0 } f && File.Exists(f))
            {
                try { File.Delete(f); } catch { }
            }
            Items.RemoveAll(x => x.Id == id);
            Save();
        }
        Changed?.Invoke();
    }

    public void Rename(string id, string name)
    {
        lock (Lock)
        {
            var p = Items.FirstOrDefault(x => x.Id == id);
            if (p != null)
            {
                p.Name = name;
                Save();
            }
        }
        Changed?.Invoke();
    }

    /// <summary>把外部对 Items 里某个订阅的修改落盘（编辑 URL 等）。</summary>
    public void Persist()
    {
        lock (Lock)
        {
            Save();
        }
        Changed?.Invoke();
    }

    public void MarkUpdated(string id)
    {
        lock (Lock)
        {
            var p = Items.FirstOrDefault(x => x.Id == id);
            if (p != null)
            {
                p.LastUpdated = DateTime.Now;
                Save();
            }
        }
        Changed?.Invoke();
    }

    public void Reload() => Items = Load();
}
