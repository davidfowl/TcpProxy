
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Microsoft.AspNetCore.Hosting
{
    public static class ServiceCollectionExtensions
    {
        public static IWebHostBuilder UseDelegatedTransport(this IWebHostBuilder hostBuilder, int? port = null)
        {
            var localPort = port ?? int.Parse(Environment.GetEnvironmentVariable("PORT")!);

            return hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<IConnectionListenerFactory, DelegatedConnectionListenerFactory>();
                services.Configure<KestrelServerOptions>(o =>
                {
                    o.Listen(IPAddress.Loopback, localPort);
                });
            });
        }
    }
}
