using MinVer.Lib;

namespace MinVerTests.Lib.Infra
{
    internal class NullLogger : ILogger
    {
        public static readonly NullLogger Instance =
#if NET
            new();
#else
            new NullLogger();
#endif

        private NullLogger() { }

        public bool IsTraceEnabled => false;

        public bool IsDebugEnabled => false;

        public void Debug(string message) { }

        public void Info(string message) { }

        public void Trace(string message) { }

        public void Warn(int code, string message) { }
    }
}
