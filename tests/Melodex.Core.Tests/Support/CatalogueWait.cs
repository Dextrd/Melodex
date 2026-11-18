using Melodex.Client;

namespace Melodex.Core.Tests.Support;

public static class CatalogueWait
{
    /// <summary>Waits for the catalogue update a scan queues after it, so the catalogue is filled in.</summary>
    public static async Task WaitForCatalogueAsync(this MelodexClient api)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            var update = (await api.ListTasksAsync(active: true)).FirstOrDefault(t => t.Kind == "catalogue-update");
            if (update is null)
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException("The catalogue update didn't finish.");
    }
}
