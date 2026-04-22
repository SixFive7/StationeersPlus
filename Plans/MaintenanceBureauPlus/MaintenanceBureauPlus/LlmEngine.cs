using LLama;
using LLama.Common;
using LLama.Sampling;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MaintenanceBureauPlus
{
    // Wraps LLamaSharp for CPU inference on a background thread.
    // Requests carry fully-formed prompt strings (ChatML-formatted by the caller).
    // One request at a time; results return via the callback, which the caller is
    // responsible for marshaling to the main thread via MainThreadQueue.
    public class LlmEngine : IDisposable
    {
        private LLamaWeights _model;
        private LLamaContext _context;

        private readonly ConcurrentQueue<InferenceRequest> _queue = new ConcurrentQueue<InferenceRequest>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Thread _workerThread;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);

        public bool IsLoaded { get { return _model != null; } }

        public void Load(string modelPath, int contextSize, int threads)
        {
            var parameters = new ModelParams(modelPath)
            {
                ContextSize = (uint?)contextSize,
                GpuLayerCount = 0,
                Threads = threads,
                BatchThreads = threads,
            };

            _model = LLamaWeights.LoadFromFile(parameters);
            _context = _model.CreateContext(parameters);

            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "MBP-Inference",
                Priority = ThreadPriority.BelowNormal
            };
            _workerThread.Start();
        }

        public void Enqueue(string prompt, int maxTokens, Action<string> onComplete)
        {
            if (!IsLoaded)
            {
                onComplete?.Invoke("[model not loaded]");
                return;
            }

            _queue.Enqueue(new InferenceRequest
            {
                Prompt = prompt ?? string.Empty,
                MaxTokens = maxTokens,
                OnComplete = onComplete
            });
            _signal.Set();
        }

        private void WorkerLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                _signal.WaitOne(1000);

                while (_queue.TryDequeue(out var request))
                {
                    if (_cts.IsCancellationRequested) return;

                    try
                    {
                        var result = RunInference(request);
                        request.OnComplete?.Invoke(result);
                    }
                    catch (Exception e)
                    {
                        MaintenanceBureauPlusPlugin.Log.LogError("Inference failed: " + e.Message);
                        request.OnComplete?.Invoke("[signal lost]");
                    }
                }
            }
        }

        private string RunInference(InferenceRequest request)
        {
            var executor = new StatelessExecutor(_model, _context.Params);
            var inferenceParams = new InferenceParams
            {
                MaxTokens = request.MaxTokens,
                AntiPrompts = new[] { "<|im_end|>", "<|endoftext|>" },
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = MaintenanceBureauPlusPlugin.Temperature
                }
            };

            var result = new StringBuilder();

            // Reflection-based async-enumerator drain. Mono's JIT resolves
            // IAsyncEnumerator<T> to a version that lacks MoveNextAsync() even
            // with Microsoft.Bcl.AsyncInterfaces loaded, so compile-time
            // interface calls throw "Method not found" at runtime. Calling
            // MoveNextAsync / Current / DisposeAsync / AsTask by name on the
            // concrete LLamaSharp state-machine type sidesteps the interface
            // lookup and always resolves.
            var asyncEnum = executor.InferAsync(request.Prompt, inferenceParams, _cts.Token);
            var enumeratorObj = asyncEnum.GetType()
                .GetMethod("GetAsyncEnumerator", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(asyncEnum, new object[] { _cts.Token });
            if (enumeratorObj == null)
                throw new InvalidOperationException("Could not obtain enumerator from " + asyncEnum.GetType().FullName);

            var enumType = enumeratorObj.GetType();
            var moveNextMethod = enumType.GetMethod("MoveNextAsync", BindingFlags.Public | BindingFlags.Instance);
            var currentProp = enumType.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
            var disposeAsyncMethod = enumType.GetMethod("DisposeAsync", BindingFlags.Public | BindingFlags.Instance);
            if (moveNextMethod == null || currentProp == null)
                throw new InvalidOperationException("MoveNextAsync or Current not found on " + enumType.FullName);

            try
            {
                while (true)
                {
                    var valueTask = moveNextMethod.Invoke(enumeratorObj, null);
                    var asTask = valueTask.GetType().GetMethod("AsTask");
                    var taskBool = (Task<bool>)asTask.Invoke(valueTask, null);
                    bool hasNext = taskBool.GetAwaiter().GetResult();
                    if (!hasNext) break;

                    var current = currentProp.GetValue(enumeratorObj) as string;
                    if (!string.IsNullOrEmpty(current))
                        result.Append(current);
                }
            }
            finally
            {
                try
                {
                    if (disposeAsyncMethod != null)
                    {
                        var dvt = disposeAsyncMethod.Invoke(enumeratorObj, null);
                        var asTask = dvt.GetType().GetMethod("AsTask");
                        var task = (Task)asTask.Invoke(dvt, null);
                        task.GetAwaiter().GetResult();
                    }
                }
                catch { /* best effort */ }
            }

            var text = result.ToString().Trim();
            var endIdx = text.IndexOf("<|im_end|>", StringComparison.Ordinal);
            if (endIdx >= 0) text = text.Substring(0, endIdx).Trim();
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
            public string Prompt;
            public int MaxTokens;
            public Action<string> OnComplete;
        }
    }
}
