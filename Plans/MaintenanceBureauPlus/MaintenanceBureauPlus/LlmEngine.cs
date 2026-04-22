using LLama;
using LLama.Abstractions;
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
    //
    // Two modes:
    //
    //   Stateless (Enqueue)            - one-shot. Full prompt sent each call,
    //                                    fresh StatelessExecutor each time.
    //                                    Used for closing message, super summary.
    //
    //   Interactive (EnqueueInteractive) - KV-cache-reusing. BeginInteractiveCycle()
    //                                    creates a fresh LLamaContext + InteractiveExecutor.
    //                                    Subsequent EnqueueInteractive calls append only
    //                                    the delta text; the preamble + persona block
    //                                    + approval rules stay in the cache. Drops
    //                                    per-turn ingestion cost from ~2 kB to ~50 chars.
    //                                    Call EndInteractiveCycle() on cycle end to
    //                                    dispose the context.
    //
    // One request at a time; results return via the callback on the worker thread;
    // the caller marshals back to the main thread via MainThreadQueue.
    public class LlmEngine : IDisposable
    {
        private LLamaWeights _model;
        private ModelParams _params;

        private readonly ConcurrentQueue<InferenceRequest> _queue = new ConcurrentQueue<InferenceRequest>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Thread _workerThread;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);

        // Interactive cycle state. Accessed from both main thread (Begin/End) and
        // worker thread (RunInference). Lock around reads/writes.
        private readonly object _cycleLock = new object();
        private LLamaContext _interactiveContext;
        private InteractiveExecutor _interactiveExecutor;

        public bool IsLoaded { get { return _model != null; } }

        public void Load(string modelPath, int contextSize, int threads)
        {
            _params = new ModelParams(modelPath)
            {
                ContextSize = (uint?)contextSize,
                GpuLayerCount = 0,
                Threads = threads,
                BatchThreads = threads,
            };

            _model = LLamaWeights.LoadFromFile(_params);

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
                OnComplete = onComplete,
                Interactive = false,
            });
            _signal.Set();
        }

        public void EnqueueInteractive(string text, int maxTokens, Action<string> onComplete)
        {
            if (!IsLoaded)
            {
                onComplete?.Invoke("[model not loaded]");
                return;
            }

            _queue.Enqueue(new InferenceRequest
            {
                Prompt = text ?? string.Empty,
                MaxTokens = maxTokens,
                OnComplete = onComplete,
                Interactive = true,
            });
            _signal.Set();
        }

        public void BeginInteractiveCycle()
        {
            lock (_cycleLock)
            {
                EndInteractiveCycleLocked();
                try
                {
                    var ctx = _model.CreateContext(_params);
                    _interactiveContext = ctx;
                    _interactiveExecutor = new InteractiveExecutor(ctx);
                    MaintenanceBureauPlusPlugin.Log.LogInfo("[LlmEngine] Interactive cycle started.");
                }
                catch (Exception ex)
                {
                    MaintenanceBureauPlusPlugin.Log.LogError("[LlmEngine] BeginInteractiveCycle failed: " + ex);
                    _interactiveContext = null;
                    _interactiveExecutor = null;
                }
            }
        }

        public void EndInteractiveCycle()
        {
            lock (_cycleLock)
            {
                if (_interactiveExecutor == null && _interactiveContext == null) return;
                EndInteractiveCycleLocked();
                MaintenanceBureauPlusPlugin.Log.LogInfo("[LlmEngine] Interactive cycle ended.");
            }
        }

        private void EndInteractiveCycleLocked()
        {
            _interactiveExecutor = null;
            try { _interactiveContext?.Dispose(); } catch { }
            _interactiveContext = null;
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
                        "[LlmEngine] Inference start: mode=" + (request.Interactive ? "interactive" : "stateless") +
                        " promptChars=" + (request.Prompt != null ? request.Prompt.Length : 0) +
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
            ILLamaExecutor executor;
            if (request.Interactive)
            {
                lock (_cycleLock)
                {
                    executor = _interactiveExecutor;
                }
                if (executor == null)
                {
                    MaintenanceBureauPlusPlugin.Log.LogWarning("[LlmEngine] Interactive request with no active cycle; skipping.");
                    return string.Empty;
                }
            }
            else
            {
                executor = new StatelessExecutor(_model, _params);
            }

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

            // Reflection-based async-enumerator drain. LLamaSharp's compiler-
            // generated state machine implements IAsyncEnumerable<T> and
            // IAsyncEnumerator<T> as explicit interface implementations, and
            // Mono's JIT cannot resolve the interface at compile time.
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

        // Called from MaintenanceBureauPlusPlugin.OnDestroy during game shutdown.
        // Does NOT wait long for the worker to finish and does NOT dispose the
        // native model/context — both risk hanging the game process indefinitely
        // because the worker can be deep in native llama.cpp code that doesn't
        // respond to cancellation quickly. The OS reclaims the native memory
        // when the process exits.
        public void Dispose()
        {
            MaintenanceBureauPlusPlugin.Log?.LogInfo("[LlmEngine] Dispose starting.");
            try { _cts.Cancel(); } catch { }
            try { _signal.Set(); } catch { }

            if (_workerThread != null && !_workerThread.Join(500))
                MaintenanceBureauPlusPlugin.Log?.LogWarning("[LlmEngine] Worker did not exit within 500 ms; abandoning (OS will reclaim on process exit).");

            // Deliberately do not dispose _model, _interactiveContext. See class-doc
            // comment above. Drop the references so nothing else can accidentally
            // reach into a now-reclaimed native resource if the worker thread is
            // still alive.
            _interactiveExecutor = null;
            _interactiveContext = null;
            _model = null;

            MaintenanceBureauPlusPlugin.Log?.LogInfo("[LlmEngine] Dispose done.");
        }

        private struct InferenceRequest
        {
            public string Prompt;
            public int MaxTokens;
            public Action<string> OnComplete;
            public bool Interactive;
        }

        // Find an instance method by simple name or explicit-interface suffix.
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
