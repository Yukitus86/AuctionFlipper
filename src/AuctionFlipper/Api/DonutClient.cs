using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AuctionFlipper.Api;

/// <summary>
/// Thin, rate-limited client for the DonutSMP public API.
///
/// Two quirks of the live API shape this class:
///  * sort/search are only honoured when sent as a JSON body on a GET request (a query string
///    returns HTTP 500), so requests carry content on a verb that usually has none;
///  * responses come back as <c>text/plain</c>, so the content type is never inspected.
/// </summary>
public sealed class DonutClient : IDisposable
{
    public const string BaseUrl = "https://api.donutsmp.net";

    /// <summary>Entries returned per auction-list page. Fixed by the server.</summary>
    public const int PageSize = 44;

    /// <summary>Sales returned per transaction page, and the highest page the server accepts.</summary>
    public const int TransactionPageSize = 100;
    public const int MaxTransactionPage = 10;

    private readonly HttpClient _http;
    private readonly RateLimiter _limiter;
    private string _apiKey;

    public DonutClient(string apiKey, RateLimiter limiter)
    {
        _apiKey = apiKey;
        _limiter = limiter;

        var handler = new SocketsHttpHandler
        {
            // The API sits behind Cloudflare on HTTP/1.1; a small pool keeps connections warm
            // without opening more sockets than the request budget can ever use.
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = false,
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AuctionFlipper/1.0");
    }

    public RateLimiter Limiter => _limiter;

    public void SetApiKey(string apiKey) => _apiKey = apiKey;

    /// <summary>Raised for every completed request so the UI can show live latency and failures.</summary>
    public event Action<RequestTelemetry>? RequestCompleted;

    /// <summary>
    /// Fetches one page of live auction listings. <paramref name="search"/> is a fuzzy name match
    /// on the server side, not an exact item id, so callers must still filter what comes back.
    /// </summary>
    public Task<AhResponse> GetListingsAsync(
        int page,
        Lane lane,
        AuctionSort sort = AuctionSort.Default,
        string? search = null,
        CancellationToken ct = default)
    {
        string? body = BuildBody(sort, search);
        return SendAsync(
            $"/v1/auction/list/{page}", body, lane,
            static (stream, c) => JsonSerializer.DeserializeAsync(stream, ApiJsonContext.Default.AhResponse, c),
            static r => (r.Status, r.Reason, r.Message),
            ct);
    }

    /// <summary>
    /// Fetches a listing page for a caller that has already taken its own slot from the limiter.
    ///
    /// The sweeper claims budget with <see cref="RateLimiter.TryAcquire"/> so it can skip a beat
    /// instead of queueing behind the live feeds; without this overload it would pay for the slot
    /// twice.
    /// </summary>
    public Task<AhResponse> GetListingsAsyncPreAuthorized(int page, CancellationToken ct = default)
    {
        return SendAsync(
            $"/v1/auction/list/{page}", null, Lane.Sweeper,
            static (stream, c) => JsonSerializer.DeserializeAsync(stream, ApiJsonContext.Default.AhResponse, c),
            static r => (r.Status, r.Reason, r.Message),
            ct,
            firstAttemptPreAuthorized: true);
    }

    /// <summary>
    /// Fetches one page of recent sales. Pages 1..10 exist and together cover only about two
    /// minutes of market history, which is why the tape has to be polled and persisted.
    /// </summary>
    public Task<TransactionResponse> GetTransactionsAsync(int page, Lane lane, CancellationToken ct = default)
    {
        return SendAsync(
            $"/v1/auction/transactions/{page}", null, lane,
            static (stream, c) => JsonSerializer.DeserializeAsync(stream, ApiJsonContext.Default.TransactionResponse, c),
            static r => (r.Status, r.Reason, r.Message),
            ct);
    }

    private static string? BuildBody(AuctionSort sort, string? search)
    {
        string? wireSort = sort.ToWire();
        if (wireSort is null && string.IsNullOrWhiteSpace(search))
            return null;

        var payload = new AuctionRequestBody { Sort = wireSort, Search = string.IsNullOrWhiteSpace(search) ? null : search };
        return JsonSerializer.Serialize(payload, ApiJsonContext.Default.AuctionRequestBody);
    }

    private async Task<T> SendAsync<T>(
        string path,
        string? jsonBody,
        Lane lane,
        Func<Stream, CancellationToken, ValueTask<T?>> parse,
        Func<T, (int Status, string? Reason, string? Message)> readStatus,
        CancellationToken ct,
        bool firstAttemptPreAuthorized = false)
        where T : class
    {
        const int maxAttempts = 3;
        Exception? last = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1 || !firstAttemptPreAuthorized)
                await _limiter.AcquireAsync(lane, ct).ConfigureAwait(false);

            long startTicks = Stopwatch.GetTimestamp();
            HttpStatusCode httpStatus = 0;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
                if (jsonBody is not null)
                {
                    // Deliberate: the API only reads sort/search from a body on GET.
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                }

                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                httpStatus = response.StatusCode;

                if (httpStatus == HttpStatusCode.TooManyRequests)
                {
                    TimeSpan backoff = response.Headers.RetryAfter?.Delta
                        ?? TimeSpan.FromSeconds(Math.Min(30, 2 * attempt));
                    _limiter.ApplyPenalty(backoff);
                    last = new HttpRequestException("Rate limited (429).");
                    Report(lane, path, httpStatus, startTicks, rateLimited: true, failed: true);
                    continue;
                }

                if (httpStatus == HttpStatusCode.Unauthorized)
                {
                    Report(lane, path, httpStatus, startTicks, rateLimited: false, failed: true);
                    throw new DonutApiException(401, "Unauthorized",
                        "The API key was rejected. Generate one in game with /api and set it in Settings.");
                }

                await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                T? parsed = await parse(stream, ct).ConfigureAwait(false);

                if (parsed is null)
                    throw new DonutApiException((int)httpStatus, "Empty response", "The server returned no JSON body.");

                (int apiStatus, string? reason, string? message) = readStatus(parsed);
                if (apiStatus != 200)
                {
                    // A 500 here usually just means "that page does not exist" - a normal answer to
                    // a probe, not a transient fault, so it is surfaced rather than retried.
                    Report(lane, path, httpStatus, startTicks, rateLimited: false, failed: true);
                    throw new DonutApiException(apiStatus, reason, message);
                }

                Report(lane, path, httpStatus, startTicks, rateLimited: false, failed: false);
                return parsed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (DonutApiException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Network blips, timeouts and 5xx from the edge are worth one more try.
                last = ex;
                Report(lane, path, httpStatus, startTicks, rateLimited: false, failed: true);
                if (attempt < maxAttempts)
                    await Task.Delay(150 * attempt, ct).ConfigureAwait(false);
            }
        }

        throw last ?? new HttpRequestException("Request failed.");
    }

    private void Report(Lane lane, string path, HttpStatusCode status, long startTicks, bool rateLimited, bool failed)
    {
        var handler = RequestCompleted;
        if (handler is null) return;

        double ms = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
        handler(new RequestTelemetry(lane, path, (int)status, ms, rateLimited, failed));
    }

    public void Dispose() => _http.Dispose();
}

public readonly record struct RequestTelemetry(
    Lane Lane,
    string Path,
    int HttpStatus,
    double ElapsedMs,
    bool RateLimited,
    bool Failed);
