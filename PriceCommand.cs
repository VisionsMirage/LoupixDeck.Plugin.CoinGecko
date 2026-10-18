using System.Globalization;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.CoinGecko;

/// <summary>
/// Touch button showing a pair's label, current price and 24h change. Pressing it opens the coin's
/// page on coingecko.com.
/// <para>The label and the price are ordinary text layers fed through <see cref="IValueDisplayCommand"/>
/// (label = <see cref="AdjustmentValue.Detail"/>, price = <see cref="AdjustmentValue.Text"/>), so the
/// user styles them in the button editor like any text: font, size, colour, position. The 24h change
/// is drawn by the plugin because its colour follows the sign.</para>
/// Parameters: <c>CoinGecko.Price(Pair,Label)</c>, e.g. <c>CoinGecko.Price(BTC/JPY,)</c>. The update
/// interval is a plugin setting shared by all buttons.
/// </summary>
internal sealed class PriceCommand(PriceService prices) : IDisplayImageCommand, IValueDisplayCommand
{
    public const string Name = "CoinGecko.Price";

    // Text stays 5 px clear of each side on a 90 px key: the device bezel hides the very edge.
    private const int TextBoxWidth = 80;

    private static readonly PluginColor NeutralColor = PluginColor.White;
    private static readonly PluginColor MutedColor = new(140, 140, 140);
    private static readonly PluginColor UpColor = new(38, 208, 124);
    private static readonly PluginColor DownColor = new(255, 82, 82);

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Price",
        Group = "CoinGecko",
        Icon = "\U000F07B1", // mdi-chart-line-variant
        Description = "Price and 24h change of a crypto pair",
        ParameterTemplate = "({Pair},{Label})",
        Parameters =
        [
            new CommandParameter("Pair", typeof(string)) { DefaultValue = "BTC/USD" },
            new CommandParameter("Label", typeof(string))
        ],
        // Created once when the command is put on a button; the 24h change is the plugin's own layer
        // and lands on top of these. Offsets are from the key centre on a 90 px key.
        ButtonLayout = new ButtonLayoutDescriptor
        {
            Mode = ButtonLayoutMode.Custom,
            Layers =
            [
                new ButtonLayerDescriptor
                {
                    Kind = ButtonLayerKind.Text,
                    Name = "Label",
                    TextSource = ButtonTextSource.Detail,
                    TextSize = 13,
                    OffsetY = -28,
                    BoxWidth = TextBoxWidth,
                    BoxHeight = 22,
                    Color = "#C8C8C8"
                },
                new ButtonLayerDescriptor
                {
                    Kind = ButtonLayerKind.Text,
                    Name = "Price",
                    TextSource = ButtonTextSource.Value,
                    TextSize = 14,
                    OffsetY = -3,
                    BoxWidth = TextBoxWidth,
                    BoxHeight = 26,
                    Color = "#FFFFFF"
                }
            ]
        }
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    // Rendering only reads the cache; the fetch schedule lives in PriceService.
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(10);

    /// <summary>Feeds the label and price text layers. NaN: text only, no indicator arc.</summary>
    public AdjustmentValue? GetValue(CommandContext ctx)
    {
        var pair = PairKey.Parse(Param(ctx, 0));
        var label = Param(ctx, 1);
        if (string.IsNullOrWhiteSpace(label))
            label = pair?.ToString() ?? "CoinGecko";

        if (pair is not { } p)
            return new AdjustmentValue(double.NaN, "Set a pair") { Detail = label };

        var quote = QuoteFor(ctx, p);
        var text = quote.Status switch
        {
            QuoteStatus.Ok => PriceFormat.Price(quote.Price, p.Currency),
            QuoteStatus.Loading => "…",
            _ => "N/A"
        };
        return new AdjustmentValue(double.NaN, text) { Detail = label };
    }

    /// <summary>Draws the 24h change (or why there is no price) below the price layer.</summary>
    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        // No fill: the host draws this layer over the button's own background and text layers.
        if (PairKey.Parse(Param(ctx, 0)) is not { } pair)
            return true;

        var s = canvas.Width / 90f;
        var quote = QuoteFor(ctx, pair);
        switch (quote.Status)
        {
            case QuoteStatus.Ok when quote.Change24h is { } change:
                // Colour by the shown value, so "0.00%" from a tiny move stays neutral.
                var shown = Math.Round(change, 2);
                var color = shown > 0 ? UpColor : shown < 0 ? DownColor : NeutralColor;
                DrawFitted(canvas, PriceFormat.Change(change), 60 * s, 24 * s, 16 * s, color, bold: true);
                break;
            case QuoteStatus.UnknownCoin or QuoteStatus.UnknownCurrency or QuoteStatus.Error:
                DrawFitted(canvas, StatusText(quote.Status), 60 * s, 24 * s, 12 * s, MutedColor, bold: false);
                break;
        }
        return true;
    }

    private Quote QuoteFor(CommandContext ctx, PairKey pair) => prices.GetQuote(pair);

    public async Task Execute(CommandContext ctx)
    {
        try
        {
            if (PairKey.Parse(Param(ctx, 0)) is not { } pair)
                return;

            var id = await prices.ResolveCoinIdAsync(pair).ConfigureAwait(false);
            if (id == null)
            {
                ctx.Host.Logger.Warn($"Cannot open CoinGecko page: no coin matches \"{pair.Coin}\".");
                return;
            }

            ctx.Host.OpenBrowser(CoinPageUrl(id));
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Error("Opening the CoinGecko page failed", ex);
        }
    }

    // coingecko.com localizes the path segment too ("/ja/コイン/bitcoin"; "/ja/coins/…" is a 404).
    // The host UI has no Japanese, so the OS display language decides.
    private static string CoinPageUrl(string id) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja"
            ? $"https://www.coingecko.com/ja/%E3%82%B3%E3%82%A4%E3%83%B3/{Uri.EscapeDataString(id)}"
            : $"https://www.coingecko.com/en/coins/{Uri.EscapeDataString(id)}";

    private static string StatusText(QuoteStatus status) => status switch
    {
        QuoteStatus.UnknownCoin => "Unknown coin",
        QuoteStatus.UnknownCurrency => "Unknown currency",
        _ => "Fetch failed"
    };

    /// <summary>Draws one centred line, shrinking the font until it fits the key's width.</summary>
    private static void DrawFitted(IRenderCanvas canvas, string text, float y, float height, float maxSize,
        PluginColor color, bool bold)
    {
        var width = (int)(TextBoxWidth * canvas.Width / 90f);
        var x = (canvas.Width - width) / 2;
        var size = maxSize;
        var minSize = 8 * canvas.Width / 90f;
        while (size > minSize && canvas.MeasureText(text, size, bold) > width)
            size -= 0.5f;

        canvas.DrawText(text, x, (int)y, width, (int)height, color, size,
            TextHAlign.Center, TextVAlign.Middle, bold);
    }

    // The host drops trailing empty parameters, so a missing index means "empty".
    private static string Param(CommandContext ctx, int index) =>
        index < ctx.Parameters.Length ? ctx.Parameters[index] : string.Empty;
}
