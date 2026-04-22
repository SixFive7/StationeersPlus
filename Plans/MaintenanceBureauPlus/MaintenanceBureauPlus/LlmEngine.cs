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

        // Number of inference requests that have been enqueued but not yet
        // completed. Incremented in Enqueue/EnqueueInteractive, decremented
        // in WorkerLoop after the request finishes (success or failure).
        // Read by ChatPatch to decide whether to enqueue a new player turn
        // or auto-respond with a busy notice. Volatile read is sufficient;
        // we never need a totally-consistent count.
        private int _inflight;
        public bool IsBusy { get { return System.Threading.Volatile.Read(ref _inflight) > 0; } }

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

            Interlocked.Increment(ref _inflight);
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

            Interlocked.Increment(ref _inflight);
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
                    finally
                    {
                        Interlocked.Decrement(ref _inflight);
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
                // Anti-prompts:
                //   <|im_end|>, <|endoftext|>, <|im_start|>  - Qwen chat-template
                //     markers. If the model starts emitting a new turn block or
                //     end-of-text, stop before the markers leak into the reply.
                //   \n**[Turn, \n[Turn, \nPHASE:  - hallucination guards. Our
                //     user-line format contains these markers, so the model
                //     tries to continue the pattern by inventing fake user
                //     turns after its assistant reply. Stop at the first sign.
                AntiPrompts = new[]
                {
                    "<|im_end|>",
                    "<|endoftext|>",
                    "<|im_start|>",
                    "\n**[Turn",
                    "\n[Turn",
                    "\nPHASE:",
                    // The model pattern-matches our PHASE:* directives (in
                    // SystemPrompts.ApprovalTagRules) against the bracketed
                    // [CONTINUE]/[APPROVED] tags and emits "[PHASE:FINAL]"
                    // inline at the end of replies. Catch the bracket form
                    // before it leaks.
                    "[PHASE",
                    "[Phase",
                    "[phase",
                },
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
        // Fire-and-forget: signal the worker to stop, drop references, return
        // immediately. Does NOT join the worker and does NOT dispose the native
        // model/context. The worker can be deep in a multi-second native
        // llama.cpp call that ignores cancellation; joining it (even briefly)
        // would block Unity's shutdown for that long. Native llama_free_model
        // can also block on a loader-lock-held thread. The OS reclaims native
        // memory + the background worker thread when the process exits.
        //
        // To stop the LLamaWeights/LLamaContext finalizers from running native
        // teardown later in the CLR shutdown path (which is what makes ALT+F4
        // hang), reach into the LLamaSharp objects and null out their internal
        // SafeHandles so finalization becomes a no-op. Best-effort; if
        // reflection misses a future LLamaSharp version, we just lose a few
        // shutdown seconds.
        public void Dispose()
        {
            MaintenanceBureauPlusPlugin.Log?.LogInfo("[LlmEngine] Dispose starting.");
            try { _cts.Cancel(); } catch { }
            try { _signal.Set(); } catch { }

            NeuterNativeFinalizers(_interactiveContext);
            NeuterNativeFinalizers(_model);

            _interactiveExecutor = null;
            _interactiveContext = null;
            _model = null;

            MaintenanceBureauPlusPlugin.Log?.LogInfo("[LlmEngine] Dispose done.");
        }

        // Reflection-walk an LLamaSharp object and SuppressFinalize it plus
        // any SafeHandle field it owns, so the CLR shutdown path doesn't end
        // up calling native llama.cpp teardown that can hang the process.
        private static void NeuterNativeFinalizers(object obj)
        {
            if (obj == null) return;
            try
            {
                GC.SuppressFinalize(obj);
                var fields = obj.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var f in fields)
                {
                    if (typeof(System.Runtime.InteropServices.SafeHandle).IsAssignableFrom(f.FieldType))
                    {
                        var handle = f.GetValue(obj) as System.Runtime.InteropServices.SafeHandle;
                        if (handle != null)
                        {
                            try { GC.SuppressFinalize(handle); } catch { }
                            try { handle.SetHandleAsInvalid(); } catch { }
                        }
                    }
                }
            }
            catch { /* best-effort */ }
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
