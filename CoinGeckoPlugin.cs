using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.CoinGecko;

public sealed class CoinGeckoPlugin : LoupixPlugin, IPluginSettingsPage
{
    internal const string ApiKeySetting = "ApiKey";
    internal const string PaidPlanSetting = "PaidPlan";
    internal const string UpdateIntervalSetting = "UpdateIntervalMinutes";

    private IPluginHost? _host;
    private PriceService? _prices;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "coingecko",
        Name = "CoinGecko Price",
        Version = new Version(1, 0, 1),
        SdkVersion = SdkInfo.Version,
        Author = "VisionsMirage",
        Icon = LoadEmbeddedIcon("CoinGecko.icon.png"),
        Description = "Show crypto prices and 24h change from CoinGecko on touch buttons.\n\n--\n\nCoinGecko の暗号資産の価格と 24 時間変動率をタッチボタンに表示します。"
    };

    public override void Initialize(IPluginHost host)
    {
        _host = host;
        _prices = new PriceService(host, () => host.RequestButtonRefresh(PriceCommand.Name));
    }

    public override void Shutdown()
    {
        _prices?.Dispose();
        _prices = null;
    }

    public override IEnumerable<IPluginCommand> GetCommands()
    {
        if (_prices == null)
            yield break;
        yield return new PriceCommand(_prices);
    }

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema { get; } =
    [
        new()
        {
            Key = UpdateIntervalSetting,
            Label = "Update interval (minutes)",
            Kind = PluginSettingKind.Number,
            DefaultValue = 5L,
            Description = "5, 10, 15, 30 or 60. Other values use the nearest of these. All buttons update together on the clock (every :00, :05, … for 5) in one request."
        },
        new()
        {
            Key = ApiKeySetting,
            Label = "API key",
            Kind = PluginSettingKind.Password,
            Description = "Optional. A CoinGecko API key avoids rate-limit errors: a free Demo key or a key from a paid plan. Leave empty to use the public API."
        },
        new()
        {
            Key = PaidPlanSetting,
            Label = "Paid plan key",
            Kind = PluginSettingKind.Toggle,
            Description = "Turn on when the API key is from a paid CoinGecko plan (it then uses the Pro API). Leave off for a Demo key."
        }
    ];

    public IReadOnlyList<PluginSettingAction> SettingsActions =>
    [
        new()
        {
            Label = "Test connection",
            Invoke = () => _prices?.TestConnectionAsync() ?? Task.FromResult("Plugin is not running.")
        }
    ];

    public void OnSettingsSaved()
    {
        _prices?.Reset();
        _host?.RequestButtonRefresh(PriceCommand.Name);
    }

    private static byte[] LoadEmbeddedIcon(string name)
    {
        using var s = typeof(CoinGeckoPlugin).Assembly.GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException($"Missing resource: {name}");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
