using System;
using System.Diagnostics;

namespace Visive;

public class StopWatch
{
    private Stopwatch _sw = new Stopwatch();
    private TimeSpan _lastLap;

    public void StartWatch()
    {
        _sw.Start();
        _lastLap = TimeSpan.Zero;
    }

    public void Cancel()
    {
        _sw.Stop();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[Total] completed in {_sw.Elapsed.TotalSeconds} Seconds");
        Console.ResetColor();
    }

    public void Lap(string message)
    {
        var current = _sw.Elapsed;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[{message}] completed in {(current - _lastLap).TotalSeconds} Seconds");
        Console.ResetColor();
        _lastLap = current;
    }
}