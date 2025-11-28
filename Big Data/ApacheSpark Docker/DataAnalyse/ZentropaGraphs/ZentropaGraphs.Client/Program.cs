using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace ZentropaGraphs.Client;

internal class Program
{
    private static async Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);

        var hostBase = new Uri(builder.HostEnvironment.BaseAddress, UriKind.Absolute);
        var staticAssetBase = new Uri(hostBase, "_content/ZentropaGraphs.Client/");

        builder.Services.AddScoped(_ => new HttpClient { BaseAddress = staticAssetBase });

        await builder.Build().RunAsync();
    }
}
