using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using System.IO;
using ZentropaGraphs.Client.Pages;
using ZentropaGraphs.Components;

namespace ZentropaGraphs
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents()
                .AddInteractiveWebAssemblyComponents();

            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped(sp =>
            {
                var accessor = sp.GetRequiredService<IHttpContextAccessor>();
                var request = accessor.HttpContext?.Request
                              ?? throw new InvalidOperationException("Unable to resolve HttpContext for HttpClient base address.");

                var rootUri = new Uri($"{request.Scheme}://{request.Host}{request.PathBase}/", UriKind.Absolute);
                var baseUri = new Uri(rootUri, "_content/ZentropaGraphs.Client/");
                return new HttpClient { BaseAddress = baseUri };
            });

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseWebAssemblyDebugging();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();

            app.UseStaticFiles();

            // Expose client-side wwwroot under the _content path so JSON assets resolve.
            var clientAssetsPath = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "ZentropaGraphs.Client", "wwwroot"));
            if (Directory.Exists(clientAssetsPath))
            {
                app.UseStaticFiles(new StaticFileOptions
                {
                    RequestPath = "/_content/ZentropaGraphs.Client",
                    FileProvider = new PhysicalFileProvider(clientAssetsPath)
                });
            }

            app.UseAntiforgery();

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode()
                .AddInteractiveWebAssemblyRenderMode()
                .AddAdditionalAssemblies(typeof(Client._Imports).Assembly);

            app.Run();
        }
    }
}
