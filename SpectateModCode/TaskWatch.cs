using System;
using System.Threading.Tasks;

namespace SpectateMod.SpectateModCode;

public static class TaskWatch
{
    /// <summary>
    /// Runs <paramref name="then" /> once <paramref name="task" /> is over, however it ends. Awaited rather than
    /// ContinueWith'd: started on the main thread, the await resumes there too, which matters because what runs next
    /// usually ends up touching Godot nodes.
    /// </summary>
    public static async void WhenDone(Task task, Action then)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // Cancelled or failed; the game deals with that. We only care that it is over.
        }
        try
        {
            then();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Cleanup after a teammate's choice failed: {e}");
        }
    }
}
