using System;
using System.Text;
using UnityEngine;

namespace RustServerMetrics;

internal class ReportUploader : MonoBehaviour
{
    private const int SendBufferCapacity = 8 * 1024 * 1024;

    private const int MaxBatchCharacters = 60000;

    private const int RequestTimeoutSeconds = 15;

    private const float FlushInterval = 1f;

    private readonly Action _notifySubsequentNetworkFailuresAction;
    private readonly Action _notifySubsequentHttpFailuresAction;

    private readonly MetricsSendBuffer _sendBuffer = new(SendBufferCapacity);

    private MetricsUploadWorker _worker;
    private MetricsLogger _metricsLogger;
    private bool _isRunning;
    private float _nextFlush;

    private bool _throttleNetworkErrorMessages;
    private uint _accumulatedNetworkErrors;

    private bool _throttleHttpErrorMessages;
    private uint _accumulatedHttpErrors;

    private ushort BatchSize
    {
        get
        {
            var configVal = _metricsLogger.Configuration?.BatchSize ?? 1000;
            return configVal < 1000 ? (ushort)1000 : configVal;
        }
    }

    public bool IsRunning => _isRunning;
    public int BufferSize => _sendBuffer.LineCount;
    public int PendingBatches => _worker?.PendingBatches ?? 0;
    public long TotalDroppedReports => _sendBuffer.TotalDropped + (_worker?.DroppedLines ?? 0);

    public ReportUploader()
    {
        _notifySubsequentNetworkFailuresAction = NotifySubsequentNetworkFailures;
        _notifySubsequentHttpFailuresAction = NotifySubsequentHttpFailures;
    }

    public ReportUploader(Action notifySubsequentHttpFailuresAction)
    {
        _notifySubsequentHttpFailuresAction = notifySubsequentHttpFailuresAction;
    }

    private void Awake()
    {
        _metricsLogger = GetComponent<MetricsLogger>();
        if (_metricsLogger == null)
        {
            Debug.LogError("[ServerMetrics] ReportUploader failed to find the MetricsLogger component");
            Destroy(this);
        }
    }

    public void AddToSendBuffer(string payload)
    {
        _sendBuffer.Append(payload);
        _isRunning = true;
    }

    public void AddToSendBuffer(StringBuilder payload)
    {
        _sendBuffer.Append(payload);
        _isRunning = true;
    }

    private void Update()
    {
        if (_metricsLogger == null)
        {
            Stop();
            return;
        }

        if (_worker != null)
        {
            DrainFailures();
        }

        if (_isRunning)
        {
            PumpBatches();
        }
    }

    private void PumpBatches()
    {
        var queuedLines = _sendBuffer.LineCount;
        if (queuedLines == 0) return;

        var batchSize = BatchSize;

        if (queuedLines < batchSize && Time.realtimeSinceStartup < _nextFlush) return;
        _nextFlush = Time.realtimeSinceStartup + FlushInterval;

        var worker = EnsureWorker();
        var compress = _metricsLogger.Configuration?.CompressRequests ?? true;
        var captureResponse = _metricsLogger.Configuration?.DebugLogging == true;

        while (_sendBuffer.LineCount > 0 && worker.HasRoom)
        {
            var batch = _sendBuffer.TakeBatch(batchSize, MaxBatchCharacters, out var characterCount);
            var lines = _sendBuffer.LastBatchLineCount;
            var data = Encoding.UTF8.GetBytes(batch, 0, characterCount);

            if (worker.TryEnqueue(_metricsLogger.BaseUri, data, lines, compress, captureResponse)) continue;

            _sendBuffer.AddDropped(lines);
            break;
        }
    }

    private MetricsUploadWorker EnsureWorker() => _worker ??= new MetricsUploadWorker(RequestTimeoutSeconds);

    private void DrainFailures()
    {
        var networkFailures = _worker.TakeNetworkFailures(out var networkError);
        if (networkFailures > 0)
        {
            ReportNetworkFailures(networkFailures, networkError);
        }

        var httpFailures = _worker.TakeHttpFailures(out var httpError, out var responseBody);
        if (httpFailures > 0)
        {
            ReportHttpFailures(httpFailures, httpError, responseBody);
        }
    }

    private void ReportNetworkFailures(int failures, string error)
    {
        if (_throttleNetworkErrorMessages)
        {
            _accumulatedNetworkErrors += (uint)failures;
            return;
        }

        Debug.LogError($"Consecutive network failures occurred while submitting a batch of metrics: {error}");
        InvokeHandler.Invoke(this, _notifySubsequentNetworkFailuresAction, 5);
        _throttleNetworkErrorMessages = true;
        _accumulatedNetworkErrors += (uint)(failures - 1);
    }

    private void ReportHttpFailures(int failures, string error, string responseBody)
    {
        if (_throttleHttpErrorMessages)
        {
            _accumulatedHttpErrors += (uint)failures;
            return;
        }

        Debug.LogError($"A HTTP error occurred while submitting batch of metrics: {error}");
        if (responseBody != null) Debug.LogError(responseBody);
        InvokeHandler.Invoke(this, _notifySubsequentHttpFailuresAction, 5);
        _throttleHttpErrorMessages = true;
        _accumulatedHttpErrors += (uint)(failures - 1);
    }

    void NotifySubsequentNetworkFailures()
    {
        _throttleNetworkErrorMessages = false;
        if (_accumulatedNetworkErrors == 0) return;
        Debug.LogError($"{_accumulatedNetworkErrors} subsequent network errors occurred in the last 5 seconds");
        _accumulatedNetworkErrors = 0;
    }

    void NotifySubsequentHttpFailures()
    {
        _throttleHttpErrorMessages = false;
        if (_accumulatedHttpErrors == 0) return;
        Debug.LogError($"{_accumulatedHttpErrors} subsequent HTTP errors occurred in the last 5 seconds");
        _accumulatedHttpErrors = 0;
    }

    void OnDestroy()
    {
        Stop();
    }

    private void DisposeWorker()
    {
        var worker = _worker;
        _worker = null;
        worker?.Dispose();
    }

    public void Stop()
    {
        _isRunning = false;

        _worker?.DropQueued();
        DisposeWorker();
    }
}
