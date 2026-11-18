using Microsoft.Extensions.DependencyInjection;
using GpSignageWebapp.Hubs;

namespace GpSignageWebapp.Injectors
{
    /// <summary>
    /// Registers SignalR services. The hub endpoint itself is mapped in Program.cs
    /// (app.MapHub&lt;SignageHub&gt;).
    /// </summary>
    public class SignalRInjector : IInjector
    {
        public void InjectServices(IServiceCollection services, IConfiguration configuration)
        {
            services.AddSignalR();
        }
    }
}
