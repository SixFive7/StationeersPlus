using System;
using System.Collections;
using System.IO;
using System.Threading;
using UnityEngine;

namespace ClientDriver
{
    /// <summary>
    /// Full-backbuffer screen capture.
    ///
    /// <c>ScreenCapture.CaptureScreenshotAsTexture()</c> after
    /// <c>WaitForEndOfFrame</c> is the only option that includes overlay UI. The
    /// game's own <c>GameManager.CreateScreenShot</c> renders a Camera into a
    /// RenderTexture, which is fine for a world thumbnail but silently omits every
    /// uGUI canvas and the ImGui console and settings panels. Anything that needs to
    /// prove a panel rendered readable text must go through the backbuffer.
    /// </summary>
    internal static class Screenshot
    {
        private sealed class Capture
        {
            public byte[] Png;
            public string Error;
            public int Width;
            public int Height;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        /// <summary>
        /// Captures a PNG. Called from the HTTP thread: schedules a coroutine on the
        /// main thread and blocks until it produces bytes.
        /// </summary>
        internal static byte[] CapturePng(int superSize, int maxWidth, int timeoutMs, out string error, out int width, out int height)
        {
            var capture = new Capture();
            error = null;
            width = 0;
            height = 0;

            MainThreadPump.Post(() =>
            {
                try { MainThreadPump.RunCoroutine(CaptureRoutine(capture, superSize, maxWidth)); }
                catch (Exception ex) { capture.Error = ex.ToString(); capture.Done.Set(); }
            });

            if (!capture.Done.WaitOne(timeoutMs))
            {
                error = "screenshot timed out after " + timeoutMs + " ms";
                return null;
            }

            error = capture.Error;
            width = capture.Width;
            height = capture.Height;
            return capture.Png;
        }

        private static IEnumerator CaptureRoutine(Capture capture, int superSize, int maxWidth)
        {
            yield return new WaitForEndOfFrame();

            Texture2D tex = null;
            Texture2D scaled = null;
            try
            {
                tex = superSize > 1
                    ? ScreenCapture.CaptureScreenshotAsTexture(superSize)
                    : ScreenCapture.CaptureScreenshotAsTexture();

                if (tex == null)
                {
                    capture.Error = "CaptureScreenshotAsTexture returned null";
                }
                else
                {
                    var source = tex;
                    if (maxWidth > 0 && tex.width > maxWidth)
                    {
                        scaled = Downscale(tex, maxWidth);
                        if (scaled != null) source = scaled;
                    }

                    capture.Width = source.width;
                    capture.Height = source.height;
                    capture.Png = source.EncodeToPNG();
                    if (capture.Png == null) capture.Error = "EncodeToPNG returned null";
                }
            }
            catch (Exception ex)
            {
                capture.Error = ex.ToString();
            }
            finally
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                if (scaled != null) UnityEngine.Object.Destroy(scaled);
                capture.Done.Set();
            }
        }

        /// <summary>
        /// GPU bilinear downscale. A 4K backbuffer encodes to roughly 6 MB of PNG,
        /// which is a lot to push through the control plane when the question is
        /// usually "does this panel read correctly". Blitting through a smaller
        /// RenderTexture costs one frame's worth of nothing and cuts that by an
        /// order of magnitude, while staying sharp enough to read UI text.
        /// </summary>
        private static Texture2D Downscale(Texture2D source, int maxWidth)
        {
            int width = maxWidth;
            int height = Mathf.Max(1, Mathf.RoundToInt(source.height * (maxWidth / (float)source.width)));

            RenderTexture rt = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                rt.filterMode = FilterMode.Bilinear;
                source.filterMode = FilterMode.Bilinear;
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var result = new Texture2D(width, height, TextureFormat.RGB24, false);
                result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                result.Apply();
                return result;
            }
            catch
            {
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }

        internal static string WriteToDisk(byte[] png, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, png);
            return path;
        }
    }
}
