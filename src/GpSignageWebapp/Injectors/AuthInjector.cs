using GpSignageWebapp.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace GpSignageWebapp.Injectors
{
    /// <summary>Registers cookie authentication + authorization for the admin surface.</summary>
    public class AuthInjector : IInjector
    {
        public void InjectServices(IServiceCollection services, IConfiguration configuration)
        {
            services.AddSignageAuth();
        }
    }
}
