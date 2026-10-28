namespace HttpFreeKit.Core;


public static class Services
{
    public static AppSettings Settings { get; private set; } = null!;
    public static RuleEngine Rules { get; private set; } = null!;
    public static TrafficStore Traffic { get; private set; } = null!;
    public static FirewallStore Firewall { get; private set; } = null!;
    public static ConnectionMonitor Connections { get; private set; } = null!;
    public static ProxyService Proxy { get; private set; } = null!;
    public static NetTestService NetTests { get; private set; } = null!;
    public static DiscordPresenceService DiscordPresence { get; private set; } = null!;

    public static void Init()
    {
        AppPaths.Ensure();
        Settings = AppSettings.Load();
        Rules = new RuleEngine();
        Traffic = new TrafficStore();
        Firewall = new FirewallStore();
        Connections = new ConnectionMonitor();
        Proxy = new ProxyService();
        NetTests = new NetTestService();
        DiscordPresence = new DiscordPresenceService();
        Traffic.RequestBodyBudgetBytes = Math.Clamp(Settings.TrafficBodyBudgetMb, 1, 2048) * 1024L * 1024L;
    }
}
