using System.IO;
using System.Text.Json;
using UltraZip.Models;

namespace UltraZip.Services;

/// <summary>
/// 密码库：明文 JSON 存于 %APPDATA%\UltraZip\passwords.json（用户明确要求明文）。
/// 提供 CRUD 与自动尝试顺序（按命中次数排序）。
/// </summary>
public class PasswordVault
{
    private static readonly string StoreDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraZip");
    private static readonly string StorePath = Path.Combine(StoreDir, "passwords.json");

    private readonly object _lock = new();
    private List<PasswordEntry> _entries = new();

    private static readonly Lazy<PasswordVault> _instance = new(() => new PasswordVault());
    public static PasswordVault Instance => _instance.Value;

    public PasswordVault()
    {
        Load();
    }

    public IReadOnlyList<PasswordEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    /// <summary>自动尝试顺序：命中次数多、最近使用的靠前。</summary>
    public IReadOnlyList<PasswordEntry> GetCandidatesOrdered()
    {
        lock (_lock)
            return _entries
                .OrderByDescending(e => e.HitCount)
                .ThenByDescending(e => e.LastUsed)
                .ToList();
    }

    public void Add(string label, string password)
    {
        lock (_lock)
        {
            var existing = _entries.FirstOrDefault(e => e.Password == password);
            if (existing != null)
            {
                if (!string.IsNullOrWhiteSpace(label)) existing.Label = label;
            }
            else
            {
                _entries.Add(new PasswordEntry
                {
                    Label = string.IsNullOrWhiteSpace(label) ? password : label,
                    Password = password
                });
            }
            Save();
        }
    }

    public void Update(PasswordEntry entry)
    {
        lock (_lock)
        {
            var i = _entries.FindIndex(e => e.Id == entry.Id);
            if (i >= 0) { _entries[i] = entry; Save(); }
        }
    }

    public void Remove(Guid id)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => e.Id == id);
            Save();
        }
    }

    /// <summary>记录一次命中：计数 + 最近使用时间。</summary>
    public void RecordHit(string password)
    {
        lock (_lock)
        {
            var e = _entries.FirstOrDefault(x => x.Password == password);
            if (e != null)
            {
                e.HitCount++;
                e.LastUsed = DateTime.Now;
                Save();
            }
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(StorePath))
                {
                    var json = File.ReadAllText(StorePath);
                    _entries = JsonSerializer.Deserialize<List<PasswordEntry>>(json) ?? new();
                }
            }
            catch
            {
                _entries = new();
            }
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            var json = JsonSerializer.Serialize(_entries,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StorePath, json);
        }
        catch
        {
            // 存储失败不阻塞主流程
        }
    }
}
