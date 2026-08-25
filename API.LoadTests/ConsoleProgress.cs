namespace API.LoadTests;

/// <summary>
/// A progress bar for the phases that are otherwise silent. Preloading a hundred thousand sessions
/// takes long enough to look like a hang.
/// <para>
/// Falls back to periodic lines when output is redirected, so a captured log does not fill up with
/// carriage returns.
/// </para>
/// </summary>
public sealed class ConsoleProgress(string label, int total) : IProgress<int>, IDisposable
{
    private const int Width = 32;

    private readonly bool _interactive = !Console.IsOutputRedirected;

    private int _done;

    private int _lastPercent = -1;

    public void Report(int value)
    {
        var done = Interlocked.Add(ref _done, value);

        var percent = total > 0 ? Math.Min(100, done * 100 / total) : 100;

        // Only redraw when the number would change, so a batch of small reports is not thousands of
        // writes to the console.
        if (percent == _lastPercent)
        {
            return;
        }

        _lastPercent = percent;

        if (_interactive)
        {
            var filled = percent * Width / 100;

            Console.Write($"\r  {label} [{new string('#', filled)}{new string('.', Width - filled)}] {percent,3}% ({done:N0}/{total:N0})");
        }
        else if (percent % 20 == 0)
        {
            Console.WriteLine($"  {label}: {percent}% ({done:N0}/{total:N0})");
        }
    }

    public void Dispose()
    {
        if (_interactive && _lastPercent >= 0)
        {
            Console.WriteLine();
        }
    }
}
