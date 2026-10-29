using System.Windows.Automation;

namespace TaskbarTiles.Host.Taskbar;

/// <summary>Where a taskbar's app icons end, in physical pixels; null when it cannot tell.</summary>
public interface IIconEdgeReader
{
    /// <summary>Called on a thread-pool thread: a call into Explorer can stall.</summary>
    int? IconsRight(IntPtr taskbar);
}

/// <summary>
/// Reads the right edge of the last app button through UI Automation (the accessibility API screen readers use). The
/// taskbar's own icon area window does not follow the icons on Windows 11, so it can end an icon or two early.
/// </summary>
public sealed class UiaIconEdgeReader : IIconEdgeReader
{
    const string AppButtonClass = "Taskbar.TaskListButtonAutomationPeer";

    static readonly Condition AppButtons = new PropertyCondition(AutomationElement.ClassNameProperty, AppButtonClass);

    public int? IconsRight(IntPtr taskbar)
    {
        var root = AutomationElement.FromHandle(taskbar); // outside the cache: a cached-only root cannot be searched
        var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
        cache.Add(AutomationElement.BoundingRectangleProperty);
        using (cache.Activate())
        {
            var buttons = root.FindAll(TreeScope.Descendants, AppButtons);
            int? right = null;
            foreach (AutomationElement button in buttons)
            {
                var r = button.Cached.BoundingRectangle;
                if (r.IsEmpty || r.Width <= 0) continue;
                right = Math.Max(right ?? int.MinValue, (int)Math.Ceiling(r.Right));
            }
            return right;
        }
    }
}
