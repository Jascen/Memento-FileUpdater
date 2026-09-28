namespace FileUpdaterServer;

//Appends log lines to the LogFilePath file, in the same format as the console
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public FileLoggerProvider(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_lock)
            _writer.Dispose();
    }

    private void Write(string line)
    {
        lock (_lock)
            _writer.WriteLine(line);
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        //Levels are filtered by the logging builder, like the console logger
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {LevelName(logLevel)}: {_category}[{eventId.Id}] {formatter(state, exception)}";
            if (exception != null)
                line += Environment.NewLine + exception;
            _provider.Write(line);
        }

        private static string LevelName(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => level.ToString()
        };
    }
}
