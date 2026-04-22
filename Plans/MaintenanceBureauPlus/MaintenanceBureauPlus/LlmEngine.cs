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

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    MaintenanceBureauPlusPlugin.Log.LogInfo(
                        "[LlmEngine] Inference start: promptChars=" + (request.Prompt != null ? request.Prompt.Length : 0) +
                        " maxTokens=" + request.MaxTokens);
                    try
                    {
                        var result = RunInference(request);
                        sw.Stop();
                        MaintenanceBureauPlusPlugin.Log.LogInfo(
                            "[LlmEngine] Inference done: " + sw.ElapsedMilliseconds + " ms, " +
                            "resultChars=" + (result != null ? result.Length : 0));
                        request.OnComplete?.Invoke(result);
                    }
                    catch (Exception e)
                    {
                        sw.Stop();
                        MaintenanceBureauPlusPlugin.Log.LogError("[LlmEngine] Inference failed after " + sw.ElapsedMilliseconds + " ms: " + e);
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

            // Reflection-based async-enumerator drain. The LLamaSharp state
            // machine returned by InferAsync implements IAsyncEnumerable<T>
            // and IAsyncEnumerator<T> as EXPLICIT interface implementations,
            // so GetMethod("GetAsyncEnumerator", Public | Instance) returns
            // null. We have to scan with Public | NonPublic flags and match
            // by name suffix (explicit impls look like
            // "System.Collections.Generic.IAsyncEnumerable<System.String>.GetAsyncEnumerator").
            // Calling the method by reflection also sidesteps Mono's broken
            // IAsyncEnumerator<T> interface resolution.
            var asyncEnum = executor.InferAsync(request.Prompt, inferenceParams, _cts.Token);

            var getAsyncEnumerator = FindInstanceMethod(asyncEnum.GetType(), "GetAsyncEnumerator");
            if (getAsyncEnumerator == null)
                throw new InvalidOperationException("GetAsyncEnumerator not found on " + asyncEnum.GetType().FullName);

            var getAsyncParams = getAsyncEnumerator.GetParameters();
            var enumeratorObj = getAsyncEnumerator.Invoke(asyncEnum,
                getAsyncParams.Length == 1 ? new object[] { _cts.Token } : null);
            if (enumeratorObj == null)
                throw new InvalidOperationException("GetAsyncEnumerator returned null");

            var enumType = enumeratorObj.GetType();
            var moveNextMethod = FindInstanceMethod(enumType, "MoveNextAsync");
            var currentProp = FindInstanceProperty(enumType, "Current");
            var disposeAsyncMethod = FindInstanceMethod(enumType, "DisposeAsync");
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

        // Find a method by simple name or explicit-interface suffix.
        // e.g. "MoveNextAsync" matches both "MoveNextAsync" and
        // "System.Collections.Generic.IAsyncEnumerator<System.String>.MoveNextAsync".
        private static MethodInfo FindInstanceMethod(Type t, string simpleName)
        {
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var m in methods)
            {
                if (m.Name == simpleName || m.Name.EndsWith("." + simpleName))
                    return m;
            }
            return null;
        }

        private static PropertyInfo FindInstanceProperty(Type t, string simpleName)
        {
            var props = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var p in props)
            {
                if (p.Name == simpleName || p.Name.EndsWith("." + simpleName))
                    return p;
            }
            return null;
        }
    }
}
