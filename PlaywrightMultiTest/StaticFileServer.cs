using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using System.Net;

namespace PlaywrightMultiTest
{
    public class StaticFileServer
    {
        WebApplication? app;
        Task? runningTask;
        string WWWRoot;
        string RequestPath;
        string Url;
        string devcertPath;
        string SolutionRoot;
        public StaticFileServer(string wwwroot, string url, string requestPath = "")
        {
            if (string.IsNullOrEmpty(wwwroot))
            {
                throw new ArgumentNullException(nameof(wwwroot));
            }
            if (!Directory.Exists(wwwroot))
            {
                throw new DirectoryNotFoundException(wwwroot);
            }
            WWWRoot = Path.GetFullPath(wwwroot);
            RequestPath = requestPath;
            Url = url;
            devcertPath = Path.GetFullPath("assets/testcert.pfx");
            if (!File.Exists(devcertPath))
                throw new Exception("testcert.pfx not found. Cannot create static server");
            SolutionRoot = FindSolutionRoot();
        }

        private static string FindSolutionRoot()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                if (Directory.GetFiles(dir, "*.slnx").Length > 0 || Directory.GetFiles(dir, "*.sln").Length > 0)
                    return dir;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return AppContext.BaseDirectory;
        }
        public bool Running => runningTask?.IsCompleted == false;
        public void Start()
        {
            runningTask ??= StartAsync();
        }
        private async Task StartAsync()
        {
            try
            {
                var builder = WebApplication.CreateBuilder();
                var port = new Uri(Url).Port;

                // This wipes out Console, Debug, and any other default providers
                builder.Logging.ClearProviders();

                // Configure static file serving
                builder.WebHost.UseKestrel();
                builder.WebHost.ConfigureKestrel(serverOptions =>
                {
                    serverOptions.Listen(IPAddress.Loopback, port, listenOptions =>
                    {
                        listenOptions.UseHttps(devcertPath, "unittests");
                    });
                });
                // Use the current directory as the web root
                builder.Environment.WebRootPath = WWWRoot;
                builder.WebHost.UseUrls(Url);

                app = builder.Build();

                // (optional) add headers that enables: window.crossOriginIsolated == true
                app.Use(async (context, next) =>
                {
                    context.Response.Headers["Cross-Origin-Embedder-Policy"] = "credentialless";
                    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
                    await next();
                });

                // Filesystem API: read/write files relative to solution root.
                // Blazor WASM apps running in PlaywrightMultiTest can use this to
                // write debug dumps, log files, and test artifacts to disk.
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments("/_fs", out var remaining) && remaining.HasValue)
                    {
                        var relativePath = remaining.Value.TrimStart('/');
                        if (string.IsNullOrEmpty(relativePath))
                        {
                            context.Response.StatusCode = 400;
                            await context.Response.WriteAsync("Path required");
                            return;
                        }
                        var fullPath = Path.GetFullPath(Path.Combine(SolutionRoot, relativePath));
                        if (!fullPath.StartsWith(SolutionRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            context.Response.StatusCode = 403;
                            await context.Response.WriteAsync("Path outside solution root");
                            return;
                        }
                        if (context.Request.Method == "GET")
                        {
                            if (!File.Exists(fullPath))
                            {
                                context.Response.StatusCode = 404;
                                await context.Response.WriteAsync("Not found");
                                return;
                            }
                            context.Response.ContentType = "application/octet-stream";
                            await context.Response.SendFileAsync(fullPath);
                        }
                        else if (context.Request.Method == "PUT")
                        {
                            var dir = Path.GetDirectoryName(fullPath);
                            if (dir != null) Directory.CreateDirectory(dir);
                            using var fs = new FileStream(fullPath, FileMode.Create);
                            await context.Request.Body.CopyToAsync(fs);
                            await context.Response.WriteAsync("OK");
                        }
                        else if (context.Request.Method == "POST")
                        {
                            var dir = Path.GetDirectoryName(fullPath);
                            if (dir != null) Directory.CreateDirectory(dir);
                            using var fs = new FileStream(fullPath, FileMode.Append);
                            await context.Request.Body.CopyToAsync(fs);
                            await context.Response.WriteAsync("OK");
                        }
                        else
                        {
                            context.Response.StatusCode = 405;
                            await context.Response.WriteAsync("Method not allowed");
                        }
                        return;
                    }
                    await next();
                });

                // enable 404 fallback to default root
                app.UseStatusCodePagesWithReExecute(string.IsNullOrEmpty(RequestPath) ? "/" : RequestPath);

                // enable index.html fallback
                app.UseDefaultFiles(new DefaultFilesOptions
                {
                    FileProvider = new PhysicalFileProvider(WWWRoot),
                    RequestPath = RequestPath
                });
                // enable unknown file types (required)
                app.UseFileServer(new FileServerOptions
                {
                    FileProvider = new PhysicalFileProvider(WWWRoot),
                    RequestPath = RequestPath,
                    EnableDirectoryBrowsing = false, // Optional: allows browsing directory listings
                    StaticFileOptions = {
                        ServeUnknownFileTypes = true, // Crucial: serves all file types, even those without known MIME types
                        DefaultContentType = "application/octet-stream" // Optional: default MIME type for unknown files
                    }
                });
                // start hosting
                await app.RunAsync();
            }
            finally
            {
                app = null;
                runningTask = null;
            }
        }
        public async Task Stop()
        {
            if (app == null || runningTask == null) return;
            try
            {
                await app.StopAsync();
            }
            catch { }
            await app.DisposeAsync();
            if (runningTask != null)
            {
                try
                {
                    await runningTask;
                }
                catch { }
            }
        }
    }
}
