namespace Visive;

public class StopWatch
{
    private CancellationTokenSource _cts = new();
    private bool lap = false;
    private string Message = "";

    public Task StartWatch()
    {
        _cts = new CancellationTokenSource();
        return watch(_cts.Token);
    }

    private async Task watch(CancellationToken token)
    {
        DateTime starttime = DateTime.Now;
        DateTime lastLap = starttime;

        while(_cts.IsCancellationRequested == false)
        {
            if(lap == true)
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
        Console.WriteLine($"[Final Time] completed in {(DateTime.Now - starttime).TotalSeconds} Seconds");
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    public void Lap(string message)
    {
        lap = true;
        Message = message;
    }
}