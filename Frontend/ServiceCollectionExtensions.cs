
using Microsoft.AspNetCore.Connections;

namespace Microsoft.AspNetCore.Hosting
{
    public static class ServiceCollectionExtensions
    {
        public static IWebHostBuilder UseDelegatingTransport(this IWebHostBuilder hostBuilder, Action<BackendOptions> configure)
        {
            return hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<IConnectionListenerFactory, DelegatingConnectionListenerFactory>();
                services.Configure(configure);
            });
        }
    }
}
