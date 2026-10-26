using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;
using System.Drawing;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 3 || !Guid.TryParseExact(args[0], "N", out _) ||
            args[1] is not ("normal" or "refuse" or "hung" or "disabled")) return 2;
        string nonce = args[0], mode = args[1], folder = Path.GetFullPath(args[2]);
        if (!folder.Split(Path.DirectorySeparatorChar).Contains("operational-fixtures") || !Directory.Exists(folder)) return 3;
        using var quit = EventWaitHandle.OpenExisting(@"Local\Respiro.Operational." + nonce);
        _ = Task.Run(() => { quit.WaitOne(); Environment.Exit(0); });
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(97), null, 20000, Timeout.Infinite);
        using var form = new FixtureWindow { Text = "Respiro — fixture descartável " + mode, Width = 420, Height = 130, ShowInTaskbar = true };
        form.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Janela de teste. Nenhum documento ou dado pessoal.\nModo: " + mode, Padding = new Padding(12) });
        form.FormClosing += (_, e) =>
        {
            bool refuse = mode == "refuse"; e.Cancel = refuse;
            File.AppendAllText(Path.Combine(folder, "events.jsonl"), JsonSerializer.Serialize(new { Kind = "WM_CLOSE", Nonce = nonce, Cancelled = refuse, At = DateTimeOffset.UtcNow }) + "\n");
        };
        form.Shown += (_, _) =>
        {
            if (mode == "disabled") form.Enabled = false;
            using var current = Process.GetCurrentProcess();
            File.WriteAllText(Path.Combine(folder, "ready.tmp"), JsonSerializer.Serialize(new { Nonce = nonce, Mode = mode, Pid = current.Id, Started = current.StartTime.ToUniversalTime().Ticks, Hwnd = form.Handle.ToInt64() }));
            File.Move(Path.Combine(folder, "ready.tmp"), Path.Combine(folder, "ready.json"));
            if (mode == "hung") form.BeginInvoke((Action)(() =>
            {
                File.WriteAllText(Path.Combine(folder, "blocked.txt"), nonce);
                Thread.Sleep(10000); // Only this disposable UI thread; the exit event remains responsive.
            }));
        };
        Application.Run(form);
        return 0;
    }
    sealed class FixtureWindow : Form { protected override bool ShowWithoutActivation => true; }
}
