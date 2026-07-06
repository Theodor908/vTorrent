using System;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Bench.Bench;

public sealed class HotkeyHandler
{
    public event Action<int>? GroupSelected;       // 0-5 (from keys 1-6)
    public event Action? NavigateUp;
    public event Action? NavigateDown;
    public event Action? IncreaseValue;
    public event Action? DecreaseValue;
    public event Action? TakeSnapshot;
    public event Action? CompareSnapshots;
    public event Action? ExportProfile;
    public event Action? ResetSettings;
    public event Action? TogglePause;
    public event Action? Quit;

    public Task RunAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                if (!Console.KeyAvailable) { Thread.Sleep(50); continue; }
                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.D1: case ConsoleKey.NumPad1: GroupSelected?.Invoke(0); break;
                    case ConsoleKey.D2: case ConsoleKey.NumPad2: GroupSelected?.Invoke(1); break;
                    case ConsoleKey.D3: case ConsoleKey.NumPad3: GroupSelected?.Invoke(2); break;
                    case ConsoleKey.D4: case ConsoleKey.NumPad4: GroupSelected?.Invoke(3); break;
                    case ConsoleKey.D5: case ConsoleKey.NumPad5: GroupSelected?.Invoke(4); break;
                    case ConsoleKey.D6: case ConsoleKey.NumPad6: GroupSelected?.Invoke(5); break;
                    case ConsoleKey.UpArrow: NavigateUp?.Invoke(); break;
                    case ConsoleKey.DownArrow: NavigateDown?.Invoke(); break;
                    case ConsoleKey.RightArrow: IncreaseValue?.Invoke(); break;
                    case ConsoleKey.LeftArrow: DecreaseValue?.Invoke(); break;
                    case ConsoleKey.S: TakeSnapshot?.Invoke(); break;
                    case ConsoleKey.C: CompareSnapshots?.Invoke(); break;
                    case ConsoleKey.E: ExportProfile?.Invoke(); break;
                    case ConsoleKey.R: ResetSettings?.Invoke(); break;
                    case ConsoleKey.P: TogglePause?.Invoke(); break;
                    case ConsoleKey.Q: Quit?.Invoke(); break;
                }
            }
        }, ct);
    }
}
