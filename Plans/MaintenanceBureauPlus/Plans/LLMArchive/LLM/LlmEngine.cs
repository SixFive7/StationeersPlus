using LLama;
using LLama.Common;
using LLama.Sampling;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LLM
{
    /// <summary>
    /// Wraps LLamaSharp for async CPU inference on a background thread.
    /// Thread-safe: requests are queued and processed one at a time.
    /// </summary>
    public class LlmEngine : IDisposable
    {
        private LLamaWeights _model;
        private LLamaContext _context;

        private readonly ConcurrentQueue<InferenceRequest> _queue = new ConcurrentQueue<InferenceRequest>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Thread _workerThread;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);

        private int _contextSize;

        public bool IsLoaded => _model != null;

        public void Load(string modelPath, int contextSize, int threads)
        {
            _contextSize = contextSize;

            var parameters = new ModelParams(modelPath)
            {
                ContextSize = (uint?)contextSize,
                GpuLayerCount = 0, // CPU only, no GPU assumed
                Threads = threads,
                BatchThreads = threads,
            };

            _model = LLamaWeights.LoadFromFile(parameters);
            _context = _model.CreateContext(parameters);

            // Start the background worker that drains the inference queue
            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "LLM-Inference",
                Priority = ThreadPriority.BelowNormal
            };
            _workerThread.Start();
        }

        /// <summary>
        /// Enqueues a chat request. The callback fires on the worker thread
        /// when inference completes; the caller is responsible for dispatching
        /// the result back to the main thread.
        /// </summary>
        public void Enqueue(string playerName, string message, Action<string> onComplete)
        {
            if (!IsLoaded)
            {
                onComplete?.Invoke("[model not loaded]");
                return;
            }

            _queue.Enqueue(new InferenceRequest
            {
                PlayerName = playerName,
                Message = message,
                OnComplete = onComplete
            });
            _signal.Set();
        }

        private void WorkerLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                _signal.WaitOne(1000); // wake on signal or poll every second

                while (_queue.TryDequeue(out var request))
                {
                    if (_cts.IsCancellationRequested) return;

                    try
                    {
                        var result = RunInference(request.PlayerName, request.Message);
                        request.OnComplete?.Invoke(result);
                    }
                    catch (Exception e)
                    {
                        LlmPlugin.Log.LogError($"Inference failed: {e.Message}");
                        request.OnComplete?.Invoke("[signal lost]");
                    }
                }
            }
        }

        private string RunInference(string playerName, string message)
        {
            // Build a chat-style prompt using Qwen2.5 instruct format.
            // Other models may need a different template; this is the one
            // Qwen2.5-Instruct expects.
            var systemPrompt = LlmPlugin.SystemPrompt.Value;
            var prompt = new StringBuilder();
            prompt.Append("<|im_start|>system\n");
            prompt.Append(systemPrompt);
            prompt.Append("<|im_end|>\n");
            prompt.Append("<|im_start|>user\n");
            prompt.Append($"[{playerName}]: {message}");
            prompt.Append("<|im_end|>\n");
            prompt.Append("<|im_start|>assistant\n");

            var executor = new StatelessExecutor(_model, _context.Params);
            var inferenceParams = new InferenceParams
            {
                MaxTokens = LlmPlugin.MaxTokens.Value,
                AntiPrompts = new[] { "<|im_end|>", "\n\n" },
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = LlmPlugin.Temperature.Value
                }
            };

            // InferAsync returns IAsyncEnumerable<string>. On .NET Framework 4.7.2
            // there is no await foreach or ToBlockingEnumerable(), so we drain it
            // manually using the async enumerator and blocking on each MoveNextAsync.
            var result = new StringBuilder();
            var asyncEnum = executor.InferAsync(prompt.ToString(), inferenceParams, _cts.Token);
            var enumerator = asyncEnum.GetAsyncEnumerator(_cts.Token);
            try
            {
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    result.Append(enumerator.Current);
                }
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            var text = result.ToString().Trim();

            // Strip any trailing template tokens the model may have appended
            var endIdx = text.IndexOf("<|im_end|>", StringComparison.Ordinal);
            if (endIdx >= 0)
                text = text.Substring(0, endIdx).Trim();

            return text;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _signal.Set();
            _workerThread?.Join(5000);

            _context?.Dispose();
            _model?.Dispose();
            _context = null;
            _model = null;
        }

        private struct InferenceRequest
        {
            public string PlayerName;
            public string Message;
            public Action<string> OnComplete;
        }
    }
}
