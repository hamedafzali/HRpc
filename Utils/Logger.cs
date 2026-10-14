using System;

namespace HRpc.Utils
{
    [Obsolete("Unused by HRpc and will be removed in 2.0. Use your own logging (e.g. Microsoft.Extensions.Logging) instead.")]
    public static class Logger
    {
        public static void Info(string msg) => Console.WriteLine($"[INFO] {msg}");
        public static void Error(string msg) => Console.WriteLine($"[ERROR] {msg}");
    }
}
