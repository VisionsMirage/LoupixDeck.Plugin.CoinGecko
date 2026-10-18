using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.CoinGecko;

internal enum QuoteStatus
{
    Loading,
    Ok,
    UnknownCoin,
    UnknownCurrency,
    Error
}

internal sealed record Quote(QuoteStatus Status, decimal Price = 0, decimal? Change24h = null);

/// <summary>A pair as the user typed it, e.g. "BTC/JPY", split and normalized.</summary>
internal readonly record struct PairKey(string Coin, string Currency)
{
    public static PairKey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var parts = text.Split('/', 2, StringSplitOptions.TrimEntries);
        var coin = parts[0].ToLowerInvariant();
        var currency = parts.Length > 1 && parts[1].Length > 0 ? parts[1].ToLowerInvariant() : "usd";
        return coin.Length == 0 ? null : new PairKey(coin, currency);
    }

    public override string ToString() => $"{Coin.ToUpperInvariant()}/{Currency.ToUpperInvariant()}";
}

/// <summary>
/// Fetches prices from the CoinGecko API in the background. Buttons register the pairs they show on
/// every render; a single worker batches all due pairs into one <c>/simple/price</c> request, so
/// many buttons cost one call.
/// <para>Updates follow the clock: with a 5-minute interval every shown pair is fetched together at
/// :00, :05, :10 … A pair that appears between two boundaries is fetched once straight away so the
/// button is not left empty, then joins the others at the next boundary.</para>
/// </summary>
internal sealed class PriceService : IDisposable
{
    // A Demo key and a paid-plan key look alike ("CG-…"), but each only works on its own root URL
    // and header, so the user says which one they have.
    private const string PublicApiBase = "https://api.coingecko.com/api/v3/";
    private const string ProApiBase = "https://pro-api.coingecko.com/api/v3/";

    // A button not rendered for this long (page switched away, button removed) stops being fetched.
    private static readonly TimeSpan SubscriptionIdle = TimeSpan.FromMinutes(2);
    // How late after a clock boundary the fetch can start. The check itself costs nothing.
    private static readonly TimeSpan WorkerTick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ResolveRetry = TimeSpan.FromMinutes(10);

    /// <summary>The intervals the settings offer, in minutes. Each divides an hour, so all line up on the clock.</summary>
    public static readonly int[] IntervalChoices = [5, 10, 15, 30, 60];

    private sealed record Resolution(string? CoinId, DateTime ResolvedAt);

    private readonly IPluginHost _host;
    private readonly Action _onUpdated;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0);

    // Pair → when a button last rendered it.
    private readonly ConcurrentDictionary<PairKey, DateTime> _lastSeen = new();
    // Pair → the clock slot it was last fetched in. Kept when a pair goes idle, so paging back to
    // a button within the same slot does not fetch again.
    private readonly ConcurrentDictionary<PairKey, long> _fetchedSlot = new();
    private readonly ConcurrentDictionary<string, Resolution> _resolved = new();
    private readonly ConcurrentDictionary<PairKey, Quote> _quotes = new();
    private DateTime _blockedUntil = DateTime.MinValue;

    public PriceService(IPluginHost host, Action onUpdated)
    {
        _host = host;
        _onUpdated = onUpdated;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LoupixDeck.Plugin.CoinGecko/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    private string ApiKey => _host.Settings.Get<string>(CoinGeckoPlugin.ApiKeySetting)?.Trim() ?? string.Empty;

    /// <summary>Snaps any number of minutes to the nearest <see cref="IntervalChoices"/> entry.</summary>
    public static int NormalizeInterval(long minutes) =>
        IntervalChoices.MinBy(choice => Math.Abs(choice - minutes));

    private TimeSpan Interval => TimeSpan.FromMinutes(NormalizeInterval(
        _host.Settings.Get(CoinGeckoPlugin.UpdateIntervalSetting, (long)IntervalChoices[0])));

    /// <summary>Number of the current interval on the local clock; it changes at :00, :05, … for 5 minutes.</summary>
    private long CurrentSlot => DateTime.Now.Ticks / Interval.Ticks;

    private bool UsesProApi => ApiKey.Length > 0 && _host.Settings.Get<bool>(CoinGeckoPlugin.PaidPlanSetting);

    /// <summary>
    /// Returns the latest quote for <paramref name="pair"/> and keeps it on the fetch schedule.
    /// Fast and non-blocking: called from the render path.
    /// </summary>
    public Quote GetQuote(PairKey pair)
    {
        _lastSeen[pair] = DateTime.UtcNow;

        if (!_fetchedSlot.TryGetValue(pair, out var slot) || slot != CurrentSlot)
            Wake();

        return _quotes.TryGetValue(pair, out var quote) ? quote : new Quote(QuoteStatus.Loading);
    }

    /// <summary>Resolves the CoinGecko coin id for a pair (for the browser link), fetching if needed.</summary>
    public async Task<string?> ResolveCoinIdAsync(PairKey pair) =>
        await ResolveAsync(pair.Coin, _cts.Token).ConfigureAwait(false);

    /// <summary>Fetches everything again, e.g. after the API key or the interval changed.</summary>
    public void Reset()
    {
        _blockedUntil = DateTime.MinValue;
        _fetchedSlot.Clear();
        foreach (var (query, resolution) in _resolved)
            if (resolution.CoinId == null)
                _resolved.TryRemove(query, out _);
        Wake();
    }

    /// <summary>Checks that the API answers with the current key. Returns a message for the user.</summary>
    public async Task<string> TestConnectionAsync()
    {
        try
        {
            var api = UsesProApi ? "paid plan API" : ApiKey.Length > 0 ? "Demo API" : "public API";
            using var response = await SendAsync("ping", _cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return $"Connected to the CoinGecko {api}.";

            var detail = await ReadErrorMessageAsync(response).ConfigureAwait(false);
            return $"CoinGecko {api} answered {(int)response.StatusCode} {response.ReasonPhrase}."
                   + (detail != null ? $" {detail}" : "");
        }
        catch (Exception ex)
        {
            return $"Connection failed: {ex.Message}";
        }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(WorkerTick, ct).ConfigureAwait(false);
                await FetchDueAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _host.Logger.Error("Price update failed", ex);
            }
        }
    }

    private async Task FetchDueAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now < _blockedUntil)
            return;

        foreach (var (pair, seen) in _lastSeen)
            if (now - seen > SubscriptionIdle)
                _lastSeen.TryRemove(pair, out _);

        // Everything shown at a new slot, plus pairs that appeared since the last fetch.
        var slot = CurrentSlot;
        var due = _lastSeen.Keys
            .Where(pair => !_fetchedSlot.TryGetValue(pair, out var fetched) || fetched != slot)
            .ToList();
        if (due.Count == 0)
            return;

        // Resolve symbols to coin ids first; one search call per unknown symbol, cached.
        Dictionary<PairKey, string> ids = [];
        var changed = false;
        foreach (var pair in due)
        {
            var id = await ResolveAsync(pair.Coin, ct).ConfigureAwait(false);
            if (now < _blockedUntil)
                return;

            if (id != null)
                ids[pair] = id;
            else
                changed |= SetQuote(pair, new Quote(QuoteStatus.UnknownCoin));
        }

        if (ids.Count > 0)
            changed |= await FetchPricesAsync(ids, ct).ConfigureAwait(false);

        // Rate limited: leave them due, the worker retries once the pause is over.
        if (now >= _blockedUntil)
        {
            foreach (var pair in due)
                _fetchedSlot[pair] = slot;
        }

        if (changed)
            _onUpdated();
    }

    private async Task<bool> FetchPricesAsync(Dictionary<PairKey, string> ids, CancellationToken ct)
    {
        var coinIds = string.Join(',', ids.Values.Distinct());
        var currencies = string.Join(',', ids.Keys.Select(p => p.Currency).Distinct());
        var url = $"simple/price?ids={Uri.EscapeDataString(coinIds)}&vs_currencies={Uri.EscapeDataString(currencies)}&include_24hr_change=true&precision=full";

        JsonDocument doc;
        try
        {
            using var response = await SendAsync(url, ct).ConfigureAwait(false);
            if (!CheckResponse(response))
                return MarkFailed(ids.Keys);

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            _host.Logger.Warn($"Price request failed: {ex.Message}");
            return MarkFailed(ids.Keys);
        }

        using (doc)
        {
            var changed = false;
            foreach (var (pair, id) in ids)
            {
                if (!doc.RootElement.TryGetProperty(id, out var coin))
                {
                    changed |= SetQuote(pair, new Quote(QuoteStatus.UnknownCoin));
                    continue;
                }

                if (!coin.TryGetProperty(pair.Currency, out var priceElement) || !TryGetDecimal(priceElement, out var price))
                {
                    changed |= SetQuote(pair, new Quote(QuoteStatus.UnknownCurrency));
                    continue;
                }

                decimal? change = coin.TryGetProperty($"{pair.Currency}_24h_change", out var changeElement)
                                  && TryGetDecimal(changeElement, out var c)
                    ? c
                    : null;
                changed |= SetQuote(pair, new Quote(QuoteStatus.Ok, price, change));
            }
            return changed;
        }
    }

    /// <summary>A failed request keeps the last good price on screen; only pairs without one show the error.</summary>
    private bool MarkFailed(IEnumerable<PairKey> pairs)
    {
        var changed = false;
        foreach (var pair in pairs)
            if (!_quotes.TryGetValue(pair, out var quote) || quote.Status != QuoteStatus.Ok)
                changed |= SetQuote(pair, new Quote(QuoteStatus.Error));
        return changed;
    }

    private bool SetQuote(PairKey pair, Quote quote)
    {
        var old = _quotes.TryGetValue(pair, out var q) ? q : null;
        _quotes[pair] = quote;
        return old != quote;
    }

    /// <summary>
    /// Maps what the user typed to a CoinGecko coin id: an exact id ("bitcoin") wins, otherwise the
    /// coin with that symbol ("btc") and the best market-cap rank. Null when nothing matches.
    /// </summary>
    private async Task<string?> ResolveAsync(string query, CancellationToken ct)
    {
        if (_resolved.TryGetValue(query, out var cached)
            && (cached.CoinId != null || DateTime.UtcNow - cached.ResolvedAt < ResolveRetry))
            return cached.CoinId;

        try
        {
            using var response = await SendAsync($"search?query={Uri.EscapeDataString(query)}", ct).ConfigureAwait(false);
            if (!CheckResponse(response))
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("coins", out var coins) || coins.ValueKind != JsonValueKind.Array)
                return null;

            string? exactId = null;
            string? bySymbol = null;
            var bestRank = int.MaxValue;
            foreach (var coin in coins.EnumerateArray())
            {
                var id = coin.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (id == null)
                    continue;
                if (string.Equals(id, query, StringComparison.OrdinalIgnoreCase))
                {
                    exactId = id;
                    break;
                }

                var symbol = coin.TryGetProperty("symbol", out var s) ? s.GetString() : null;
                if (!string.Equals(symbol, query, StringComparison.OrdinalIgnoreCase))
                    continue;

                var rank = coin.TryGetProperty("market_cap_rank", out var r) && r.ValueKind == JsonValueKind.Number
                    ? r.GetInt32()
                    : int.MaxValue - 1;
                if (bySymbol == null || rank < bestRank)
                {
                    bySymbol = id;
                    bestRank = rank;
                }
            }

            var result = exactId ?? bySymbol;
            _resolved[query] = new Resolution(result, DateTime.UtcNow);
            if (result == null)
                _host.Logger.Warn($"No CoinGecko coin matches \"{query}\".");
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            _host.Logger.Warn($"Coin lookup for \"{query}\" failed: {ex.Message}");
            return null;
        }
    }

    private bool CheckResponse(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return true;

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = response.Headers.RetryAfter?.Delta ?? RateLimitBackoff;
            _blockedUntil = DateTime.UtcNow + wait;
            _host.Logger.Warn($"CoinGecko rate limit reached; pausing for {wait.TotalSeconds:0} s. Setting a CoinGecko API key helps.");
        }
        else
        {
            _host.Logger.Warn($"CoinGecko answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        return false;
    }

    private Task<HttpResponseMessage> SendAsync(string relativeUrl, CancellationToken ct)
    {
        var key = ApiKey;
        var pro = UsesProApi;
        var request = new HttpRequestMessage(HttpMethod.Get, (pro ? ProApiBase : PublicApiBase) + relativeUrl);
        if (key.Length > 0)
            request.Headers.Add(pro ? "x-cg-pro-api-key" : "x-cg-demo-api-key", key);
        return _http.SendAsync(request, ct);
    }

    /// <summary>
    /// CoinGecko's own explanation of a failed request, e.g. that a paid-plan key needs the Pro API.
    /// It sits in <c>status.error_message</c> or at the top level, depending on the error.
    /// </summary>
    private static async Task<string?> ReadErrorMessageAsync(HttpResponseMessage response)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object
                && status.TryGetProperty("error_message", out var nested))
                return nested.GetString();
            return root.TryGetProperty("error_message", out var top) ? top.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetDecimal(JsonElement element, out decimal value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number)
            return false;
        if (element.TryGetDecimal(out value))
            return true;
        // Very small or very large values may come in exponent form that decimal rejects.
        if (!element.TryGetDouble(out var d) || double.IsNaN(d) || double.IsInfinity(d))
            return false;
        try
        {
            value = (decimal)d;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
        _cts.Dispose();
    }
}
