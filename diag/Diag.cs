// Diagnostic: list media sessions, print metadata/timeline, optionally toggle play/pause or seek.
// Usage: Diag.exe [toggle | seek <seconds>]
using System;
using System.Linq;
using Windows.Media.Control;

static class Ext
{
    public static T Wait<T>(this Windows.Foundation.IAsyncOperation<T> op)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
        op.Completed = (o, st) => { if (st == Windows.Foundation.AsyncStatus.Completed) tcs.SetResult(o.GetResults()); else tcs.SetException(new Exception("Async " + st)); };
        return tcs.Task.Result;
    }
}

class Diag
{
    static void Main(string[] args)
    {
        var mgr = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().Wait();
        var sessions = mgr.GetSessions();
        Console.WriteLine("Sessions: " + sessions.Count);
        foreach (var s in sessions)
        {
            var props = s.TryGetMediaPropertiesAsync().Wait();
            var info = s.GetPlaybackInfo();
            var tl = s.GetTimelineProperties();
            Console.WriteLine("--- " + s.SourceAppUserModelId);
            Console.WriteLine("  Title:    " + props.Title + " | Artist: " + props.Artist);
            Console.WriteLine("  Status:   " + info.PlaybackStatus + " | type " + info.PlaybackType);
            Console.WriteLine("  Pos/End:  " + tl.Position + " / " + tl.EndTime + " (start " + tl.StartTime + ", maxSeek " + tl.MaxSeekTime + ")");
            Console.WriteLine("  Updated:  " + tl.LastUpdatedTime.ToLocalTime());
            Console.WriteLine("  Can play/pause/toggle/seek: " + info.Controls.IsPlayEnabled + "/" + info.Controls.IsPauseEnabled + "/" + info.Controls.IsPlayPauseToggleEnabled + "/" + info.Controls.IsPlaybackPositionEnabled);
        }
        var chrome = sessions.FirstOrDefault(x => x.SourceAppUserModelId.IndexOf("chrome", StringComparison.OrdinalIgnoreCase) >= 0);
        if (chrome == null || args.Length == 0) return;
        if (args[0] == "toggle")
            Console.WriteLine("Toggle: " + chrome.TryTogglePlayPauseAsync().Wait());
        else if (args[0] == "seek")
            Console.WriteLine("Seek: " + chrome.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(double.Parse(args[1])).Ticks).Wait());
        System.Threading.Thread.Sleep(800);
        var t2 = chrome.GetTimelineProperties();
        Console.WriteLine("After: " + chrome.GetPlaybackInfo().PlaybackStatus + " pos " + t2.Position);
    }
}

