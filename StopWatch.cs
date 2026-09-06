namespace Visive;

public class StopWatch
{
    private CancellationTokenSource _cts = new();
    private bool lap = false;
    private string Message = "";
    DateTime starttime;
    DateTime lastLap;

    public Task StartWatch()
    {
        _cts = new CancellationTokenSource();
        return watch(_cts.Token);
    }

    private async Task watch(CancellationToken token)
    {
        starttime = DateTime.Now;
        lastLap = starttime;

        while (_cts.IsCancellationRequested == false)
        {
            if (lap == true)
            {
                lap = false;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{Message}] completed in {(DateTime.Now - lastLap).TotalSeconds} Seconds");
                Console.ResetColor();
                Message = "";
                lastLap = DateTime.Now;
            }
            await Task.Delay(100, token);
        }
    }

    public void Cancel()
    {
        Console.WriteLine($"[Total] completed in {(DateTime.Now - starttime).TotalSeconds} Seconds");
        _cts?.Cancel();
    }

    public void Lap(string message)
    {
        lap = true;
        Message = message;
    }
}