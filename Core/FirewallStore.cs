using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace HttpFreeKit.Core;





public sealed class FirewallStore
{
    public const string Group = "HTTP FreeKit";
    private const string LegacyGroup = "PortableProxy";
    public ObservableCollection<FirewallEntry> Entries { get; } = new();

    public FirewallStore()
    {
        foreach (var e in JsonUtil.LoadOrDefault(AppPaths.Firewall, () => new List<FirewallEntry>()))
            Entries.Add(e);
    }

    private void Save() { try { JsonUtil.Save(AppPaths.Firewall, Entries.ToList()); } catch { } }

    private static dynamic NewPolicy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)!)!;

    private static string RuleName(string program, string dir)
    {
        var h = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(program.ToLowerInvariant())))[..6];
        return $"HTTP FreeKit - {Path.GetFileName(program)} [{h}] {dir}";
    }

    private static string LegacyRuleName(string program, string dir)
    {
        var h = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(program.ToLowerInvariant())))[..6];
        return $"PortableProxy - {Path.GetFileName(program)} [{h}] {dir}";
    }

    public void Block(string program)
    {
        if (!File.Exists(program)) throw new FileNotFoundException("Programme introuvable.", program);
        if (Entries.Any(e => e.Program.Equals(program, StringComparison.OrdinalIgnoreCase))) return;

        var policy = NewPolicy();
        foreach (var (dir, code) in new[] { ("OUT", 2), ("IN", 1) })
        {
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true)!)!;
            rule.Name = RuleName(program, dir);
            rule.Description = "Created by HTTP FreeKit (removed during cleanup)";
            rule.ApplicationName = program;
            rule.Action = 0;
            rule.Direction = code;
            rule.Enabled = true;
            rule.Profiles = 0x7FFFFFFF;
            rule.Grouping = Group;
            policy.Rules.Add(rule);
        }

        Entries.Add(new FirewallEntry { Program = program });
        Save();
    }

    public void Unblock(FirewallEntry entry)
    {
        try
        {
            var policy = NewPolicy();
            foreach (var dir in new[] { "OUT", "IN" })
            {
                try { policy.Rules.Remove(RuleName(entry.Program, dir)); } catch { }
                try { policy.Rules.Remove(LegacyRuleName(entry.Program, dir)); } catch { }
            }
        }
        catch { }
        Entries.Remove(entry);
        Save();
    }

    public void UnblockAll(bool includeUnknown = false)
    {
        foreach (var e in Entries.ToList()) Unblock(e);

        if (!includeUnknown) return;
        try
        {
            var policy = NewPolicy();
            var names = new List<string>();
            foreach (dynamic r in policy.Rules)
            {
                string? g = r.Grouping;
                if (g == Group || g == LegacyGroup) names.Add((string)r.Name);
            }
            foreach (var n in names)
            {
                try { policy.Rules.Remove(n); } catch { }
            }
        }
        catch { }
    }
}
