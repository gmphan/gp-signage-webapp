using GpSignageWebapp.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace GpSignageWebapp.Injectors
{
    /// <summary>Registers the local file upload storage service.</summary>
    public class StorageInjector : IInjector
    {
        public void InjectServices(IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<IStorageService, LocalStorageService>();
        }
    }
}
