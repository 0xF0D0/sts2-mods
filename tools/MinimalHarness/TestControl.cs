using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;

namespace MinimalHarness;

// Test scheduling/oracles only. No game messages, votes or replay data use this channel.
internal sealed class TestControl : IDisposable
{
    private readonly string _directory, _run;
    private readonly Uri? _endpoint;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private readonly ConcurrentDictionary<string, long> _reads = new();
    private readonly ConcurrentDictionary<string, byte> _pending = new();
    private Exception? _failure;

    internal TestControl(string directory, string run, string? endpoint)
    {
        _directory = directory; _run = run;
        _endpoint = endpoint is null ? null : new Uri(endpoint.TrimEnd('/') + "/");
    }

    private string PathFor(string key)
    {
        if (key != System.IO.Path.GetFileName(key) || key is "." or "..")
            throw new ArgumentException("Control keys must be filenames.", nameof(key));
        return System.IO.Path.Combine(_directory, key);
    }

    private Uri Url(string key) => new(_endpoint!,
        $"runs/{Uri.EscapeDataString(_run)}/values/{Uri.EscapeDataString(key)}");

    internal async Task Put(string key, string value)
    {
        CheckFailure();
        if (_endpoint is null)
        {
            string path = PathFor(key), temporary = path + $".tmp-{Environment.ProcessId}";
            await File.WriteAllTextAsync(temporary, value);
            File.Move(temporary, path, overwrite: true);
        }
        else
        {
            using var response = await _http.PutAsJsonAsync(Url(key), new { value });
            response.EnsureSuccessStatusCode();
            _cache[key] = value;
        }
    }

    internal string? Read(string key)
    {
        CheckFailure();
        if (_endpoint is null)
        {
            string path = PathFor(key);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        if (_cache.TryGetValue(key, out string? value)) return value;
        long now = Environment.TickCount64;
        if ((!_reads.TryGetValue(key, out long previous) || now - previous >= 100)
            && _pending.TryAdd(key, 0))
        {
            _reads[key] = now;
            _ = Fetch(key);
        }
        return null;
    }

    private async Task Fetch(string key)
    {
        try
        {
            using var response = await _http.GetAsync(Url(key));
            if (response.StatusCode == HttpStatusCode.NotFound) return;
            response.EnsureSuccessStatusCode();
            var value = await response.Content.ReadFromJsonAsync<ControlValue>();
            _cache[key] = value?.Value ?? throw new IOException("Missing control value.");
        }
        catch (Exception exception) { _failure = exception; }
        finally { _pending.TryRemove(key, out _); }
    }

    private sealed record ControlValue(string Value);
    private void CheckFailure()
    {
        if (_failure is { } error) throw new IOException("Test control failed.", error);
    }
    public void Dispose() => _http.Dispose();
}
