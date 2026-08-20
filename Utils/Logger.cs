using System;

namespace HRpc.Utils
{
    public static class Logger
    {
        public static void Info(string msg) => Console.WriteLine($"[INFO] {msg}");
        public static void Error(string msg) => Console.WriteLine($"[ERROR] {msg}");
    }
}
