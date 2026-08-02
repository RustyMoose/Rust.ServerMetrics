using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;

namespace RustServerMetrics;

internal sealed class MetricsUploadWorker : IDisposable
{
    private const int MaxQueuedBatches = 4;
    private const int MaxAttempts = 3;
    private const int IdleWaitMilliseconds = 250;
    private const int RetryDelayMilliseconds = 200;
    private const int ShutdownJoinMilliseconds = 1000;

    private sealed class Batch
    {
        public Uri Uri;
        public byte[] Body;
        public int Lines;
        public bool Compress;
        public bool CaptureResponse;
    }

    private readonly object _sync = new();
    private readonly Batch[] _queue = new Batch[MaxQueuedBatches];
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private readonly int _timeoutSeconds;

    private int _head;
    private int _count;
    private int _inFlight;
    private volatile bool _running = true;

    private HttpClient _client;
    private MemoryStream _compressionBuffer;

    private long _droppedLines;
    private int _networkFailures;
    private int _httpFailures;
    private string _lastNetworkError;
    private string _lastHttpError;
    private string _lastResponseBody;

    public MetricsUploadWorker(int timeoutSeconds)
    {
        _timeoutSeconds = timeoutSeconds;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ServerMetrics uploader"
        };

        _thread.Start();
    }

    public long DroppedLines => Interlocked.Read(ref _droppedLines);

    public int PendingBatches
    {
        get { lock (_sync) { return _count + _inFlight; } }
    }

    public bool HasRoom
    {
        get { lock (_sync) { return _running && _count < MaxQueuedBatches; } }
    }

    public bool TryEnqueue(Uri uri, byte[] body, int lines, bool compress, bool captureResponse)
    {
        lock (_sync)
        {
            if (!_running || _count == MaxQueuedBatches) return false;

            _queue[(_head + _count) % MaxQueuedBatches] = new Batch
            {
                Uri = uri,
                Body = body,
                Lines = lines,
                Compress = compress,
                CaptureResponse = captureResponse
            };

            _count++;
        }

        _signal.Set();
        return true;
    }

    public int TakeNetworkFailures(out string lastError)
    {
        lock (_sync)
        {
            var failures = _networkFailures;
            lastError = _lastNetworkError;
            _networkFailures = 0;
            _lastNetworkError = null;
            return failures;
        }
    }

    public int TakeHttpFailures(out string lastError, out string lastResponseBody)
    {
        lock (_sync)
        {
            var failures = _httpFailures;
            lastError = _lastHttpError;
            lastResponseBody = _lastResponseBody;
            _httpFailures = 0;
            _lastHttpError = null;
            _lastResponseBody = null;
            return failures;
        }
    }

    public void DropQueued()
    {
        lock (_sync)
        {
            while (_count > 0)
            {
                var batch = _queue[_head];
                _queue[_head] = null;
                _head = (_head + 1) % MaxQueuedBatches;
                _count--;
                Interlocked.Add(ref _droppedLines, batch.Lines);
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        _signal.Set();
        _thread.Join(ShutdownJoinMilliseconds);

        _client?.Dispose();
        _client = null;
    }

    private void Run()
    {
        try
        {
            Loop();
        }
        catch
        {
            //
        }
    }

    private void Loop()
    {
        while (_running)
        {
            Batch batch = null;

            lock (_sync)
            {
                if (_count > 0)
                {
                    batch = _queue[_head];
                    _queue[_head] = null;
                    _head = (_head + 1) % MaxQueuedBatches;
                    _count--;
                    _inFlight++;
                }
            }

            if (batch == null)
            {
                _signal.WaitOne(IdleWaitMilliseconds);
                continue;
            }

            try
            {
                Send(batch);
            }
            catch (Exception e)
            {
                RecordNetworkFailure(Describe(e), batch.Lines);
            }
            finally
            {
                lock (_sync) { _inFlight--; }
            }
        }
    }

    private void Send(Batch batch)
    {
        var body = batch.Body;
        var length = body.Length;
        var compressed = false;

        if (batch.Compress)
        {
            length = Compress(body, out body);
            compressed = true;
        }

        string lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var content = new ByteArrayContent(body, 0, length);
                content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                if (compressed)
                {
                    content.Headers.ContentEncoding.Add("gzip");
                }

                using var response = EnsureClient().PostAsync(batch.Uri, content).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode) return;

                var responseBody = batch.CaptureResponse
                    ? response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    : null;

                RecordHttpFailure($"{(int)response.StatusCode} {response.ReasonPhrase}", responseBody, batch.Lines);
                return;
            }
            catch (Exception e)
            {
                lastError = Describe(e);
                if (!_running || attempt == MaxAttempts) break;
                Thread.Sleep(RetryDelayMilliseconds * attempt);
            }
        }

        RecordNetworkFailure(lastError ?? "request failed", batch.Lines);
    }

    private int Compress(byte[] body, out byte[] compressed)
    {
        _compressionBuffer ??= new MemoryStream(1 << 16);
        _compressionBuffer.SetLength(0);

        using (var gzip = new GZipStream(_compressionBuffer, CompressionLevel.Fastest, true))
        {
            gzip.Write(body, 0, body.Length);
        }

        compressed = _compressionBuffer.GetBuffer();
        return (int)_compressionBuffer.Length;
    }

    private HttpClient EnsureClient()
    {
        if (_client != null) return _client;

        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(_timeoutSeconds) };
        _client.DefaultRequestHeaders.ExpectContinue = false;
        return _client;
    }

    private void RecordNetworkFailure(string error, int lines)
    {
        Interlocked.Add(ref _droppedLines, lines);

        lock (_sync)
        {
            _networkFailures++;
            _lastNetworkError = error;
        }
    }

    private void RecordHttpFailure(string error, string responseBody, int lines)
    {
        Interlocked.Add(ref _droppedLines, lines);

        lock (_sync)
        {
            _httpFailures++;
            _lastHttpError = error;
            if (responseBody != null) _lastResponseBody = responseBody;
        }
    }

    private static string Describe(Exception e) => e.GetBaseException().Message;
}
