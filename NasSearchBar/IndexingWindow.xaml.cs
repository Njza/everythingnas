using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace NasSearchBar;

// Shown instead of the bar while this PC's index is still being built; closes itself once it is ready
public partial class IndexingWindow : Window
{
    public IndexingWindow(LocalIndex index)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Theme.Frame(this);
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        void Tick()
        {
            if (index.Ready)
            {
                timer.Stop();
                Close();
                return;
            }
            Count.Text = $"已读到 {index.Found:N0} 项 · {clock.Elapsed.TotalSeconds:0} 秒";
        }
        timer.Tick += (_, _) => Tick();
        Closed += (_, _) => timer.Stop();
        timer.Start();
        Loaded += (_, _) => Tick();  // not in here: closing a window that was never shown would throw
    }
}
