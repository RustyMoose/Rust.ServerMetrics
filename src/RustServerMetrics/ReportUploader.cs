using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace RustServerMetrics;

internal class ReportUploader : MonoBehaviour
{
    private const int SendBufferCapacity = 100000;

    private readonly Action _notifySubsequentNetworkFailuresAction;
    private readonly Action _notifySubsequentHttpFailuresAction;

    private readonly Queue<string> _sendBuffer = new(SendBufferCapacity);
    private readonly StringBuilder _payloadBuilder = new();

    private bool _isRunning;
    private ushort _attempt;
    private byte[] _data;
    private Uri _uri;
    private MetricsLogger _metricsLogger;

    private char[] _charBuffer = new char[8192 * 4];

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
    public int BufferSize => _sendBuffer.Count;

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
        if (_sendBuffer.Count == SendBufferCapacity)
        {
            _sendBuffer.Dequeue();
        }

        _sendBuffer.Enqueue(payload);

        if (!_isRunning)
        {
            StartCoroutine(SendBufferLoop());
        }
    }

    private IEnumerator SendBufferLoop()
    {
        _isRunning = true;
        yield return null;

        while (_sendBuffer.Count > 0 && _isRunning)
        {
            var amountToTake = Mathf.Min(_sendBuffer.Count, BatchSize);
            for (var i = 0; i < amountToTake; i++)
            {
                _payloadBuilder.Append(_sendBuffer.Dequeue());
                _payloadBuilder.Append("\n");
            }
            _attempt = 0;

            // more GC friendly GetBytes implementation
            if (_payloadBuilder.Length > _charBuffer.Length)
            {
                _charBuffer = new char[_payloadBuilder.Length + 1024];
            }

            _payloadBuilder.CopyTo(0, _charBuffer, 0, _payloadBuilder.Length);
            _data = Encoding.UTF8.GetBytes(_charBuffer, 0, _payloadBuilder.Length);

            _uri = _metricsLogger.BaseUri;
            _payloadBuilder.Clear();
            yield return SendRequest();
        }
        _isRunning = false;
    }

    private IEnumerator SendRequest()
    {
        var request = new UnityWebRequest(_uri, UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler = new UploadHandlerRaw(_data),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = 15,
            useHttpContinue = true,
            redirectLimit = 5
        };
        yield return request.SendWebRequest();

        if (request.isNetworkError)
        {
            if (_attempt >= 2)
            {
                if (_throttleNetworkErrorMessages)
                {
                    _accumulatedNetworkErrors += 1;
                }
                else
                {
                    Debug.LogError($"Two consecutive network failures occurred while submitting a batch of metrics");
                    InvokeHandler.Invoke(this, _notifySubsequentNetworkFailuresAction, 5);
                    _throttleNetworkErrorMessages = true;
                }
                yield break;
            }

            _attempt++;
            yield return SendRequest();
            yield break;
        }

        if (request.isHttpError)
        {
            if (_throttleHttpErrorMessages)
            {
                _accumulatedHttpErrors += 1;
            }
            else
            {
                Debug.LogError($"A HTTP error occurred while submitting batch of metrics: {request.error}");
                if (_metricsLogger.Configuration?.DebugLogging == true) Debug.LogError(request.downloadHandler.text);
                InvokeHandler.Invoke(this, _notifySubsequentHttpFailuresAction, 5);
                _throttleHttpErrorMessages = true;
            }
        }
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

    public void Stop()
    {
        _isRunning = false;
        StopAllCoroutines();
    }
}